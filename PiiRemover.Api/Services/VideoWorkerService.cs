using PiiRemover.Core.Engines;
using PiiRemover.Data.Repositories;

namespace PiiRemover.Api.Services;

/// <summary>
/// Background service that polls the VideoJobs table and processes queued jobs with FFmpeg.
/// Optionally redacts PII from the transcript text before burning it as a video overlay.
/// Sends WebSocket events to the client when job status changes.
/// </summary>
public class VideoWorkerService : BackgroundService
{
    private readonly IVideoJobRepository _jobs;
    private readonly VideoProcessingService _processor;
    private readonly VideoWebSocketManager _wsManager;
    private readonly VideoSettings _settings;
    private readonly RedactionOrchestrator _orchestrator;
    private readonly FieldsCache _fieldsCache;
    private readonly ILogger<VideoWorkerService> _logger;

    public VideoWorkerService(
        IVideoJobRepository jobs,
        VideoProcessingService processor,
        VideoWebSocketManager wsManager,
        VideoSettings settings,
        RedactionOrchestrator orchestrator,
        FieldsCache fieldsCache,
        ILogger<VideoWorkerService> logger)
    {
        _jobs         = jobs;
        _processor    = processor;
        _wsManager    = wsManager;
        _settings     = settings;
        _orchestrator = orchestrator;
        _fieldsCache  = fieldsCache;
        _logger       = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await TickAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogError(ex, "VideoWorkerService tick error"); }

            var pollSecs = await _settings.GetPollSecsAsync();
            await Task.Delay(TimeSpan.FromSeconds(pollSecs), ct);
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var batchSize = await _settings.GetBatchSizeAsync();
        var queued    = (await _jobs.GetQueuedAsync(batchSize)).ToList();
        if (queued.Count == 0) return;

        var storagePath = await _settings.GetStoragePathAsync();
        var deleteInput = await _settings.GetDeleteInputAsync();

        var tasks = queued.Select(job => ProcessJobAsync(job, storagePath, deleteInput, ct));
        await Task.WhenAll(tasks);
    }

    private async Task ProcessJobAsync(
        PiiRemover.Data.Models.VideoJob job, string storagePath, bool deleteInput, CancellationToken ct)
    {
        _logger.LogInformation("Video job {Id} starting", job.Id);

        var startedAt = DateTime.UtcNow.ToString("o");
        await _jobs.UpdateStatusAsync(job.Id, "processing", startedAt: startedAt);
        await _wsManager.SendToClientAsync(job.ClientId,
            new { type = "job.processing", jobId = job.Id, payload = new { } });

        try
        {
            // Optionally redact PII from transcript text before burning it as overlay
            if (!string.IsNullOrWhiteSpace(job.TranscriptText))
            {
                var piiEnabled = await _settings.GetPiiRedactionEnabledAsync();
                if (piiEnabled)
                {
                    var fields  = await _fieldsCache.GetFieldsAsync(job.ClientId);
                    var redacted = _orchestrator.Redact(job.TranscriptText, fields);
                    job.TranscriptText = redacted.RedactedText;
                    _logger.LogInformation("Video job {Id}: transcript PII-redacted ({Before}→{After} chars)",
                        job.Id, job.TranscriptText.Length, redacted.RedactedText.Length);
                }
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            await _processor.ProcessAsync(job, storagePath, ct);
            sw.Stop();

            var completedAt = DateTime.UtcNow.ToString("o");
            await _jobs.UpdateStatusAsync(job.Id, "completed",
                outputPath: job.OutputPath, durationMs: sw.ElapsedMilliseconds, completedAt: completedAt);

            await _wsManager.SendToClientAsync(job.ClientId, new
            {
                type = "job.completed",
                jobId = job.Id,
                payload = new
                {
                    downloadUrl = $"/api/v1/video/download/{job.Id}",
                    durationMs  = sw.ElapsedMilliseconds
                }
            });

            _logger.LogInformation("Video job {Id} completed in {Ms}ms", job.Id, sw.ElapsedMilliseconds);

            if (deleteInput)
            {
                TryDelete(job.VideoPath);
                TryDelete(job.AudioPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Video job {Id} failed", job.Id);
            await _jobs.UpdateStatusAsync(job.Id, "failed", errorMsg: ex.Message);
            await _wsManager.SendToClientAsync(job.ClientId, new
            {
                type = "job.failed",
                jobId = job.Id,
                payload = new { error = ex.Message }
            });
        }
    }

    private static void TryDelete(string? path)
    {
        if (path is null) return;
        try { File.Delete(path); } catch { }
    }
}
