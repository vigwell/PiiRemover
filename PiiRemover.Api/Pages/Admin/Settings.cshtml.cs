using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.Sqlite;
using PiiRemover.Api.Services;
using PiiRemover.Core.Licensing;
using PiiRemover.Data.Repositories;
using static PiiRemover.Api.Pages.Admin.AdminAccountHelper;

namespace PiiRemover.Api.Pages.Admin;

[Authorize]
public class SettingsModel : AdminPageModel
{
    private const int MaxAdmins = 5;

    private readonly ISettingsRepository _settings;
    private readonly IQuotaRepository    _quota;
    private readonly LicenseInfo         _license;
    private readonly IConfiguration      _config;
    private readonly IBackupService      _backup;

    public LicenseInfo License => _license;
    public int DaysUntilExpiry => Math.Max(0,
        _license.ExpiryDate.DayNumber - DateOnly.FromDateTime(DateTime.UtcNow).DayNumber);
    public long QuotaUsed { get; private set; }
    public IEnumerable<SettingEntry> AllSettings { get; private set; } = [];

    public string? SuccessMessage { get; private set; }
    public string? ErrorMessage   { get; private set; }

    // ── Admin accounts ──────────────────────────────────────────────────
    public List<string> AdminUsernames { get; private set; } = [];

    [BindProperty] public string NewAdminUsername { get; set; } = string.Empty;
    [BindProperty] public string NewAdminPassword { get; set; } = string.Empty;
    [BindProperty] public string NewAdminConfirm  { get; set; } = string.Empty;
    [BindProperty] public string RemoveUsername   { get; set; } = string.Empty;

    // ── Change current user's own password ──────────────────────────────
    [BindProperty] public string CurrentPassword { get; set; } = string.Empty;
    [BindProperty] public string NewPassword     { get; set; } = string.Empty;
    [BindProperty] public string ConfirmPassword { get; set; } = string.Empty;

    // ── Log retention ───────────────────────────────────────────────────
    [BindProperty] public int RetentionMonths { get; set; } = 1;

    // ── OCR ─────────────────────────────────────────────────────────────
    [BindProperty] public string OcrEngine1        { get; set; } = "platform";
    [BindProperty] public string OcrEngine2        { get; set; } = "enhanced";
    [BindProperty] public string OcrLanguages      { get; set; } = "heb+eng";
    [BindProperty] public int    OcrMaxConcurrency { get; set; } = 0;

    // ── Engine name translation ─────────────────────────────────────────
    private static readonly Dictionary<string, string> EngineToKey = new(StringComparer.OrdinalIgnoreCase)
    {
        ["WindowsOcr"] = "platform",
        ["Tesseract"]  = "enhanced",
    };
    private static readonly Dictionary<string, string> KeyToEngine = new(StringComparer.OrdinalIgnoreCase)
    {
        ["platform"] = "WindowsOcr",
        ["enhanced"] = "Tesseract",
    };
    public static IReadOnlyDictionary<string, string> EngineLabels { get; } = new Dictionary<string, string>
    {
        ["platform"] = "Platform OCR Engine",
        ["enhanced"] = "Enhanced OCR Engine",
    };
    public static IReadOnlyDictionary<string, string> LanguageLabels { get; } = new Dictionary<string, string>
    {
        ["heb+eng"]     = "Hebrew + English",
        ["heb"]         = "Hebrew only",
        ["eng"]         = "English only",
        ["ara+heb+eng"] = "Arabic + Hebrew + English",
    };

    // ── AI Extraction Engine ────────────────────────────────────────────
    [BindProperty] public bool   AiEnabled { get; set; } = false;
    [BindProperty] public string AiBaseUrl { get; set; } = "http://localhost:11434";
    [BindProperty] public string AiModel   { get; set; } = "phi3.5:latest";
    [BindProperty] public int    AiTimeout { get; set; } = 15;

    // ── Backup ──────────────────────────────────────────────────────────
    [BindProperty] public bool   BackupEnabled       { get; set; } = false;
    [BindProperty] public int    BackupIntervalHours { get; set; } = 24;
    [BindProperty] public int    BackupKeepCount     { get; set; } = 10;
    [BindProperty] public string BackupDirectory     { get; set; } = "backups";
    [BindProperty] public string RestoreBackupFile   { get; set; } = string.Empty;

