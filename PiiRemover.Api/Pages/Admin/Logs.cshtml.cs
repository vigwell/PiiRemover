using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PiiRemover.Data.Repositories;
using System.Text;

namespace PiiRemover.Api.Pages.Admin;

[Authorize]
public class LogsModel : AdminPageModel
{
    private readonly ILogRepository    _logs;
    private readonly IClientRepository _clients;

    public IEnumerable<RequestLogEntry> Logs { get; private set; } = [];
    public int Total { get; private set; }
    public int CurrentPage { get; private set; }
    public int PageCount { get; private set; }

    // Filter state
    public string? FilterFrom      { get; private set; }
    public string? FilterTo        { get; private set; }
    public int?    FilterClientId  { get; private set; }
    public string? FilterFileName  { get; private set; }
    public string? FilterEventType { get; private set; }
    public bool    IsFiltered      => FilterFrom != null || FilterTo != null || FilterClientId != null || FilterFileName != null || FilterEventType != null;
    public IEnumerable<ClientRecord>   AllClients      { get; private set; } = [];
    public IEnumerable<EventTypeCount> EventTypeCounts { get; private set; } = [];

    public static readonly Dictionary<string, string> EventTypeLabels = new()
    {
        ["TextRedaction"]  = "📄 Text Redaction",
        ["ImageRedaction"] = "🖼 Image Redaction",
        ["OcrExtract"]     = "🔍 OCR Extract",
        ["VideoProcessing"]= "🎬 Video Processing"
    };

    public LogsModel(ILogRepository logs, IClientRepository clients)
    {
        _logs    = logs;
        _clients = clients;
    }

    public async Task OnGetAsync(int page = 1, string? from = null, string? to = null,
        int? clientId = null, string? fileName = null, string? eventType = null)
    {
        CurrentPage     = Math.Max(1, page);
        FilterFrom      = string.IsNullOrWhiteSpace(from)      ? null : from;
        FilterTo        = string.IsNullOrWhiteSpace(to)        ? null : to;
        FilterClientId  = clientId;
        FilterFileName  = string.IsNullOrWhiteSpace(fileName)  ? null : fileName;
        FilterEventType = string.IsNullOrWhiteSpace(eventType) ? null : eventType;

        var loadsTask    = _clients.GetAllAsync();
        var countsTask   = _logs.GetEventTypeCountsAsync(30);
        await Task.WhenAll(loadsTask, countsTask);
        AllClients      = loadsTask.Result;
        EventTypeCounts = countsTask.Result;

        if (IsFiltered)
        {
            Total     = await _logs.CountFilteredAsync(FilterFrom, FilterTo, FilterClientId, FilterFileName, FilterEventType);
            PageCount = Math.Max(1, (int)Math.Ceiling(Total / 50.0));
            Logs      = await _logs.GetFilteredAsync(CurrentPage, 50, FilterFrom, FilterTo, FilterClientId, FilterFileName, FilterEventType);
        }
        else
        {
            Total     = await _logs.CountAsync();
            PageCount = Math.Max(1, (int)Math.Ceiling(Total / 50.0));
            Logs      = await _logs.GetRecentAsync(CurrentPage, 50);
        }
    }

    public async Task<IActionResult> OnGetExportCsvAsync(string? from = null, string? to = null,
        int? clientId = null, string? fileName = null, string? eventType = null)
    {
        var f  = string.IsNullOrWhiteSpace(from)      ? null : from;
        var t  = string.IsNullOrWhiteSpace(to)        ? null : to;
        var fn = string.IsNullOrWhiteSpace(fileName)  ? null : fileName;
        var et = string.IsNullOrWhiteSpace(eventType) ? null : eventType;

        // Fetch up to 50,000 rows for CSV export
        var rows = f == null && t == null && clientId == null && fn == null && et == null
            ? await _logs.GetRecentAsync(1, 50000)
            : await _logs.GetFilteredAsync(1, 50000, f, t, clientId, fn, et);

        var sb = new StringBuilder();
        sb.AppendLine("Id,RequestedAt,ClientId,EventType,FileName,FileSizeKb,DurationMs,FieldsHit,Error");
        foreach (var row in rows)
        {
            sb.AppendLine(string.Join(",",
                row.Id,
                CsvEsc(row.RequestedAt),
                row.ClientId?.ToString() ?? "",
                CsvEsc(row.EventType),
                CsvEsc(row.FileName),
                row.FileSizeKb,
                row.DurationMs,
                CsvEsc(row.FieldsHit),
                CsvEsc(row.ErrorMsg)));
        }

        var date = DateTime.UtcNow.ToString("yyyyMMdd");
        return File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv", $"pii-log-{date}.csv");
    }

    /// <summary>Builds a page URL preserving current filter params.</summary>
    public string PageUrl(int page)
    {
        var parts = new List<string> { $"page={page}" };
        if (FilterFrom      != null) parts.Add($"from={Uri.EscapeDataString(FilterFrom)}");
        if (FilterTo        != null) parts.Add($"to={Uri.EscapeDataString(FilterTo)}");
        if (FilterClientId  != null) parts.Add($"clientId={FilterClientId}");
        if (FilterFileName  != null) parts.Add($"fileName={Uri.EscapeDataString(FilterFileName)}");
        if (FilterEventType != null) parts.Add($"eventType={Uri.EscapeDataString(FilterEventType)}");
        return "/admin/logs?" + string.Join("&", parts);
    }

    /// <summary>URL for CSV export with current filter params.</summary>
    public string CsvExportUrl
    {
        get
        {
            var parts = new List<string> { "handler=ExportCsv" };
            if (FilterFrom      != null) parts.Add($"from={Uri.EscapeDataString(FilterFrom)}");
            if (FilterTo        != null) parts.Add($"to={Uri.EscapeDataString(FilterTo)}");
            if (FilterClientId  != null) parts.Add($"clientId={FilterClientId}");
            if (FilterFileName  != null) parts.Add($"fileName={Uri.EscapeDataString(FilterFileName)}");
            if (FilterEventType != null) parts.Add($"eventType={Uri.EscapeDataString(FilterEventType)}");
            return "/admin/logs?" + string.Join("&", parts);
        }
    }

    private static string CsvEsc(string? v)
    {
        if (string.IsNullOrEmpty(v)) return "";
        if (v.Contains(',') || v.Contains('"') || v.Contains('\n'))
            return "\"" + v.Replace("\"", "\"\"") + "\"";
        return v;
    }
}
