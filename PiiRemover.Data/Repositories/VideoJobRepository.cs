using Dapper;
using Microsoft.Data.Sqlite;
using PiiRemover.Data.Models;

namespace PiiRemover.Data.Repositories;

public class VideoJobRepository : IVideoJobRepository
{
    private readonly string _cs;
    public VideoJobRepository(string connectionString) => _cs = connectionString;

    private SqliteConnection Open() { var c = new SqliteConnection(_cs); c.Open(); return c; }

    public async Task<VideoJob?> GetAsync(string id)
    {
        using var conn = Open();
        return await conn.QueryFirstOrDefaultAsync<VideoJob>(
            "SELECT * FROM VideoJobs WHERE Id = @id", new { id });
    }

    public async Task<IEnumerable<VideoJob>> GetByClientAsync(int clientId, int limit = 20)
    {
        using var conn = Open();
        return await conn.QueryAsync<VideoJob>(
            "SELECT * FROM VideoJobs WHERE ClientId = @clientId ORDER BY CreatedAt DESC LIMIT @limit",
            new { clientId, limit });
    }

    public async Task<IEnumerable<VideoJob>> GetQueuedAsync(int limit)
    {
        using var conn = Open();
        return await conn.QueryAsync<VideoJob>(
            "SELECT * FROM VideoJobs WHERE Status = 'queued' ORDER BY CreatedAt LIMIT @limit",
            new { limit });
    }

    public async Task InsertAsync(VideoJob job)
    {
        using var conn = Open();
        await conn.ExecuteAsync("""
            INSERT INTO VideoJobs
                (Id, ClientId, Status, VideoPath, AudioPath, OutputPath, VideoName, AudioName,
                 TranscriptText, RedactPii, RedactAudioPii, CreatedAt)
            VALUES
                (@Id, @ClientId, @Status, @VideoPath, @AudioPath, @OutputPath, @VideoName, @AudioName,
                 @TranscriptText, @RedactPii, @RedactAudioPii, @CreatedAt)
            """, job);
    }

    public async Task<IEnumerable<VideoJob>> GetAllAsync(int limit = 50)
    {
        using var conn = Open();
        return await conn.QueryAsync<VideoJob>(
            "SELECT * FROM VideoJobs ORDER BY CreatedAt DESC LIMIT @limit",
            new { limit });
    }

    public async Task<IEnumerable<VideoJob>> GetOldCompletedAsync(DateTime olderThan)
    {
        using var conn = Open();
        return await conn.QueryAsync<VideoJob>(
            """
            SELECT * FROM VideoJobs
            WHERE Status IN ('completed','failed')
              AND (VideoPath IS NOT NULL OR AudioPath IS NOT NULL)
              AND datetime(CreatedAt) < datetime(@cutoff)
            ORDER BY CreatedAt
            """,
            new { cutoff = olderThan.ToString("o") });
    }

    public async Task UpdateStatusAsync(string id, string status,
        string? outputPath = null, long? durationMs = null, string? errorMsg = null,
        string? startedAt = null, string? completedAt = null)
    {
        using var conn = Open();
        await conn.ExecuteAsync("""
            UPDATE VideoJobs SET
                Status      = @status,
                OutputPath  = COALESCE(@outputPath, OutputPath),
                DurationMs  = COALESCE(@durationMs, DurationMs),
                ErrorMsg    = COALESCE(@errorMsg, ErrorMsg),
                StartedAt   = COALESCE(@startedAt, StartedAt),
                CompletedAt = COALESCE(@completedAt, CompletedAt)
            WHERE Id = @id
            """, new { id, status, outputPath, durationMs, errorMsg, startedAt, completedAt });
    }
}
