using PiiRemover.Core.Logging;
using PiiRemover.Data.Repositories;

namespace PiiRemover.Api.Services;

// Runs once on startup then every 24 hours.
// Deletes RequestLogs older than LogRetentionMonths (default = 1).
// Lightweight: uses an indexed DELETE — no scan of the whole table.
public class LogCleanupService : BackgroundService
{
    private readonly ILogRepository _logs;
    private readonly IConfiguration _config;
    private readonly IPiiLogger _logger;
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    public LogCleanupService(ILogRepository logs, IConfiguration config, IPiiLogger logger)
    {
        _logs   = logs;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Run once on startup to clean up immediately after a restart
        await RunCleanupAsync(stoppingToken);

        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await RunCleanupAsync(stoppingToken);
    }

    private async Task RunCleanupAsync(CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return;
        try
        {
            var months  = _config.GetValue<int>("Logging:RetentionMonths", 1);
            var cutoff  = DateTime.UtcNow.AddMonths(-Math.Max(1, months));
            var deleted = await _logs.DeleteOlderThanAsync(cutoff);
            if (deleted > 0)
                _logger.LogInfo($"LogCleanup: deleted {deleted} request log(s) older than {cutoff:yyyy-MM-dd} (retention={months} month(s)).");
        }
        catch (Exception ex)
        {
            _logger.LogError("LogCleanup", null, ex);
        }
    }
}
