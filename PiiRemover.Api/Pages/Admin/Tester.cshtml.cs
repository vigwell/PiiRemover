using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PiiRemover.Api.Extractors;
using PiiRemover.Api.Services;
using PiiRemover.Core.Engines;
using PiiRemover.Core.Models;
using PiiRemover.Data.Repositories;
using System.Diagnostics;
using SysFile = System.IO.File;

namespace PiiRemover.Api.Pages.Admin;

[Authorize]
[RequestFormLimits(MultipartBodyLengthLimit = long.MaxValue)]
[DisableRequestSizeLimit]
public class TesterModel : AdminPageModel
{
    private readonly ExtractorFactory _extractors;
    private readonly RedactionOrchestrator _orchestrator;
    private readonly IFieldRepository _fields;
    private readonly FieldsCache _fieldsCache;

    public TesterModel(ExtractorFactory extractors, RedactionOrchestrator orchestrator,
                       IFieldRepository fields, FieldsCache fieldsCache)
    {
        _extractors   = extractors;
        _orchestrator = orchestrator;
        _fields       = fields;
        _fieldsCache  = fieldsCache;
    }

    public void OnGet() { }

    // ── Analyze a single file — OCR + Redact run concurrently ────────────────
    public async Task<IActionResult> OnPostAnalyzeAsync(IFormFile file, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return BadRequest(new { error = "No file provided." });

        var mimeType = file.ContentType ?? "application/octet-stream";
        Core.Extractors.ITextExtractor extractor;
        try   { extractor = _extractors.GetExtractor(mimeType); }
        catch (NotSupportedException ex) { return BadRequest(new { error = ex.Message }); }

        var bytes = new byte[file.Length];
        await using (var ms = new MemoryStream(bytes))
            await file.CopyToAsync(ms, ct);

        var ocrPath    = Path.GetTempFileName();
        var redactPath = Path.GetTempFileName();
        try
        {
            await Task.WhenAll(
                SysFile.WriteAllBytesAsync(ocrPath,    bytes, ct),
                SysFile.WriteAllBytesAsync(redactPath, bytes, ct));

            var ocrTask = Task.Run(async () =>
            {
                var sw   = Stopwatch.StartNew();
                var text = await extractor.ExtractAsync(ocrPath, ct);
                sw.Stop();
                return new { text, charCount = text.Length, durationMs = sw.ElapsedMilliseconds,
                             extractorUsed = extractor.GetType().Name };
            }, ct);

            var redactTask = Task.Run(async () =>
            {
                var sw           = Stopwatch.StartNew();
                var text         = await extractor.ExtractAsync(redactPath, ct);
                var activeFields = await _fieldsCache.GetFieldsAsync(null);
                var result       = _orchestrator.Redact(text, activeFields);
                sw.Stop();
                return new
                {
                    redactedText = result.RedactedText,
                    matchCount   = result.Matches.Count,
                    fieldsHit    = result.Matches.Select(m => m.FieldName).Distinct().ToArray(),
                    durationMs   = sw.ElapsedMilliseconds,
                    matches      = result.Matches
                        .OrderBy(m => m.StartIndex)
                        .Select(m => new
                        {
                            startIndex  = m.StartIndex,
                            length      = m.Length,
                            fieldName   = m.FieldName,
                            replacement = m.Replacement,
                            matchedText = m.MatchedText
                        }).ToArray()
                };
            }, ct);

            await Task.WhenAll(ocrTask, redactTask);
            return new JsonResult(new { fileName = file.FileName, fileSize = file.Length,
                                        ocr = ocrTask.Result, redact = redactTask.Result });
        }
        catch (OperationCanceledException) { return StatusCode(499); }
        catch (Exception ex) { return StatusCode(500, new { error = "Processing failed.", detail = ex.Message }); }
        finally
        {
            try { if (SysFile.Exists(ocrPath))    SysFile.Delete(ocrPath);    } catch { }
            try { if (SysFile.Exists(redactPath)) SysFile.Delete(redactPath); } catch { }
        }
    }

