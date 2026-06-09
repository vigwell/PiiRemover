using Dapper;
using Microsoft.Data.Sqlite;

namespace PiiRemover.Data.Repositories;

public class LogRepository : ILogRepository
{
    private readonly string _cs;
    public LogRepository(string connectionString) => _cs = connectionString;

    private SqliteConnection Open() { var c = new SqliteConnection(_cs); c.Open(); return c; }

    public async Task InsertAsync(RequestLogEntry entry)
    {
        using var conn = Open();
        await conn.ExecuteAsync(
            """
            INSERT INTO RequestLogs (ClientId, FileName, FileSizeKb, DurationMs, FieldsHit, ErrorMsg)
            VALUES (@ClientId, @FileName, @FileSizeKb, @DurationMs, @FieldsHit, @ErrorMsg)
            """, entry);
    }

    public async Task<IEnumerable<RequestLogEntry>> GetRecentAsync(int page, int pageSize)
    {
        using var conn = Open();
        return await conn.QueryAsync<RequestLogEntry>(
            "SELECT * FROM RequestLogs ORDER BY Id DESC LIMIT @pageSize OFFSET @offset",
            new { pageSize, offset = (page - 1) * pageSize });
    }

    public async Task<int> CountAsync()
    {
        using var conn = Open();
        return await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM RequestLogs");
    }

    public async Task<int> DeleteOlderThanAsync(DateTime cutoff)
    {
        using var conn = Open();
        // Uses the index on RequestedAt — no full table scan
        return await conn.ExecuteAsync(
            "DELETE FROM RequestLogs WHERE RequestedAt < @cutoff",
            new { cutoff = cutoff.ToString("yyyy-MM-dd HH:mm:ss") });
    }
}