    public string? LastBackupAt     { get; private set; }
    public string? NextBackupDue    { get; private set; }   // human-readable "in 3h 22m"
    public IReadOnlyList<BackupFileInfo> ExistingBackups { get; private set; } = [];

    public static IReadOnlyDictionary<int, string> BackupIntervalOptions { get; } =
        new Dictionary<int, string>
        {
            [6]   = "Every 6 hours",
            [12]  = "Every 12 hours",
            [24]  = "Daily (every 24 hours)",
            [48]  = "Every 2 days",
            [168] = "Weekly",
        };

    public SettingsModel(ISettingsRepository settings, IQuotaRepository quota,
                         LicenseInfo license, IConfiguration config, IBackupService backup)
    {
        _settings = settings;
        _quota    = quota;
        _license  = license;
        _config   = config;
        _backup   = backup;
    }

    public async Task OnGetAsync() => await LoadAsync();

    // ── Add admin ────────────────────────────────────────────────────────
    public async Task<IActionResult> OnPostAddAdminAsync()
    {
        await LoadAsync();
        var name = NewAdminUsername.Trim();
        if (string.IsNullOrEmpty(name))
        {
            ErrorMessage = "Username is required.";
            return Page();
        }
        if (string.IsNullOrEmpty(NewAdminPassword))
        {
            ErrorMessage = "Password is required.";
            return Page();
        }
        if (NewAdminPassword != NewAdminConfirm)
        {
            ErrorMessage = "Passwords do not match.";
            return Page();
        }

        var accounts = await AdminAccountHelper.LoadAccountsAsync(_settings);
        if (accounts.Count >= MaxAdmins)
        {
            ErrorMessage = $"Maximum of {MaxAdmins} admin accounts allowed.";
            return Page();
        }
        if (accounts.Any(a => string.Equals(a.Username, name, StringComparison.OrdinalIgnoreCase)))
        {
            ErrorMessage = $"Username '{name}' already exists.";
            return Page();
        }

        accounts.Add(new AdminAccount { Username = name, PasswordHash = HashPw(NewAdminPassword) });
        await AdminAccountHelper.SaveAccountsAsync(_settings, accounts);

        SuccessMessage = $"Admin account '{name}' created.";
        await LoadAsync();
        return Page();
    }

    // ── Remove admin ─────────────────────────────────────────────────────
    public async Task<IActionResult> OnPostRemoveAdminAsync()
    {
        var target     = RemoveUsername.Trim();
        var currentUser = User.Identity?.Name ?? "";

        if (string.Equals(target, currentUser, StringComparison.OrdinalIgnoreCase))
        {
            ErrorMessage = "You cannot remove your own account.";
            await LoadAsync();
            return Page();
        }

        var accounts = await AdminAccountHelper.LoadAccountsAsync(_settings);
        if (accounts.Count <= 1)
        {
            ErrorMessage = "At least one admin account must remain.";
            await LoadAsync();
            return Page();
        }

        accounts.RemoveAll(a => string.Equals(a.Username, target, StringComparison.OrdinalIgnoreCase));
        await AdminAccountHelper.SaveAccountsAsync(_settings, accounts);

        SuccessMessage = $"Admin account '{target}' removed.";
        await LoadAsync();
        return Page();
    }

    // ── Change current user's password ───────────────────────────────────
    public async Task<IActionResult> OnPostChangePasswordAsync()
    {
        var currentUser = User.Identity?.Name ?? "admin";
        var accounts    = await AdminAccountHelper.LoadAccountsAsync(_settings);
        var account     = accounts.FirstOrDefault(a =>
            string.Equals(a.Username, currentUser, StringComparison.OrdinalIgnoreCase));

        if (account is null || account.PasswordHash != HashPw(CurrentPassword))
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
        if (NewPassword.Length < 4)
        {
            ErrorMessage = "Password must be at least 4 characters.";
            await LoadAsync();
            return Page();
        }

        account.PasswordHash = HashPw(NewPassword);
        await AdminAccountHelper.SaveAccountsAsync(_settings, accounts);

        // Keep legacy key in sync for any external tooling
        if (string.Equals(currentUser, "admin", StringComparison.OrdinalIgnoreCase))
            await _settings.SetAsync("admin:passwordHash", HashPw(NewPassword), "Admin console password (SHA-256 hex)");

        SuccessMessage = "Password changed successfully.";
        await LoadAsync();
        return Page();
    }

