using PiiRemover.Core.Logging;
using PiiRemover.Data.Repositories;

namespace PiiRemover.Api.Services;

/// <summary>
/// Background service that performs scheduled automatic database backups.
///
/// Scheduling logic:
///   • Checks every 15 minutes whether a backup is due.
///   • "Due" means: (now − LastBackupAt) ≥ configured interval.
///   • On first ever run (no LastBackupAt stored), waits for the first
///     15-minute tick then creates an initial backup.
///   • After each backup, prunes old files to stay within KeepCount.
///   • All schedule parameters are read from the Settings table on every
///     tick so changes take effect without a restart.
/// </summary>
public sealed class AutoBackupService : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(15);

    private readonly IBackupService      _backup;
    private readonly ISettingsRepository _settings;
    private readonly IPiiLogger          _logger;

    public AutoBackupService(IBackupService backup, ISettingsRepository settings, IPiiLogger logger)
    {
        _backup   = backup;
        _settings = settings;
        _logger   = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInfo("AutoBackupService started.");

        using var timer = new PeriodicTimer(CheckInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await TryRunBackupAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError("AutoBackup", null, ex);
            }
        }
    }

    private async Task TryRunBackupAsync(CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return;

        // Read all schedule params fresh on every tick
        var enabledStr     = await _settings.GetAsync("Backup:Enabled");
        if (!string.Equals(enabledStr, "true", StringComparison.OrdinalIgnoreCase)) return;

        var intervalStr    = await _settings.GetAsync("Backup:IntervalHours");
        var keepStr        = await _settings.GetAsync("Backup:KeepCount");
        var lastStr        = await _settings.GetAsync("Backup:LastBackupAt");

        var intervalHours  = int.TryParse(intervalStr, out var ih) && ih > 0 ? ih : 24;
        var keepCount      = int.TryParse(keepStr,     out var kc) && kc > 0 ? kc : 10;
        var lastBackup     = DateTime.TryParse(lastStr, out var lb) ? lb : DateTime.MinValue;

        var due = (DateTime.UtcNow - lastBackup) >= TimeSpan.FromHours(intervalHours);
        if (!due) return;

        _logger.LogInfo($"AutoBackup: backup due (last={lastStr ?? "never"}, interval={intervalHours}h). Running…");

        var result = await _backup.CreateBackupAsync("auto");
        if (!result.Success)
        {
            _logger.LogError($"AutoBackup: backup FAILED — {result.Error}", null, null);
            return;
        }

        // Record timestamp so the next tick calculates the right next-due time
        await _settings.SetAsync("Backup:LastBackupAt",
            DateTime.UtcNow.ToString("O"),
            "Timestamp of last successful auto-backup");

        // Prune old backups — but only auto ones don't count manual ones
        await _backup.PruneAsync(keepCount);

        _logger.LogInfo($"AutoBackup: completed — {result.FileName}. Kept last {keepCount} backup(s).");
    }
}
