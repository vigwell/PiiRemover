using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using SysFile = System.IO.File;
using PiiRemover.Api.Extractors;
using PiiRemover.Core.Logging;
using PiiRemover.Data.Repositories;

namespace PiiRemover.Api.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class OcrController : ControllerBase
{
    private readonly ExtractorFactory _extractors;
    private readonly IQuotaRepository _quota;
    private readonly IPiiLogger _logger;

    public OcrController(ExtractorFactory extractors, IQuotaRepository quota, IPiiLogger logger)
    {
        _extractors = extractors;
        _quota      = quota;
        _logger     = logger;
    }

    /// <summary>Extract text from a document. No PII removal — raw text only.</summary>
    [HttpPost("extract")]
    [DisableRequestSizeLimit]
    [RequestFormLimits(MultipartBodyLengthLimit = long.MaxValue)]
    public async Task<IActionResult> Extract(IFormFile file, CancellationToken ct)
    {
        var clientId   = HttpContext.Items["ClientId"]   as int?;
        var clientName = HttpContext.Items["ClientName"] as string;
        var tempPath   = Path.GetTempFileName();
        var sw         = Stopwatch.StartNew();

        try
        {
            await using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write,
                             FileShare.None, bufferSize: 81920, useAsync: true))
                await file.CopyToAsync(fs, ct);

            var mimeType = file.ContentType ?? "application/octet-stream";
            Core.Extractors.ITextExtractor extractor;
            try { extractor = _extractors.GetExtractor(mimeType); }
            catch (NotSupportedException ex) { return BadRequest(new { error = ex.Message }); }

            var text = await extractor.ExtractAsync(tempPath, ct);
            sw.Stop();
            await _quota.IncrementAsync();

            _logger.LogRequest(new PiiRequestLog
            {
                Operation     = "Ocr",
                ClientId      = clientId,
                ClientName    = clientName,
                FileName      = file.FileName,
                FileSizeBytes = file.Length,
                MimeType      = mimeType,
                ExtractorUsed = extractor.GetType().Name,
                DurationMs    = sw.ElapsedMilliseconds,
                ExtractedText = text
            });

            return Ok(new { text, charCount = text.Length, durationMs = sw.ElapsedMilliseconds });
        }
        catch (OperationCanceledException) { return StatusCode(499); }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError("Ocr", clientName, ex, file.FileName);
            return StatusCode(500, new { error = "Processing failed.", detail = ex.Message });
        }
        finally
        {
            try { if (SysFile.Exists(tempPath)) SysFile.Delete(tempPath); } catch { /* best-effort */ }
        }
    }
}
