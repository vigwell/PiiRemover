using Microsoft.AspNetCore.Mvc;
using PiiRemover.Api.Services;
using PiiRemover.Core.Licensing;
using PiiRemover.Data.Models;
using PiiRemover.Data.Repositories;

namespace PiiRemover.Api.Controllers;

[ApiController]
[Route("api/v1/video")]
public class VideoController : ControllerBase
{
    private readonly IVideoJobRepository _jobs;
    private readonly VideoWebSocketManager _wsManager;
    private readonly VideoSettings _settings;
    private readonly LicenseInfo _license;

    public VideoController(
        IVideoJobRepository jobs,
        VideoWebSocketManager wsManager,
        VideoSettings settings,
        LicenseInfo license)
    {
        _jobs      = jobs;
        _wsManager = wsManager;
        _settings  = settings;
        _license   = license;
    }

    // ── POST /api/v1/video/upload ─────────────────────────────────────────────

    [HttpPost("upload")]
    [DisableRequestSizeLimit]
    [RequestFormLimits(MultipartBodyLengthLimit = long.MaxValue)]
    public async Task<IActionResult> Upload(
        IFormFile video,
        IFormFile? audio,
        [FromForm] string? transcriptText,
        [FromForm] string? transcriptSegments,
        [FromForm] bool createCaptions = false,
        [FromForm] bool redactPii = false,
        [FromForm] bool redactAudioPii = false,
        CancellationToken ct = default)
    {
        if (!CheckLicense(out var err)) return err!;
        var clientId = GetClientId();

        var maxMb = await _settings.GetMaxFileMbAsync();
        if (video.Length > (long)maxMb * 1024 * 1024)
            return BadRequest(new { error = $"Video file exceeds maximum of {maxMb} MB." });
        if (audio is not null && audio.Length > (long)maxMb * 1024 * 1024)
            return BadRequest(new { error = $"Audio file exceeds maximum of {maxMb} MB." });

        var storagePath = await _settings.GetStoragePathAsync();
        var inputPath   = Path.Combine(storagePath, "input");
        var outputPath  = Path.Combine(storagePath, "output");
        Directory.CreateDirectory(inputPath);
        Directory.CreateDirectory(outputPath);

        var jobId = Guid.NewGuid().ToString("N");

        var videoExt  = Path.GetExtension(video.FileName).ToLowerInvariant().TrimStart('.') is var ve && ve.Length > 0 ? ve : "webm";
        var videoFile = Path.Combine(inputPath, $"{jobId}_video.{videoExt}");
        await using (var fs = new FileStream(videoFile, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            await video.CopyToAsync(fs, ct);

        string? audioFile = null;
        if (audio is not null)
        {
            var audioExt = Path.GetExtension(audio.FileName).ToLowerInvariant().TrimStart('.') is var ae && ae.Length > 0 ? ae : "webm";
            audioFile = Path.Combine(inputPath, $"{jobId}_audio.{audioExt}");
            await using var fs = new FileStream(audioFile, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
            await audio.CopyToAsync(fs, ct);
        }

        var job = new VideoJob
        {
            Id             = jobId,
            ClientId       = clientId == 0 ? null : clientId,
            Status         = "queued",
            VideoPath      = videoFile,
            AudioPath      = audioFile,
            OutputPath     = Path.Combine(outputPath, $"{jobId}.mp4"),
            VideoName      = video.FileName,
            AudioName      = audio?.FileName,
            TranscriptText     = transcriptText,
            TranscriptSegments = transcriptSegments,
            CreateCaptions     = createCaptions,
            RedactPii      = createCaptions && redactPii,   // redactPii only meaningful when captions are created
            RedactAudioPii = redactAudioPii,
        };

        await _jobs.InsertAsync(job);

        await _wsManager.SendToClientAsync(clientId,
            new { type = "job.queued", jobId, payload = new { } });

        return Ok(new { jobId });
    }

    // ── GET /api/v1/video/captions/{jobId} ───────────────────────────────────

    [HttpGet("captions/{jobId}")]
    public async Task<IActionResult> Captions(string jobId)
    {
        if (!CheckLicense(out var err)) return err!;
        var clientId = GetClientId();

        var job = await _jobs.GetAsync(jobId);
        if (job is null)                                return NotFound(new { error = "Job not found." });
        if (clientId != 0 && job.ClientId != clientId) return Forbid();
        if (job.Status != "completed")                  return StatusCode(425, new { error = "Job not yet completed." });

        var vttPath = Path.ChangeExtension(job.OutputPath, ".vtt");
        if (!System.IO.File.Exists(vttPath))
            return NotFound(new { error = "No captions file — job was processed without a transcript." });

        return PhysicalFile(vttPath, "text/vtt", $"captions_{jobId}.vtt");
    }

    // ── GET /api/v1/video/download/{jobId} ────────────────────────────────────

    [HttpGet("download/{jobId}")]
    public async Task<IActionResult> Download(string jobId, CancellationToken ct)
    {
        if (!CheckLicense(out var err)) return err!;
        var clientId = GetClientId();

        var job = await _jobs.GetAsync(jobId);
        if (job is null)                       return NotFound(new { error = "Job not found." });
        if (clientId != 0 && job.ClientId != clientId) return Forbid();
        if (job.Status != "completed")         return StatusCode(425, new { error = "Job not yet completed.", status = job.Status });
        if (!System.IO.File.Exists(job.OutputPath)) return NotFound(new { error = "Output file not found." });

        return PhysicalFile(job.OutputPath!, "video/mp4", $"output_{jobId}.mp4", enableRangeProcessing: true);
    }

    // ── GET /api/v1/video/status/{jobId} ─────────────────────────────────────

    [HttpGet("status/{jobId}")]
    public async Task<IActionResult> Status(string jobId)
    {
        if (!CheckLicense(out var err)) return err!;
        var clientId = GetClientId();

        var job = await _jobs.GetAsync(jobId);
        if (job is null)                                    return NotFound(new { error = "Job not found." });
        if (clientId != 0 && job.ClientId != clientId)     return Forbid();

        return Ok(new
        {
            jobId       = job.Id,
            status      = job.Status,
            createdAt   = job.CreatedAt,
            startedAt   = job.StartedAt,
            completedAt = job.CompletedAt,
            durationMs  = job.DurationMs,
            errorMsg    = job.ErrorMsg,
            downloadUrl = job.Status == "completed" ? $"/api/v1/video/download/{jobId}" : null
        });
    }

    // ── POST /api/v1/video/ws-token ───────────────────────────────────────────

    [HttpPost("ws-token")]
    public async Task<IActionResult> WsToken()
    {
        if (!CheckLicense(out var err)) return err!;
        var clientId = GetClientId();

        var expiryMins = await _settings.GetWsTokenExpiryAsync();
        var token      = _wsManager.IssueToken(clientId, expiryMins);
        var base_      = Request.PathBase.ToString().TrimEnd('/');
        var wsPath     = $"{base_}/ws/video?token={token}";

        return Ok(new { token, wsPath });
    }

    // ── GET /api/v1/video/jobs ────────────────────────────────────────────────

    [HttpGet("jobs")]
    public async Task<IActionResult> Jobs()
    {
        if (!CheckLicense(out var err)) return err!;
        var clientId = GetClientId();

        var jobs = clientId == 0
            ? await _jobs.GetAllAsync(50)
            : await _jobs.GetByClientAsync(clientId, 50);
        return Ok(jobs.Select(j => new
        {
            jobId       = j.Id,
            status      = j.Status,
            videoName   = j.VideoName,
            createdAt   = j.CreatedAt,
            completedAt = j.CompletedAt,
            durationMs  = j.DurationMs,
            errorMsg    = j.ErrorMsg,
            downloadUrl = j.Status == "completed" ? $"/api/v1/video/download/{j.Id}" : null
        }));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private int GetClientId() => HttpContext.Items["ClientId"] as int? ?? 0;

    private bool CheckLicense(out IActionResult? result)
    {
        result = null;
        if (_license.Features.Contains("VideoProcessing", StringComparer.OrdinalIgnoreCase))
            return true;
        result = StatusCode(402, new { error = "VideoProcessing feature is not included in your license." });
        return false;
    }
}