    // ── Log retention ────────────────────────────────────────────────────
    public async Task<IActionResult> OnPostSaveLogRetentionAsync()
    {
        if (RetentionMonths < 1) RetentionMonths = 1;
        await _settings.SetAsync("Logging:RetentionMonths", RetentionMonths.ToString(), "Log retention in months");
        SuccessMessage = $"Log retention set to {RetentionMonths} month(s).";
        await LoadAsync();
        return Page();
    }

    // ── OCR settings ─────────────────────────────────────────────────────
    public async Task<IActionResult> OnPostSaveOcrAsync()
    {
        var e1 = KeyToEngine.GetValueOrDefault(OcrEngine1, "WindowsOcr");
        var e2 = KeyToEngine.GetValueOrDefault(OcrEngine2, "Tesseract");
        var engineOrder = e1 == e2 ? e1 : $"{e1},{e2}";

        await _settings.SetAsync("Ocr:EngineOrder",        engineOrder,                  "OCR engine order (comma-separated)");
        await _settings.SetAsync("Ocr:TesseractLanguages", OcrLanguages,                 "OCR recognition language string");
        await _settings.SetAsync("Ocr:MaxConcurrency",     OcrMaxConcurrency.ToString(), "Max concurrent OCR operations (0 = CPU count)");
        SuccessMessage = "OCR settings saved. Restart the service to apply engine or concurrency changes.";
        await LoadAsync();
        return Page();
    }

    // ── Backup settings ───────────────────────────────────────────────────
    public async Task<IActionResult> OnPostSaveBackupSettingsAsync()
    {
        await _settings.SetAsync("Backup:Enabled",       BackupEnabled ? "true" : "false", "Enable automatic scheduled backups");
        await _settings.SetAsync("Backup:IntervalHours", BackupIntervalHours.ToString(),   "Hours between automatic backups");
        await _settings.SetAsync("Backup:KeepCount",     BackupKeepCount.ToString(),       "Number of backup files to keep");
        await _settings.SetAsync("Backup:Directory",     BackupDirectory.Trim(),           "Directory for backup files");
        SuccessMessage = "Backup settings saved.";
        await LoadAsync();
        return Page();
    }

    // ── Create backup now ─────────────────────────────────────────────────
    public async Task<IActionResult> OnPostCreateBackupNowAsync()
    {
        var result = await _backup.CreateBackupAsync("manual");
        if (result.Success)
        {
            var keepStr  = await _settings.GetAsync("Backup:KeepCount");
            var keep     = int.TryParse(keepStr, out var k) && k > 0 ? k : 10;
            await _backup.PruneAsync(keep);
            SuccessMessage = $"Backup created: {result.FileName}";
        }
        else
        {
            ErrorMessage = $"Backup failed: {result.Error}";
        }
        await LoadAsync();
        return Page();
    }

    // ── Restore from backup list ──────────────────────────────────────────
    public async Task<IActionResult> OnPostRestoreFromBackupAsync()
    {
        if (string.IsNullOrWhiteSpace(RestoreBackupFile))
        {
            TempData["SettingsError"] = "No backup file specified.";
            return RedirectToPage(new { tab = "backup" });
        }
        try
        {
            await _backup.RestoreAsync(RestoreBackupFile);
        }
        catch (Exception ex)
        {
            TempData["SettingsError"] = $"Restore failed: {ex.Message}";
            return RedirectToPage(new { tab = "backup" });
        }
        TempData["DbRestored"] = "1";
        return RedirectToPage(new { tab = "backup" });
    }

    // ── Download a specific backup file ───────────────────────────────────
    public IActionResult OnGetDownloadBackupAsync(string f)
    {
        var fullPath = _backup.ResolveSafe(f);
        if (fullPath is null) return NotFound();
        return PhysicalFile(fullPath, "application/octet-stream", f);
    }