    // ── Return all global fields with their patterns (for Add-to-PII dialog) ─
    public async Task<IActionResult> OnGetFieldsAsync()
    {
        var fields = await _fieldsCache.GetFieldsAsync(null);
        return new JsonResult(fields.Select(f => new
        {
            id          = f.Id,
            fieldName   = f.FieldName,
            replaceWith = f.ReplaceWith,
            isActive    = f.IsActive,
            isPreserve  = f.IsPreserve,
            patterns    = f.Patterns.Select(p => new
            {
                id       = p.Id,
                type     = p.PatternType.ToString(),
                pattern  = p.Pattern,
                priority = p.Priority
            }).ToArray()
        }).ToArray());
    }

    // ── Add a new pattern to an existing field ────────────────────────────────
    // NOTE: called via JSON fetch (apiPost), so [FromBody] is required.
    public async Task<IActionResult> OnPostAddPatternAsync([FromBody] AddPatternRequest req)
    {
        if (!Enum.TryParse<PatternType>(req.PatternType, true, out var pt))
            return BadRequest(new { error = $"Unknown pattern type: {req.PatternType}" });
        if (string.IsNullOrWhiteSpace(req.Pattern))
            return BadRequest(new { error = "Pattern cannot be empty." });

        var id = await _fields.CreatePatternAsync(req.FieldId, pt, req.Pattern.Trim(), 100);
        _fieldsCache.Invalidate();
        return new JsonResult(new { ok = true, patternId = id });
    }

