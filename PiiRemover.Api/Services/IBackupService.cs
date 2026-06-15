namespace PiiRemover.Api.Services;

public interface IBackupService
{
    /// <summary>Directory where backup files are stored.</summary>
    string BackupDirectory { get; }

    /// <summary>
    /// Creates a database backup.
    /// Label is appended to the filename (e.g. "auto", "manual").
    /// </summary>
    Task<BackupResult> CreateBackupAsync(string label = "manual");

    /// <summary>Returns backup files sorted newest-first.</summary>
    Task<IReadOnlyList<BackupFileInfo>> ListBackupsAsync();

    /// <summary>
    /// Deletes backup files beyond <paramref name="keepCount"/>, oldest first.
    /// </summary>
    Task PruneAsync(int keepCount);

    /// <summary>Restores the database from a named backup file (filename only, no path).</summary>
    Task RestoreAsync(string fileName);

    /// <summary>Returns the full path for a named backup file after validating it exists.</summary>
    string? ResolveSafe(string fileName);
}

public sealed class BackupFileInfo
{
    public string   FileName  { get; init; } = string.Empty;
    public string   FullPath  { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; }
    public long     SizeBytes { get; init; }
    public string   Label     { get; init; } = string.Empty;   // "auto" | "manual"

    public string SizeDisplay => SizeBytes switch
    {
        < 1024           => $"{SizeBytes} B",
        < 1024 * 1024    => $"{SizeBytes / 1024.0:F1} KB",
        _                => $"{SizeBytes / (1024.0 * 1024):F1} MB",
    };
}

public sealed class BackupResult
{
    public bool    Success  { get; init; }
    public string? FileName { get; init; }
    public string? Error    { get; init; }
}
