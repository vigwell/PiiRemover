namespace PiiRemover.Data.Repositories;

public interface ILogRepository
{
    Task InsertAsync(RequestLogEntry entry);
    Task<IEnumerable<RequestLogEntry>> GetRecentAsync(int page, int pageSize);
    Task<int> CountAsync();
    Task<int> DeleteOlderThanAsync(DateTime cutoff);
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
}
