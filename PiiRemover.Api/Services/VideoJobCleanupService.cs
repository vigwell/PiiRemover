using PiiRemover.Data.Repositories;

namespace PiiRemover.Api.Services;

/// <summary>
/// Hourly background service that deletes orphaned input files for completed or failed
/// VideoJobs older than the configured threshold (video:cleanupInputOlderThanHours).
/// Output MP4 files are kept until the user downloads them.
/// </summary>
public class VideoJobCleanupService : BackgroundService
{
    private readonly IVideoJobRepository _jobs;
    private readonly VideoSettings _settings;
    private readonly ILogger<VideoJobCleanupService> _logger;

    public VideoJobCleanupService(
        IVideoJobRepository jobs,
        VideoSettings settings,
        ILogger<VideoJobCleanupService> logger)
    {
        _jobs     = jobs;
        _settings = settings;
        _logger   = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromMinutes(1), ct);

        while (!ct.IsCancellationRequested)
        {
            try { await CleanupAsync(); }
            catch (Exception ex) { _logger.LogWarning(ex, "VideoJobCleanupService error"); }

            await Task.Delay(TimeSpan.FromHours(1), ct);
        }
    }

    private async Task CleanupAsync()
    {
        var hours = await _settings.GetCleanupOlderThanHoursAsync();
        if (hours <= 0) return;

        var cutoff = DateTime.UtcNow.AddHours(-hours);
        var jobs   = await _jobs.GetOldCompletedAsync(cutoff);

        var deleted = 0;
        foreach (var job in jobs)
        {
            deleted += TryDelete(job.VideoPath);
            deleted += TryDelete(job.AudioPath);
        }

        if (deleted > 0)
            _logger.LogInformation("VideoJobCleanupService: deleted {Count} orphaned input files (older than {H}h)",
                deleted, hours);
    }

    private static int TryDelete(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return 0;
        try { File.Delete(path); return 1; }
        catch { return 0; }
    }
}
