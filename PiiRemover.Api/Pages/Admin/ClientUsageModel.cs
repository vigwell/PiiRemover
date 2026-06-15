using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PiiRemover.Data.Repositories;

namespace PiiRemover.Api.Pages.Admin;

[Authorize]
public class ClientUsageModel : AdminPageModel
{
    private readonly IClientRepository _clients;
    private readonly ILogRepository    _logs;

    public string ClientName { get; private set; } = "Unknown";
    public int TotalCalls { get; private set; }
    public int ErrorCount { get; private set; }
    public long AvgDurationMs { get; private set; }
    public List<DailyCallCount> DailyCalls { get; private set; } = [];
    public IEnumerable<FieldHitCount> TopFields { get; private set; } = [];

    public ClientUsageModel(IClientRepository clients, ILogRepository logs)
    {
        _clients = clients;
        _logs    = logs;
    }

    public async Task<IActionResult> OnGetAsync(int clientId)
    {
        var allClients = await _clients.GetAllAsync();
        var client = allClients.FirstOrDefault(c => c.Id == clientId);
        if (client is null) return RedirectToPage("/admin/clients");

        ClientName = client.Name;

        var stats = await _logs.GetClientStatsAsync(clientId, 30);
        TotalCalls    = stats.TotalCalls;
        ErrorCount    = stats.ErrorCount;
        AvgDurationMs = stats.AvgDurationMs;

        DailyCalls = (await _logs.GetClientDailyCallsAsync(clientId, 30)).ToList();
        TopFields  = await _logs.GetClientTopFieldsAsync(clientId, 30, 10);

        return Page();
    }
}