    // ── Append a value to an existing ConstList pattern ───────────────────────
    public async Task<IActionResult> OnPostAppendConstListAsync([FromBody] AppendConstListRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.AppendValue))
            return BadRequest(new { error = "Value cannot be empty." });

        var trimmed    = req.AppendValue.Trim();
        var newPattern = string.IsNullOrWhiteSpace(req.CurrentPattern)
            ? trimmed
            : req.CurrentPattern.TrimEnd('|') + "|" + trimmed;

        await _fields.UpdatePatternAsync(req.PatternId, PatternType.ConstList, newPattern, 0);
        _fieldsCache.Invalidate();
        return new JsonResult(new { ok = true, newPattern });
    }

    // ── OCR only — extract text, skip redaction entirely ─────────────────────
    public async Task<IActionResult> OnPostOcrOnlyAsync(IFormFile file, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return BadRequest(new { error = "No file provided." });

        Core.Extractors.ITextExtractor extractor;
        try   { extractor = _extractors.GetExtractor(file.ContentType ?? "application/octet-stream"); }
        catch (NotSupportedException ex) { return BadRequest(new { error = ex.Message }); }

        var tmp = Path.GetTempFileName();
        try
        {
            await using (var fs = System.IO.File.Create(tmp))
                await file.CopyToAsync(fs, ct);

            var sw   = Stopwatch.StartNew();
            var text = await extractor.ExtractAsync(tmp, ct);
            sw.Stop();

            return new JsonResult(new
            {
                fileName      = file.FileName,
                fileSize      = file.Length,
                ocr = new {
                    text,
                    charCount     = text.Length,
                    durationMs    = sw.ElapsedMilliseconds,
                    extractorUsed = extractor.GetType().Name
                }
            });
        }
        catch (OperationCanceledException) { return StatusCode(499); }
        catch (Exception ex) { return StatusCode(500, new { error = "OCR failed.", detail = ex.Message }); }
        finally { try { System.IO.File.Delete(tmp); } catch { } }
    }

    // ── Redact plain text (no file/OCR) ──────────────────────────────────────
    public async Task<IActionResult> OnPostRedactTextAsync([FromBody] RedactTextRequest req)
    {
        var text = req?.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
            return BadRequest(new { error = "No text provided." });

        var sw           = Stopwatch.StartNew();
        var activeFields = await _fieldsCache.GetFieldsAsync(null);
        var result       = _orchestrator.Redact(text, activeFields);
        sw.Stop();

        return new JsonResult(new
        {
            matchCount = result.Matches.Count,
            fieldsHit  = result.Matches.Select(m => m.FieldName).Distinct().ToArray(),
            durationMs = sw.ElapsedMilliseconds,
            matches    = result.Matches
                .OrderBy(m => m.StartIndex)
                .Select(m => new {
                    startIndex  = m.StartIndex,
                    length      = m.Length,
                    fieldName   = m.FieldName,
                    replacement = m.Replacement,
                    matchedText = m.MatchedText
                }).ToArray()
        });
    }

    // ── Create a new field with its first pattern ──────────────────────────────
    public async Task<IActionResult> OnPostCreateFieldAsync([FromBody] CreateFieldRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.FieldName))
            return BadRequest(new { error = "Field name cannot be empty." });
        if (!Enum.TryParse<PatternType>(req.PatternType, true, out var pt))
            return BadRequest(new { error = $"Unknown pattern type: {req.PatternType}" });
        if (string.IsNullOrWhiteSpace(req.Pattern))
            return BadRequest(new { error = "Pattern cannot be empty." });

        var rw       = string.IsNullOrWhiteSpace(req.ReplaceWith) ? (req.IsPreserve ? "—" : "████") : req.ReplaceWith.Trim();
        var patPri   = req.IsPreserve ? 999 : 100;
        var fieldId  = await _fields.CreateFieldAsync(null, req.FieldName.Trim(), rw, req.IsPreserve);
        var patId    = await _fields.CreatePatternAsync(fieldId, pt, req.Pattern.Trim(), patPri);
        _fieldsCache.Invalidate();
        return new JsonResult(new { ok = true, fieldId, patternId = patId });
    }

    /// <summary>
    /// Appends a single value to the first ConstList pattern found on the given field.
    /// If no ConstList exists yet, creates a new one.
    /// Used by the "Add and combine with existing ConstList" checkbox in the Preserve modal.
    /// </summary>
    public async Task<IActionResult> OnPostAppendToConstListAsync([FromBody] AppendToConstListRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Value))
            return BadRequest(new { error = "Value cannot be empty." });

        var allFields = await _fields.GetAllFieldsAsync();
        var field     = allFields.FirstOrDefault(f => f.Id == req.FieldId);
        if (field is null)
            return BadRequest(new { error = $"Field {req.FieldId} not found." });

        var existing = field.Patterns.FirstOrDefault(p => p.PatternType == PatternType.ConstList);

        if (existing is not null)
        {
            // Append the new value to the existing ConstList (deduplicated)
            var terms = existing.Pattern
                .Split('|', StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var newTerm = req.Value.Trim();
            if (terms.Add(newTerm))   // Add returns false if already present
            {
                var updated = string.Join("|", terms.OrderBy(t => t, StringComparer.OrdinalIgnoreCase));
                await _fields.UpdatePatternAsync(existing.Id, PatternType.ConstList, updated, existing.Priority);
            }
            _fieldsCache.Invalidate();
            return new JsonResult(new { ok = true, patternId = existing.Id, combined = true });
        }
        else
        {
            // No ConstList exists yet — create one
            var patId = await _fields.CreatePatternAsync(req.FieldId, PatternType.ConstList,
                                                          req.Value.Trim(), priority: 999);
            _fieldsCache.Invalidate();
            return new JsonResult(new { ok = true, patternId = patId, combined = false });
        }
    }
}

public record RedactTextRequest(string Text);
public record AddPatternRequest(int FieldId, string PatternType, string Pattern);
public record AppendConstListRequest(int PatternId, string CurrentPattern, string AppendValue);
public record AppendToConstListRequest(int FieldId, string Value);
public record CreateFieldRequest(
    string FieldName, string ReplaceWith, string PatternType, string Pattern,
    bool IsPreserve = false);
