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
            INSERT INTO RequestLogs (ClientId, FileName, FileSizeKb, DurationMs, FieldsHit, ErrorMsg, EventType)
            VALUES (@ClientId, @FileName, @FileSizeKb, @DurationMs, @FieldsHit, @ErrorMsg, @EventType)
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
        return await conn.ExecuteAsync(
            "DELETE FROM RequestLogs WHERE RequestedAt < @cutoff",
            new { cutoff = cutoff.ToString("yyyy-MM-dd HH:mm:ss") });
    }

    public async Task<IEnumerable<RequestLogEntry>> GetFilteredAsync(int page, int pageSize,
        string? fromDate, string? toDate, int? clientId, string? fileName, string? eventType = null)
    {
        using var conn = Open();
        var sql = BuildFilterSql("SELECT *", fromDate, toDate, clientId, fileName, eventType)
                  + " ORDER BY Id DESC LIMIT @pageSize OFFSET @offset";
        return await conn.QueryAsync<RequestLogEntry>(sql,
            new { pageSize, offset = (page - 1) * pageSize,
                  fromDate, toDate, clientId, fileName = fileName != null ? $"%{fileName}%" : null, eventType });
    }

    public async Task<int> CountFilteredAsync(string? fromDate, string? toDate, int? clientId, string? fileName, string? eventType = null)
    {
        using var conn = Open();
        var sql = BuildFilterSql("SELECT COUNT(*)", fromDate, toDate, clientId, fileName, eventType);
        return await conn.ExecuteScalarAsync<int>(sql,
            new { fromDate, toDate, clientId, fileName = fileName != null ? $"%{fileName}%" : null, eventType });
    }

    public async Task<IEnumerable<EventTypeCount>> GetEventTypeCountsAsync(int days = 30)
    {
        using var conn = Open();
        return await conn.QueryAsync<EventTypeCount>($"""
            SELECT COALESCE(EventType,'TextRedaction') AS EventType, COUNT(*) AS Count
            FROM RequestLogs
            WHERE RequestedAt >= date('now','-{days} days')
            GROUP BY EventType
            ORDER BY Count DESC
            """);
    }

    public async Task<IEnumerable<DailyCallCount>> GetDailyCallsAsync(int days)
    {
        using var conn = Open();
        // Fill gaps with 0 using a recursive CTE
        var sql = $"""
            WITH RECURSIVE dates(d) AS (
              SELECT date('now', '-{days - 1} days')
              UNION ALL SELECT date(d, '+1 day') FROM dates WHERE d < date('now')
            )
            SELECT d AS Day, COALESCE(c.cnt, 0) AS Count
            FROM dates
            LEFT JOIN (
              SELECT date(RequestedAt) AS day, COUNT(*) AS cnt
              FROM RequestLogs
              WHERE RequestedAt >= date('now', '-{days - 1} days')
              GROUP BY date(RequestedAt)
            ) c ON c.day = dates.d
            ORDER BY dates.d
            """;
        return await conn.QueryAsync<DailyCallCount>(sql);
    }

    public async Task<IEnumerable<FieldHitCount>> GetTopFieldsAsync(int days, int topN)
    {
        using var conn = Open();
        // FieldsHit is a JSON array like ["Name","ID"]. We pull all non-null entries
        // from the last N days and count occurrences in application code.
        var entries = await conn.QueryAsync<string?>(
            $"SELECT FieldsHit FROM RequestLogs WHERE FieldsHit IS NOT NULL AND RequestedAt >= date('now','-{days} days')");

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var json in entries)
        {
            if (string.IsNullOrWhiteSpace(json)) continue;
            try
            {
                var fields = System.Text.Json.JsonSerializer.Deserialize<string[]>(json);
                if (fields is null) continue;
                foreach (var f in fields)
                {
                    if (string.IsNullOrWhiteSpace(f)) continue;
                    counts.TryGetValue(f, out var c);
                    counts[f] = c + 1;
                }
            }
            catch { /* skip malformed JSON */ }
        }

        return counts
            .OrderByDescending(kv => kv.Value)
            .Take(topN)
            .Select(kv => new FieldHitCount { FieldName = kv.Key, Count = kv.Value });
    }

    public async Task<ClientStats> GetClientStatsAsync(int clientId, int days)
    {
        using var conn = Open();
        return await conn.QueryFirstAsync<ClientStats>($"""
            SELECT
              COUNT(*)                                AS TotalCalls,
              SUM(CASE WHEN ErrorMsg IS NOT NULL AND ErrorMsg != '' THEN 1 ELSE 0 END) AS ErrorCount,
              COALESCE(AVG(DurationMs), 0)            AS AvgDurationMs
            FROM RequestLogs
            WHERE ClientId = @clientId AND RequestedAt >= date('now','-{days} days')
            """, new { clientId });
    }

    public async Task<IEnumerable<DailyCallCount>> GetClientDailyCallsAsync(int clientId, int days)
    {
        using var conn = Open();
        var sql = $"""
            WITH RECURSIVE dates(d) AS (
              SELECT date('now', '-{days - 1} days')
              UNION ALL SELECT date(d, '+1 day') FROM dates WHERE d < date('now')
            )
            SELECT d AS Day, COALESCE(c.cnt, 0) AS Count
            FROM dates
            LEFT JOIN (
              SELECT date(RequestedAt) AS day, COUNT(*) AS cnt
              FROM RequestLogs
              WHERE ClientId = @clientId AND RequestedAt >= date('now', '-{days - 1} days')
              GROUP BY date(RequestedAt)
            ) c ON c.day = dates.d
            ORDER BY dates.d
            """;
        return await conn.QueryAsync<DailyCallCount>(sql, new { clientId });
    }

    public async Task<IEnumerable<FieldHitCount>> GetClientTopFieldsAsync(int clientId, int days, int topN)
    {
        using var conn = Open();
        var entries = await conn.QueryAsync<string?>(
            $"SELECT FieldsHit FROM RequestLogs WHERE ClientId = @clientId AND FieldsHit IS NOT NULL AND RequestedAt >= date('now','-{days} days')",
            new { clientId });
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var json in entries)
        {
            if (string.IsNullOrWhiteSpace(json)) continue;
            try
            {
                var fields = System.Text.Json.JsonSerializer.Deserialize<string[]>(json);
                if (fields is null) continue;
                foreach (var f in fields)
                {
                    if (string.IsNullOrWhiteSpace(f)) continue;
                    counts.TryGetValue(f, out var c);
                    counts[f] = c + 1;
                }
            }
            catch { }
        }
        return counts.OrderByDescending(kv => kv.Value).Take(topN)
            .Select(kv => new FieldHitCount { FieldName = kv.Key, Count = kv.Value });
    }

    public async Task<DashboardStats> GetDashboardStatsAsync()
    {
        using var conn = Open();
        var today = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM RequestLogs WHERE date(RequestedAt) = date('now')");
        var row = await conn.QueryFirstAsync<DashboardStats>("""
            SELECT
              CAST(COALESCE(AVG(DurationMs), 0) AS INTEGER) AS AvgDurationMs,
              SUM(CASE WHEN ErrorMsg IS NOT NULL AND ErrorMsg != '' THEN 1 ELSE 0 END) AS ErrorCount7d,
              COUNT(*) AS TotalCalls7d
            FROM RequestLogs
            WHERE RequestedAt >= date('now', '-6 days')
            """);
        row.TodayCount = today;
        row.LastRequestAt = await conn.ExecuteScalarAsync<string>(
            "SELECT RequestedAt FROM RequestLogs ORDER BY Id DESC LIMIT 1") ?? string.Empty;
        return row;
    }

    public async Task<IEnumerable<RequestLogEntry>> GetLatestAsync(int count)
    {
        using var conn = Open();
        return await conn.QueryAsync<RequestLogEntry>(
            "SELECT * FROM RequestLogs ORDER BY Id DESC LIMIT @count", new { count });
    }

    private static string BuildFilterSql(string select, string? fromDate, string? toDate, int? clientId, string? fileName, string? eventType = null)
    {
        var wheres = new List<string>();
        if (!string.IsNullOrWhiteSpace(fromDate))  wheres.Add("date(RequestedAt) >= @fromDate");
        if (!string.IsNullOrWhiteSpace(toDate))    wheres.Add("date(RequestedAt) <= @toDate");
        if (clientId.HasValue)                     wheres.Add("ClientId = @clientId");
        if (!string.IsNullOrWhiteSpace(fileName))  wheres.Add("FileName LIKE @fileName");
        if (!string.IsNullOrWhiteSpace(eventType)) wheres.Add("COALESCE(EventType,'TextRedaction') = @eventType");
        return $"{select} FROM RequestLogs" + (wheres.Count > 0 ? " WHERE " + string.Join(" AND ", wheres) : "");
    }
}