    // ── Database export ───────────────────────────────────────────────────
    public async Task<IActionResult> OnGetExportDatabaseAsync()
    {
        var dbPath = DbPath();
        if (!System.IO.File.Exists(dbPath))
        {
            TempData["SettingsError"] = "Database file not found.";
            return RedirectToPage(new { tab = "advanced" });
        }

        // Flush WAL journal into the main file before reading bytes
        try
        {
            using var conn = new SqliteConnection($"Data Source={dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA wal_checkpoint(FULL)";
            cmd.ExecuteNonQuery();
        }
        catch { /* non-fatal — export the file as-is */ }

        var bytes    = await System.IO.File.ReadAllBytesAsync(dbPath);
        var fileName = $"piiremovals_backup_{DateTime.Now:yyyyMMdd_HHmmss}.db";
        return File(bytes, "application/octet-stream", fileName);
    }

    // ── Database import ───────────────────────────────────────────────────
    public async Task<IActionResult> OnPostImportDatabaseAsync(IFormFile? dbFile)
    {
        if (dbFile is null || dbFile.Length == 0)
        {
            TempData["SettingsError"] = "No file selected. Please choose a .db backup file.";
            return RedirectToPage(new { tab = "advanced" });
        }

        // Validate SQLite magic header: first 16 bytes must be "SQLite format 3\0"
        using var stream = dbFile.OpenReadStream();
        var magic    = new byte[16];
        var magicRead = await stream.ReadAsync(magic);
        var expected  = new byte[] { 83,81,76,105,116,101,32,102,111,114,109,97,116,32,51,0 }; // "SQLite format 3\0"
        if (magicRead < 16 || !magic.SequenceEqual(expected))
        {
            TempData["SettingsError"] = "Invalid file. Only a valid PiiRemover database backup (.db) file can be imported.";
            return RedirectToPage(new { tab = "advanced" });
        }

        var dbPath = DbPath();

        // Save a .bak of the current DB before overwriting
        if (System.IO.File.Exists(dbPath))
            System.IO.File.Copy(dbPath, dbPath + ".bak", overwrite: true);

        // Release all pooled SQLite connections so the file can be replaced
        SqliteConnection.ClearAllPools();

        // Write the uploaded file
        stream.Seek(0, SeekOrigin.Begin);
        await using var fs = new FileStream(dbPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await stream.CopyToAsync(fs);

        // Remove stale WAL / SHM sidecar files that belonged to the old database
        foreach (var sidecar in new[] { dbPath + "-wal", dbPath + "-shm" })
            if (System.IO.File.Exists(sidecar)) System.IO.File.Delete(sidecar);

        TempData["DbRestored"] = "1";
        return RedirectToPage(new { tab = "advanced" });
    }

    private string DbPath() => _config["Database:Path"] ?? "piiremovals.db";

    // ── AI Extraction Engine handlers ────────────────────────────────────
    public async Task<IActionResult> OnPostAiModelsAsync()
    {
        try
        {
            var http    = HttpContext.RequestServices.GetRequiredService<IHttpClientFactory>().CreateClient();
            var baseUrl = await _settings.GetAsync("ai:baseUrl") ?? "http://localhost:11434";
            http.Timeout = TimeSpan.FromSeconds(5);
            var resp = await http.GetAsync($"{baseUrl.TrimEnd('/')}/api/tags");
            if (!resp.IsSuccessStatusCode)
                return new JsonResult(new { ok = false, error = $"HTTP {(int)resp.StatusCode}" });
            var json = await resp.Content.ReadAsStringAsync();
            var doc  = System.Text.Json.JsonDocument.Parse(json);
            var models = new List<string>();
            if (doc.RootElement.TryGetProperty("models", out var arr))
                foreach (var m in arr.EnumerateArray())
                    if (m.TryGetProperty("name", out var nm)) models.Add(nm.GetString() ?? "");
            if (models.Count == 0)
                return new JsonResult(new { ok = false, error = "No models found. Download a model first (see 📥 Install AI Engine below)." });
            return new JsonResult(new { ok = true, models });
        }
        catch (Exception ex)
        {
            return new JsonResult(new { ok = false, error = ex.Message });
        }
    }

    public async Task<IActionResult> OnPostSaveAiSettingsAsync()
    {
        await _settings.SetAsync("ai:enabled",       AiEnabled ? "true" : "false", "AI Extraction Engine enabled");
        await _settings.SetAsync("ai:baseUrl",        AiBaseUrl,                   "AI engine base URL");
        await _settings.SetAsync("ai:model",          AiModel,                     "AI model name");
        await _settings.SetAsync("ai:timeoutSeconds", AiTimeout.ToString(),        "AI request timeout (seconds)");
        TempData["SettingsSuccess"] = "AI settings saved.";
        return RedirectToPage(new { tab = "ai" });
    }

    public async Task<IActionResult> OnPostTestAiConnectionAsync()
    {
        try
        {
            var http = HttpContext.RequestServices.GetRequiredService<IHttpClientFactory>().CreateClient();
            var baseUrl = await _settings.GetAsync("ai:baseUrl") ?? "http://localhost:11434";
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var resp = await http.GetAsync($"{baseUrl.TrimEnd('/')}/api/tags");
            sw.Stop();
            if (!resp.IsSuccessStatusCode)
                return new JsonResult(new { ok = false, error = $"HTTP {(int)resp.StatusCode}" });
            var json = await resp.Content.ReadAsStringAsync();
            var doc = System.Text.Json.JsonDocument.Parse(json);
            var models = new List<string>();
            if (doc.RootElement.TryGetProperty("models", out var arr))
                foreach (var m in arr.EnumerateArray())
                    if (m.TryGetProperty("name", out var nm)) models.Add(nm.GetString() ?? "");
            return new JsonResult(new { ok = true, models, latencyMs = (int)sw.ElapsedMilliseconds });
        }
        catch (Exception ex)
        {
            return new JsonResult(new { ok = false, error = ex.Message });
        }
    }

    // ── Load ─────────────────────────────────────────────────────────────
    private async Task LoadAsync()
    {
        QuotaUsed    = await _quota.GetUsedAsync();
        AllSettings  = await _settings.GetAllAsync();

        var accounts = await AdminAccountHelper.LoadAccountsAsync(_settings);
        AdminUsernames = accounts.Select(a => a.Username).ToList();

        var retStr = await _settings.GetAsync("Logging:RetentionMonths");
        RetentionMonths = int.TryParse(retStr, out var r) ? r : 1;

        var eng = await _settings.GetAsync("Ocr:EngineOrder");
        if (!string.IsNullOrWhiteSpace(eng))
        {
            var parts = eng.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            OcrEngine1 = parts.Length > 0 ? EngineToKey.GetValueOrDefault(parts[0], "platform") : "platform";
            OcrEngine2 = parts.Length > 1 ? EngineToKey.GetValueOrDefault(parts[1], "enhanced") : "enhanced";
        }
        var tl = await _settings.GetAsync("Ocr:TesseractLanguages");
        if (!string.IsNullOrWhiteSpace(tl)) OcrLanguages = tl;
        var mc = await _settings.GetAsync("Ocr:MaxConcurrency");
        if (int.TryParse(mc, out var mci)) OcrMaxConcurrency = mci;

        // AI settings
        AiEnabled = string.Equals(await _settings.GetAsync("ai:enabled"), "true", StringComparison.OrdinalIgnoreCase);
        var aiUrl = await _settings.GetAsync("ai:baseUrl");
        if (!string.IsNullOrWhiteSpace(aiUrl)) AiBaseUrl = aiUrl;
        var aiModel = await _settings.GetAsync("ai:model");
        if (!string.IsNullOrWhiteSpace(aiModel)) AiModel = aiModel;
        var aiTo = await _settings.GetAsync("ai:timeoutSeconds");
        if (int.TryParse(aiTo, out var to) && to > 0) AiTimeout = to;

        // Backup settings
        BackupEnabled       = string.Equals(await _settings.GetAsync("Backup:Enabled"), "true", StringComparison.OrdinalIgnoreCase);
        var bih = await _settings.GetAsync("Backup:IntervalHours");
        if (int.TryParse(bih, out var ih) && ih > 0) BackupIntervalHours = ih;
        var bkc = await _settings.GetAsync("Backup:KeepCount");
        if (int.TryParse(bkc, out var kc) && kc > 0) BackupKeepCount = kc;
        var bdir = await _settings.GetAsync("Backup:Directory");
        if (!string.IsNullOrWhiteSpace(bdir)) BackupDirectory = bdir;

        LastBackupAt    = await _settings.GetAsync("Backup:LastBackupAt");
        NextBackupDue   = ComputeNextDue(LastBackupAt, BackupIntervalHours);
        ExistingBackups = await _backup.ListBackupsAsync();
    }

    private static string ComputeNextDue(string? lastStr, int intervalHours)
    {
        if (!DateTime.TryParse(lastStr, out var last)) return "no backup recorded yet";
        var next = last.AddHours(intervalHours);
        var diff = next - DateTime.UtcNow;
        if (diff <= TimeSpan.Zero) return "due now";
        if (diff.TotalMinutes < 60) return $"in {(int)diff.TotalMinutes}m";
        return $"in {(int)diff.TotalHours}h {diff.Minutes}m";
    }

    private static string HashPw(string pw) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(pw))).ToLowerInvariant();
}
