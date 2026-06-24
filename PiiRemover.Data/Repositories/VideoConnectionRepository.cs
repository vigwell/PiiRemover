using Dapper;
using Microsoft.Data.Sqlite;

namespace PiiRemover.Data.Repositories;

public class VideoConnectionRepository : IVideoConnectionRepository
{
    private readonly string _cs;
    public VideoConnectionRepository(string connectionString) => _cs = connectionString;

    private SqliteConnection Open() { var c = new SqliteConnection(_cs); c.Open(); return c; }

    public async Task InsertAsync(string connectionId, int clientId)
    {
        var now = DateTime.UtcNow.ToString("o");
        using var conn = Open();
        await conn.ExecuteAsync("""
            INSERT OR REPLACE INTO VideoConnections (ConnectionId, ClientId, ConnectedAt, LastSeenAt, IsActive)
            VALUES (@connectionId, @clientId, @now, @now, 1)
            """, new { connectionId, clientId, now });
    }

    public async Task UpdateLastSeenAsync(string connectionId)
    {
        using var conn = Open();
        await conn.ExecuteAsync(
            "UPDATE VideoConnections SET LastSeenAt = @now WHERE ConnectionId = @connectionId",
            new { now = DateTime.UtcNow.ToString("o"), connectionId });
    }

    public async Task MarkInactiveAsync(string connectionId)
    {
        using var conn = Open();
        await conn.ExecuteAsync(
            "UPDATE VideoConnections SET IsActive = 0 WHERE ConnectionId = @connectionId",
            new { connectionId });
    }

    public async Task PruneInactiveAsync(int olderThanMinutes)
    {
        using var conn = Open();
        await conn.ExecuteAsync("""
            DELETE FROM VideoConnections
            WHERE IsActive = 0
              AND datetime(LastSeenAt) < datetime('now', @offset)
            """, new { offset = $"-{olderThanMinutes} minutes" });
    }
}
