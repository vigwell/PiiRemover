using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using SysFile = System.IO.File;
using PiiRemover.Api.Extractors;
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
    private readonly IFieldRepository _fields;
    private readonly IQuotaRepository _quota;
    private readonly ILogRepository _logs;
    private readonly IPiiLogger _logger;

    public RedactController(
        ExtractorFactory extractors,
        RedactionOrchestrator orchestrator,
        IFieldRepository fields,
        IQuotaRepository quota,
        ILogRepository logs,
        IPiiLogger logger)
    {
        _extractors   = extractors;
        _orchestrator = orchestrator;
        _fields       = fields;
        _quota        = quota;
        _logs         = logs;
        _logger       = logger;
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

            var activeFields = await _fields.GetFieldsWithPatternsAsync(clientId);
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
                fieldsHit    = result.Matches.Select(m => m.FieldName).Distinct(),
                durationMs   = result.DurationMs
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
