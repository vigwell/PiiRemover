using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PiiRemover.Core.Licensing;
using PiiRemover.Data.Repositories;

namespace PiiRemover.Api.Pages.Admin;

[Authorize]
public class IndexModel : AdminPageModel
{
    private readonly LicenseInfo _license;
    private readonly IClientRepository _clients;
    private readonly IFieldRepository _fields;
    private readonly ILogRepository _logs;
    private readonly IQuotaRepository _quota;

    public LicenseInfo License => _license;
    public DateOnly LicenseExpiry => _license.ExpiryDate;
    public int DaysUntilExpiry => Math.Max(0,
        _license.ExpiryDate.DayNumber - DateOnly.FromDateTime(DateTime.UtcNow).DayNumber);

    public int TotalClients { get; private set; }
    public int TotalFields { get; private set; }
    public int TotalLogs { get; private set; }
    public long QuotaUsed { get; private set; }

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
        TotalClients = (await _clients.GetAllAsync()).Count();
        TotalFields  = (await _fields.GetAllFieldsAsync()).Count();
        TotalLogs    = await _logs.CountAsync();
        QuotaUsed    = await _quota.GetUsedAsync();
    }


}
