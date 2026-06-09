using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PiiRemover.Core.Licensing;
using PiiRemover.Data.Repositories;

namespace PiiRemover.Api.Pages.Admin;

[Authorize]
public class SettingsModel : AdminPageModel
{
    private readonly ISettingsRepository _settings;
    private readonly IQuotaRepository _quota;
    private readonly LicenseInfo _license;

    public LicenseInfo License => _license;
    public int DaysUntilExpiry => Math.Max(0,
        _license.ExpiryDate.DayNumber - DateOnly.FromDateTime(DateTime.UtcNow).DayNumber);
    public long QuotaUsed { get; private set; }
    public IEnumerable<SettingEntry> AllSettings { get; private set; } = [];

    public string? SuccessMessage { get; private set; }
    public string? ErrorMessage { get; private set; }

    [BindProperty] public string CurrentPassword { get; set; } = string.Empty;
    [BindProperty] public string NewPassword { get; set; } = string.Empty;
    [BindProperty] public string ConfirmPassword { get; set; } = string.Empty;
    [BindProperty] public int RetentionMonths { get; set; } = 1;
    [BindProperty] public string OcrEngineOrder { get; set; } = "WindowsOcr,Tesseract";
    [BindProperty] public string TesseractLanguages { get; set; } = "heb+eng";
    [BindProperty] public int OcrMaxConcurrency { get; set; } = 0;

    public SettingsModel(ISettingsRepository settings, IQuotaRepository quota, LicenseInfo license)
    {
        _settings = settings;
        _quota    = quota;
        _license  = license;
    }

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostChangePasswordAsync()
    {
        var existing = await _settings.GetAsync("admin:passwordHash");
        if (HashPw(CurrentPassword) != existing)
        {
            ErrorMessage = "Current password is incorrect.";
            await LoadAsync();
            return Page();
        }
        if (NewPassword != ConfirmPassword)
        {
            ErrorMessage = "New password and confirmation do not match.";
            await LoadAsync();
            return Page();
        }
        await _settings.SetAsync("admin:passwordHash", HashPw(NewPassword), "Admin console password (SHA-256 hex)");
        SuccessMessage = "Password changed successfully.";
        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostSaveLogRetentionAsync()
    {
        if (RetentionMonths < 1) RetentionMonths = 1;
        await _settings.SetAsync("Logging:RetentionMonths", RetentionMonths.ToString(), "Log retention in months");
        SuccessMessage = $"Log retention set to {RetentionMonths} month(s).";
        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostSaveOcrAsync()
    {
        await _settings.SetAsync("Ocr:EngineOrder", OcrEngineOrder, "OCR engine order (comma-separated)");
        await _settings.SetAsync("Ocr:TesseractLanguages", TesseractLanguages, "Tesseract language string");
        await _settings.SetAsync("Ocr:MaxConcurrency", OcrMaxConcurrency.ToString(), "Max concurrent OCR operations (0 = CPU count)");
        SuccessMessage = "OCR settings saved. Restart the service to apply engine/concurrency changes.";
        await LoadAsync();
        return Page();
    }

    private async Task LoadAsync()
    {
        QuotaUsed   = await _quota.GetUsedAsync();
        AllSettings = await _settings.GetAllAsync();
        var retStr  = await _settings.GetAsync("Logging:RetentionMonths");
        RetentionMonths = int.TryParse(retStr, out var r) ? r : 1;
        var eng = await _settings.GetAsync("Ocr:EngineOrder");
        if (!string.IsNullOrWhiteSpace(eng)) OcrEngineOrder = eng;
        var tl = await _settings.GetAsync("Ocr:TesseractLanguages");
        if (!string.IsNullOrWhiteSpace(tl)) TesseractLanguages = tl;
        var mc = await _settings.GetAsync("Ocr:MaxConcurrency");
        if (int.TryParse(mc, out var mci)) OcrMaxConcurrency = mci;
    }

    private static string HashPw(string pw) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(pw))).ToLowerInvariant();
}
