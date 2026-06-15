using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PiiRemover.Core.Licensing;
using PiiRemover.Data.Repositories;

namespace PiiRemover.Api.Pages.Admin;

[Authorize]
public class AboutModel : AdminPageModel
{
    private readonly LicenseInfo _license;
    private readonly ISettingsRepository _settings;

    public LicenseInfo License => _license;
    public int DaysUntilExpiry => Math.Max(0,
        _license.ExpiryDate.DayNumber - DateOnly.FromDateTime(DateTime.UtcNow).DayNumber);
    public string? SupportContact { get; private set; }

    public AboutModel(LicenseInfo license, ISettingsRepository settings)
    {
        _license  = license;
        _settings = settings;
    }

    public async Task OnGetAsync()
    {
        SupportContact = await _settings.GetAsync("SupportContact");
    }
}
