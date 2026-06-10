using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using SysFile = System.IO.File;
using PiiRemover.Api.Extractors;
using PiiRemover.Api.Services;
using PiiRemover.Core.Engines;
using PiiRemover.Core.Logging;
using PiiRemover.Data.Repositories;

namespace PiiRemover.Api.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class RedactController : ControllerBase
{
    private readonly ExtractorFactory _extractors;
    private readonly RedactionOrchestrator _orchestrator;
    private readonly FieldsCache _fieldsCache;
    private readonly IQuotaRepository _quota;
    private readonly ILogRepository _logs;
    private readonly IPiiLogger _logger;
    private readonly ImageRedactor _imageRedactor;

    public RedactController(
        ExtractorFactory extractors,
        RedactionOrchestrator orchestrator,
        FieldsCache fieldsCache,
        IQuotaRepository quota,
        ILogRepository logs,
        IPiiLogger logger,
        ImageRedactor imageRedactor)
    {
        _extractors   = extractors;
        _orchestrator = orchestrator;
        _fieldsCache  = fieldsCache;
        _quota        = quota;
        _logs         = logs;
        _logger       = logger;
        _imageRedactor = imageRedactor;
    }

    [HttpPost("redact")]
    [DisableRequestSizeLimit]
    [RequestFormLimits(MultipartBodyLengthLimit = long.MaxValue)]
    public async Task<IActionResult> Redact(IFormFile file, CancellationToken ct)
    {
        var clientId   = HttpContext.Items["ClientId"]   as int?;
        var clientName = HttpContext.Items["ClientName"] as string;
        var tempPath   = Path.GetTempFileName();
        var sw         = Stopwatch.StartNew();

        try
        {
            // Stream upload directly to disk — file content is never buffered in RAM
            await using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write,
                             FileShare.None, bufferSize: 81920, useAsync: true))
                await file.CopyToAsync(fs, ct);

            var mimeType = file.ContentType ?? "application/octet-stream";
            if (!TryGetExtractor(mimeType, out var extractor, out var badRequest))
                return badRequest!;

            // Extractor reads directly from tempPath — no additional copy
            var text = await extractor!.ExtractAsync(tempPath, ct);

            var activeFields = await _fieldsCache.GetFieldsAsync(clientId);
            var result = _orchestrator.Redact(text, activeFields);

            sw.Stop();
            await _quota.IncrementAsync();

            var fieldsHit = result.Matches.Select(m => m.FieldName).Distinct().ToArray();
            _logger.LogRequest(new PiiRequestLog
            {
                Operation     = "Redact",
                ClientId      = clientId,
                ClientName    = clientName,
                FileName      = file.FileName,
                FileSizeBytes = file.Length,
                MimeType      = mimeType,
                ExtractorUsed = extractor!.GetType().Name,
                DurationMs    = result.DurationMs,
                MatchCount    = result.Matches.Count,
                FieldsHit     = fieldsHit,
                ExtractedText = text,
                RedactedText  = result.RedactedText
            });
            await _logs.InsertAsync(new RequestLogEntry
            {
                ClientId   = clientId,
                FileName   = file.FileName,
                FileSizeKb = (int)(file.Length / 1024),
                DurationMs = result.DurationMs,
                FieldsHit  = fieldsHit.Length > 0 ? System.Text.Json.JsonSerializer.Serialize(fieldsHit) : null,
                ErrorMsg   = null
            });

            return Ok(new
            {
                redactedText = result.RedactedText,
                matchCount   = result.Matches.Count,
                fieldsHit    = result.Matches.Select(m => m.FieldName).Distinct().ToArray(),
                durationMs   = result.DurationMs,
                matches      = result.Matches
                    .OrderBy(m => m.StartIndex)
                    .Select(m => new
                    {
                        startIndex  = m.StartIndex,
                        length      = m.Length,
                        fieldName   = m.FieldName,
                        replacement = m.Replacement,
                        matchedText = m.MatchedText
                    })
            });
        }
        catch (OperationCanceledException) { return StatusCode(499); }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError("Redact", clientName, ex, file.FileName);
            return StatusCode(500, new { error = "Processing failed.", detail = ex.Message });
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    /// <summary>
    /// POST /api/v1/redact/redact-image
    /// Accepts an image file, runs OCR, and paints black rectangles over EVERY detected
    /// word — no PII filtering. Returns the scrubbed image as JPEG bytes.
    ///
    /// Response headers:
    ///   X-Word-Count   — number of OCR words painted
    ///   X-Duration-Ms  — total processing time in milliseconds
    /// </summary>
    [HttpPost("redact-image")]
    [DisableRequestSizeLimit]
    [RequestFormLimits(MultipartBodyLengthLimit = long.MaxValue)]
    public async Task<IActionResult> RedactImage(IFormFile file, CancellationToken ct)
    {
        var clientId   = HttpContext.Items["ClientId"]   as int?;
        var clientName = HttpContext.Items["ClientName"] as string;
        var sw         = Stopwatch.StartNew();

        try
        {
            if (file is null || file.Length == 0)
                return BadRequest(new { error = "No file provided." });

            var mime = (file.ContentType ?? string.Empty).ToLowerInvariant();
            if (!mime.StartsWith("image/"))
                return BadRequest(new { error = $"Unsupported MIME type: {file.ContentType}. Upload an image (PNG, JPEG, TIFF, BMP)." });

            var imageBytes = new byte[file.Length];
            using (var ms  = new MemoryStream(imageBytes))
                await file.CopyToAsync(ms, ct);

            var result = await _imageRedactor.RedactAllTextAsync(imageBytes, ct);

            sw.Stop();
            await _quota.IncrementAsync();

            _logger.LogRequest(new PiiRequestLog
            {
                Operation     = "RedactImage",
                ClientId      = clientId,
                ClientName    = clientName,
                FileName      = file.FileName,
                FileSizeBytes = file.Length,
                MimeType      = mime,
                ExtractorUsed = "OcrExtractor",
                DurationMs    = result.DurationMs,
                MatchCount    = result.MatchCount,
                FieldsHit     = result.FieldsHit,
                ExtractedText = result.OcrText,
                RedactedText  = $"[image — {result.MatchCount} words painted]"
            });
            await _logs.InsertAsync(new RequestLogEntry
            {
                ClientId   = clientId,
                FileName   = file.FileName,
                FileSizeKb = (int)(file.Length / 1024),
                DurationMs = result.DurationMs,
                FieldsHit  = "[\"(all text)\"]",
                ErrorMsg   = null
            });

            Response.Headers["X-Word-Count"]  = result.MatchCount.ToString();
            Response.Headers["X-Duration-Ms"] = result.DurationMs.ToString();

            return File(result.RedactedImage, "image/jpeg",
                        Path.GetFileNameWithoutExtension(file.FileName) + "_redacted.jpg");
        }
        catch (OperationCanceledException) { return StatusCode(499); }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError("RedactImage", clientName, ex, file?.FileName);
            return StatusCode(500, new { error = "Image text-scrub failed.", detail = ex.Message });
        }
    }

    private bool TryGetExtractor(string mimeType, out Core.Extractors.ITextExtractor? extractor, out IActionResult? error)
    {
        try { extractor = _extractors.GetExtractor(mimeType); error = null; return true; }
        catch (NotSupportedException ex) { extractor = null; error = BadRequest(new { error = ex.Message }); return false; }
    }

    private static void TryDelete(string path)
    {
        try { if (SysFile.Exists(path)) SysFile.Delete(path); } catch { /* best-effort */ }
    }
}
