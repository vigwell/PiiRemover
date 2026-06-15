using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PiiRemover.Core.Licensing;
using PiiRemover.Data.Repositories;

namespace PiiRemover.Api.Pages.Admin;

[Authorize]
public class IndexModel : AdminPageModel
{
    private readonly LicenseInfo      _license;
    private readonly IClientRepository _clients;
    private readonly IFieldRepository  _fields;
    private readonly ILogRepository    _logs;
    private readonly IQuotaRepository  _quota;

    // ── License ─────────────────────────────────────────────────────────
    public LicenseInfo License        => _license;
    public DateOnly    LicenseExpiry  => _license.ExpiryDate;
    public int DaysUntilExpiry => Math.Max(0,
        _license.ExpiryDate.DayNumber - DateOnly.FromDateTime(DateTime.UtcNow).DayNumber);

    // ── Totals ───────────────────────────────────────────────────────────
    public int  TotalClients  { get; private set; }
    public int  TotalFields   { get; private set; }
    public int  TotalPatterns { get; private set; }
    public int  TotalLogs     { get; private set; }
    public long QuotaUsed     { get; private set; }

    // ── KPI stats (today + 7-day window) ─────────────────────────────────
    public DashboardStats Stats { get; private set; } = new();

    // ── Charts ───────────────────────────────────────────────────────────
    public List<DailyCallCount>      DailyCalls { get; private set; } = [];
    public IEnumerable<FieldHitCount> TopFields  { get; private set; } = [];

    // ── Activity feed ────────────────────────────────────────────────────
    public IEnumerable<RequestLogEntry> RecentActivity { get; private set; } = [];

    public IndexModel(LicenseInfo license, IClientRepository clients,
        IFieldRepository fields, ILogRepository logs, IQuotaRepository quota)
    {
        _license = license;
        _clients = clients;
        _fields  = fields;
        _logs    = logs;
        _quota   = quota;
    }

    public async Task OnGetAsync()
    {
        var allFields  = (await _fields.GetAllFieldsAsync()).ToList();
        var allClients = (await _clients.GetAllAsync()).ToList();

        TotalFields   = allFields.Count;
        TotalClients  = allClients.Count;
        TotalLogs     = await _logs.CountAsync();
        QuotaUsed     = await _quota.GetUsedAsync();

        // Pattern count via fields-with-patterns
        var fieldsWithPatterns = await _fields.GetFieldsWithPatternsAsync(null);
        TotalPatterns = fieldsWithPatterns.Sum(f => f.Patterns?.Count() ?? 0);

        Stats          = await _logs.GetDashboardStatsAsync();
        DailyCalls     = (await _logs.GetDailyCallsAsync(30)).ToList();
        TopFields      = await _logs.GetTopFieldsAsync(30, 8);
        RecentActivity = await _logs.GetLatestAsync(10);
    }

    /// <summary>Parses a FieldsHit JSON array string into a list of field name strings.</summary>
    public static List<string> ParseFields(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? []; }
        catch { return []; }
    }

    /// <summary>Returns a human-readable "time ago" label for a stored datetime string.</summary>
    public static string TimeAgo(string? ts)
    {
        if (string.IsNullOrEmpty(ts)) return "—";
        if (!DateTime.TryParse(ts, out var dt)) return "—";
        var diff = DateTime.UtcNow - dt.ToUniversalTime();
        if (diff.TotalSeconds < 60)  return $"{(int)diff.TotalSeconds}s ago";
        if (diff.TotalMinutes < 60)  return $"{(int)diff.TotalMinutes}m ago";
        if (diff.TotalHours   < 24)  return $"{(int)diff.TotalHours}h ago";
        return $"{(int)diff.TotalDays}d ago";
    }
}
