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
    public async Task<IActionResult> OnPostAddPatternAsync(int fieldId, string patternType, string pattern)
    {
        if (!Enum.TryParse<PatternType>(patternType, true, out var pt))
            return BadRequest(new { error = $"Unknown pattern type: {patternType}" });
        if (string.IsNullOrWhiteSpace(pattern))
            return BadRequest(new { error = "Pattern cannot be empty." });

        var id = await _fields.CreatePatternAsync(fieldId, pt, pattern.Trim(), 0);
        _fieldsCache.Invalidate();
        return new JsonResult(new { ok = true, patternId = id });
    }

    // ── Append a value to an existing ConstList pattern ───────────────────────
    public async Task<IActionResult> OnPostAppendConstListAsync(int patternId, string currentPattern, string appendValue)
    {
        if (string.IsNullOrWhiteSpace(appendValue))
            return BadRequest(new { error = "Value cannot be empty." });

        var trimmed    = appendValue.Trim();
        var newPattern = string.IsNullOrWhiteSpace(currentPattern)
            ? trimmed
            : currentPattern.TrimEnd('|') + "|" + trimmed;

        await _fields.UpdatePatternAsync(patternId, PatternType.ConstList, newPattern, 0);
        _fieldsCache.Invalidate();
        return new JsonResult(new { ok = true, newPattern });
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
    public async Task<IActionResult> OnPostCreateFieldAsync(
        string fieldName, string replaceWith, string patternType, string pattern)
    {
        if (string.IsNullOrWhiteSpace(fieldName))
            return BadRequest(new { error = "Field name cannot be empty." });
        if (!Enum.TryParse<PatternType>(patternType, true, out var pt))
            return BadRequest(new { error = $"Unknown pattern type: {patternType}" });
        if (string.IsNullOrWhiteSpace(pattern))
            return BadRequest(new { error = "Pattern cannot be empty." });

        var rw      = string.IsNullOrWhiteSpace(replaceWith) ? "████" : replaceWith.Trim();
        var fieldId = await _fields.CreateFieldAsync(null, fieldName.Trim(), rw);
        var patId   = await _fields.CreatePatternAsync(fieldId, pt, pattern.Trim(), 0);
        _fieldsCache.Invalidate();
        return new JsonResult(new { ok = true, fieldId, patternId = patId });
    }
}

public record RedactTextRequest(string Text);
