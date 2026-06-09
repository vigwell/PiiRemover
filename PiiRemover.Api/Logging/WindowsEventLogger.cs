using System.Diagnostics;
using System.Text;
using PiiRemover.Core.Logging;

namespace PiiRemover.Api.Logging;

// Writes structured log entries to the Windows Application Event Log.
// Requires the "PiiRemover" event source to be registered once (admin rights).
// Registration happens automatically on first use if running as admin, otherwise falls back to "Application".
public class WindowsEventLogger : IPiiLogger
{
    private const string SourceName = "PiiRemover";
    private const string LogName    = "Application";

    private readonly LogMode _mode;

    public WindowsEventLogger(LogMode mode)
    {
        _mode = mode;
        EnsureSourceExists();
    }

    public void LogRequest(PiiRequestLog entry)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Operation   : {entry.Operation}");
        sb.AppendLine($"Client      : {entry.ClientName ?? "unknown"} (Id={entry.ClientId})");
        sb.AppendLine($"File        : {entry.FileName ?? "-"} ({entry.FileSizeBytes / 1024} KB, {entry.MimeType})");
        sb.AppendLine($"Extractor   : {entry.ExtractorUsed ?? "-"}");
        sb.AppendLine($"Duration    : {entry.DurationMs} ms");
        sb.AppendLine($"Matches     : {entry.MatchCount}");
        sb.AppendLine($"Fields hit  : {string.Join(", ", entry.FieldsHit)}");

        if (_mode == LogMode.Debug)
        {
            sb.AppendLine();
            sb.AppendLine("── Extracted text ──────────────────────────────────────");
            sb.AppendLine(Truncate(entry.ExtractedText, 8192));
            if (entry.RedactedText is not null)
            {
                sb.AppendLine("── Redacted text ───────────────────────────────────────");
                sb.AppendLine(Truncate(entry.RedactedText, 8192));
            }
        }

        Write(EventLogEntryType.Information, 1000, sb.ToString());
    }

    public void LogError(string operation, string? clientName, Exception ex, string? extraInfo = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Operation : {operation}");
        sb.AppendLine($"Client    : {clientName ?? "unknown"}");
        sb.AppendLine($"Error     : {ex.GetType().Name}: {ex.Message}");
        if (_mode == LogMode.Debug)
        {
            sb.AppendLine($"Stack     : {ex.StackTrace}");
            if (extraInfo is not null)
            {
                sb.AppendLine("── Extra info ─────────────────────────────────────────");
                sb.AppendLine(Truncate(extraInfo, 4096));
            }
        }
        Write(EventLogEntryType.Error, 1001, sb.ToString());
    }

    public void LogInfo(string message) =>
        Write(EventLogEntryType.Information, 1002, message);

    private static void Write(EventLogEntryType type, int eventId, string message)
    {
        try
        {
            EventLog.WriteEntry(SourceName, message, type, eventId);
        }
        catch
        {
            // Fallback if source not registered
            try { EventLog.WriteEntry("Application", $"[PiiRemover] {message}", type, eventId); }
            catch { /* cannot log — swallow silently */ }
        }
    }

    private static void EnsureSourceExists()
    {
        try
        {
            if (!EventLog.SourceExists(SourceName))
                EventLog.CreateEventSource(SourceName, LogName);
        }
        catch { /* requires admin rights; falls back to "Application" source in Write() */ }
    }

    private static string Truncate(string? text, int maxLen) =>
        text is null ? "-" :
        text.Length <= maxLen ? text :
        text[..maxLen] + $"\n[...truncated at {maxLen} chars]";
}
