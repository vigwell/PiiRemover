using Microsoft.Data.Sqlite;
using PiiRemover.Core.Logging;

namespace PiiRemover.Api.Services;

/// <summary>
/// Handles all database backup / restore / prune operations.
/// Backup files are named: piiremovals_YYYYMMDD_HHmmss_{label}.db
/// </summary>
public sealed class BackupService : IBackupService
{
    private readonly IConfiguration _config;
    private readonly IPiiLogger     _logger;

    public BackupService(IConfiguration config, IPiiLogger logger)
    {
        _config = config;
        _logger = logger;
    }

    // ── IBackupService ────────────────────────────────────────────────────

    public string BackupDirectory
    {
        get
        {
            var dir = _config["Backup:Directory"];
            if (string.IsNullOrWhiteSpace(dir)) dir = "backups";
            // Relative paths are resolved from the app base directory
            return Path.IsPathRooted(dir) ? dir : Path.Combine(AppContext.BaseDirectory, dir);
        }
    }

    public async Task<BackupResult> CreateBackupAsync(string label = "manual")
    {
        try
        {
            var dbPath = DbPath();
            if (!File.Exists(dbPath))
                return new BackupResult { Success = false, Error = "Database file not found." };

            Directory.CreateDirectory(BackupDirectory);

            // Flush WAL journal so the copied file is fully consistent
            try
            {
                using var conn = new SqliteConnection($"Data Source={dbPath}");
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "PRAGMA wal_checkpoint(FULL)";
                cmd.ExecuteNonQuery();
            }
            catch { /* non-fatal — copy anyway */ }

            var safeLabel = string.IsNullOrWhiteSpace(label) ? "manual" : label.Trim().ToLowerInvariant();
            var fileName  = $"piiremovals_{DateTime.Now:yyyyMMdd_HHmmss}_{safeLabel}.db";
            var destPath  = Path.Combine(BackupDirectory, fileName);

            await using var src  = new FileStream(dbPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            await using var dest = new FileStream(destPath, FileMode.Create, FileAccess.Write);
            await src.CopyToAsync(dest);

            _logger.LogInfo($"Backup created: {fileName} ({new FileInfo(destPath).Length / 1024} KB)");
            return new BackupResult { Success = true, FileName = fileName };
        }
        catch (Exception ex)
        {
            _logger.LogError("AutoBackup", null, ex);
            return new BackupResult { Success = false, Error = ex.Message };
        }
    }

    public Task<IReadOnlyList<BackupFileInfo>> ListBackupsAsync()
    {
        var dir = BackupDirectory;
        if (!Directory.Exists(dir))
            return Task.FromResult<IReadOnlyList<BackupFileInfo>>([]);

        var files = Directory
            .GetFiles(dir, "piiremovals_*.db")
            .Select(path =>
            {
                var name  = Path.GetFileNameWithoutExtension(path); // piiremovals_20260615_143022_auto
                var parts = name.Split('_');
                // parse date from parts[1] (date) + parts[2] (time)
                var label = parts.Length >= 4 ? parts[^1] : "manual";
                DateTime created;
                if (parts.Length >= 3 &&
                    DateTime.TryParseExact(parts[1] + parts[2], "yyyyMMddHHmmss",
                        null, System.Globalization.DateTimeStyles.None, out var dt))
                    created = dt;
                else
                    created = File.GetCreationTime(path);

                return new BackupFileInfo
                {
                    FileName  = Path.GetFileName(path),
                    FullPath  = path,
                    CreatedAt = created,
                    SizeBytes = new FileInfo(path).Length,
                    Label     = label,
                };
            })
            .OrderByDescending(f => f.CreatedAt)
            .ToList();

        return Task.FromResult<IReadOnlyList<BackupFileInfo>>(files);
    }

    public async Task PruneAsync(int keepCount)
    {
        if (keepCount <= 0) return;
        var all = await ListBackupsAsync();
        foreach (var old in all.Skip(keepCount))
        {
            try
            {
                File.Delete(old.FullPath);
                _logger.LogInfo($"Backup pruned: {old.FileName}");
            }
            catch { /* best-effort */ }
        }
    }

    public async Task RestoreAsync(string fileName)
    {
        var srcPath = ResolveSafe(fileName)
            ?? throw new FileNotFoundException($"Backup not found: {fileName}");

        var dbPath = DbPath();

        // Safety copy before overwrite
        if (File.Exists(dbPath))
            File.Copy(dbPath, dbPath + ".bak", overwrite: true);

        // Release pooled connections
        SqliteConnection.ClearAllPools();

        await using var src  = new FileStream(srcPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        await using var dest = new FileStream(dbPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await src.CopyToAsync(dest);

        // Delete stale WAL / SHM sidecars
        foreach (var sidecar in new[] { dbPath + "-wal", dbPath + "-shm" })
            if (File.Exists(sidecar)) File.Delete(sidecar);

        _logger.LogInfo($"Database restored from backup: {fileName}");
    }

    public string? ResolveSafe(string fileName)
    {
        // Reject any attempt at path traversal
        if (string.IsNullOrWhiteSpace(fileName)
            || fileName.Contains('/')
            || fileName.Contains('\\')
            || !fileName.EndsWith(".db", StringComparison.OrdinalIgnoreCase)
            || !fileName.StartsWith("piiremovals_", StringComparison.OrdinalIgnoreCase))
            return null;

        var full = Path.Combine(BackupDirectory, fileName);
        return File.Exists(full) ? full : null;
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private string DbPath() => _config["Database:Path"] ?? "piiremovals.db";
}
