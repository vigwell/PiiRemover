namespace PiiRemover.Core.Logging;

public interface IPiiLogger
{
    void LogRequest(PiiRequestLog entry);
    void LogError(string operation, string? clientName, Exception ex, string? extraInfo = null);
    void LogInfo(string message);
}

public class PiiRequestLog
{
    public string Operation { get; set; } = string.Empty;      // "Redact" or "Ocr"
    public string? ClientName { get; set; }
    public int? ClientId { get; set; }
    public string? FileName { get; set; }
    public long FileSizeBytes { get; set; }
    public string? MimeType { get; set; }
    public string? ExtractorUsed { get; set; }
    public long DurationMs { get; set; }
    public int MatchCount { get; set; }
    public IEnumerable<string> FieldsHit { get; set; } = [];
    // Full text — only written to log when LogMode = Debug
    public string? ExtractedText { get; set; }
    public string? RedactedText { get; set; }
}
