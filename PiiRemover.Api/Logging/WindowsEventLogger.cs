using System.Diagnostics;
using System.Text;
using PiiRemover.Core.Logging;

namespace PiiRemover.Api.Logging;

// Writes structured log entries to a dedicated Windows Event Log.
// Source and log name are read from appsettings: Logging:EventLog:SourceName / LogName.
// Run Register-EventLogSource.ps1 once (admin) before first use.
public class WindowsEventLogger : IPiiLogger
{
    private readonly string _sourceName;
    private readonly string _logName;
    private readonly LogMode _mode;

    public WindowsEventLogger(string sourceName, string logName, LogMode mode)
    {
        _sourceName = sourceName;
        _logName    = logName;
        _mode       = mode;
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
            sb.AppendLine("-- Extracted text --");
            sb.AppendLine(Truncate(entry.ExtractedText, 8192));
            if (entry.RedactedText is not null)
            {
                sb.AppendLine("-- Redacted text --");
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
                sb.AppendLine("-- Extra info --");
                sb.AppendLine(Truncate(extraInfo, 4096));
            }
        }
        Write(EventLogEntryType.Error, 1001, sb.ToString());
    }

    public void LogInfo(string message) =>
        Write(EventLogEntryType.Information, 1002, message);

    private void Write(EventLogEntryType type, int eventId, string message)
    {
        try
        {
            using var log = new EventLog(_logName) { Source = _sourceName };
            log.WriteEntry(message, type, eventId);
        }
        catch (Exception ex1)
        {
            try
            {
                using var fb = new EventLog("Application") { Source = "Application" };
                fb.WriteEntry($"[{_sourceName}] {message}", type, eventId);
            }
            catch (Exception ex2)
            {
                Console.Error.WriteLine($"[EventLog] Failed to write to '{_logName}': {ex1.Message}");
                Console.Error.WriteLine($"[EventLog] Fallback also failed: {ex2.Message}");
            }
        }
    }

    private void EnsureSourceExists()
    {
        try
        {
            if (!EventLog.SourceExists(_sourceName))
            {
                EventLog.CreateEventSource(_sourceName, _logName);
                Console.WriteLine($"[EventLog] Created source '{_sourceName}' in log '{_logName}'.");
            }
            else
            {
                var actual = EventLog.LogNameFromSourceName(_sourceName, ".");
                Console.WriteLine($"[EventLog] Source '{_sourceName}' registered under log '{actual}'.");
                if (!string.Equals(actual, _logName, StringComparison.OrdinalIgnoreCase))
                    Console.Error.WriteLine($"[EventLog] WARNING: expected log '{_logName}' but source is under '{actual}'. Run Register-EventLogSource.ps1 as admin.");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[EventLog] Cannot verify source '{_sourceName}': {ex.Message}. Run Register-EventLogSource.ps1 as admin.");
        }
    }

    private static string Truncate(string? text, int maxLen) =>
        text is null ? "-" :
        text.Length <= maxLen ? text :
        text[..maxLen] + $"\n[...truncated at {maxLen} chars]";
}
