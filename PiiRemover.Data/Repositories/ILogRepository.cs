namespace PiiRemover.Data.Repositories;

public interface ILogRepository
{
    Task InsertAsync(RequestLogEntry entry);
    Task<IEnumerable<RequestLogEntry>> GetRecentAsync(int page, int pageSize);
    Task<int> CountAsync();
    Task<int> DeleteOlderThanAsync(DateTime cutoff);
    Task<IEnumerable<RequestLogEntry>> GetFilteredAsync(int page, int pageSize,
        string? fromDate, string? toDate, int? clientId, string? fileName, string? eventType = null);
    Task<int> CountFilteredAsync(string? fromDate, string? toDate, int? clientId, string? fileName, string? eventType = null);
    Task<IEnumerable<EventTypeCount>> GetEventTypeCountsAsync(int days = 30);
    Task<IEnumerable<DailyCallCount>> GetDailyCallsAsync(int days);
    Task<IEnumerable<FieldHitCount>> GetTopFieldsAsync(int days, int topN);
    Task<ClientStats> GetClientStatsAsync(int clientId, int days);
    Task<IEnumerable<DailyCallCount>> GetClientDailyCallsAsync(int clientId, int days);
    Task<IEnumerable<FieldHitCount>> GetClientTopFieldsAsync(int clientId, int days, int topN);
    /// <summary>Returns aggregate KPIs for the dashboard header row.</summary>
    Task<DashboardStats> GetDashboardStatsAsync();
    /// <summary>Returns the N most-recent log entries (for the activity feed).</summary>
    Task<IEnumerable<RequestLogEntry>> GetLatestAsync(int count);
}

public class RequestLogEntry
{
    public int Id { get; set; }
    public int? ClientId { get; set; }
    public string RequestedAt { get; set; } = string.Empty;
    public string? FileName { get; set; }
    public int FileSizeKb { get; set; }
    public long DurationMs { get; set; }
    public string? FieldsHit { get; set; }
    public string? ErrorMsg { get; set; }
    public string EventType { get; set; } = "TextRedaction";
}

public class EventTypeCount
{
    public string EventType { get; set; } = string.Empty;
    public int Count { get; set; }
}

public class ClientStats
{
    public int TotalCalls { get; set; }
    public int ErrorCount { get; set; }
    public long AvgDurationMs { get; set; }
}

public class DailyCallCount
{
    public string Day { get; set; } = string.Empty;
    public int Count { get; set; }
}

public class FieldHitCount
{
    public string FieldName { get; set; } = string.Empty;
    public int Count { get; set; }
}

public class DashboardStats
{
    public int  TodayCount    { get; set; }
    public int  TotalCalls7d  { get; set; }
    public long AvgDurationMs { get; set; }
    public int  ErrorCount7d  { get; set; }
    public string LastRequestAt { get; set; } = string.Empty;
}
