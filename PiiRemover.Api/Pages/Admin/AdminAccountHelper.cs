using System.Text.Json;
using PiiRemover.Data.Repositories;

namespace PiiRemover.Api.Pages.Admin;

/// <summary>
/// Persists admin accounts as a JSON array in the Settings table under key "admin:accounts".
/// Automatically migrates from the legacy single-admin key "admin:passwordHash" on first use.
/// </summary>
public static class AdminAccountHelper
{
    private const string Key         = "admin:accounts";
    private const string LegacyKey   = "admin:passwordHash";
    private const string Description = "Admin user accounts (username + hashed password)";

    public sealed class AdminAccount
    {
        public string Username     { get; set; } = string.Empty;
        public string PasswordHash { get; set; } = string.Empty;
    }

    private static readonly JsonSerializerOptions _opts = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Loads all admin accounts. Migrates from legacy single-admin setting if needed.</summary>
    public static async Task<List<AdminAccount>> LoadAccountsAsync(ISettingsRepository settings)
    {
        var json = await settings.GetAsync(Key);
        if (!string.IsNullOrWhiteSpace(json))
        {
            try { return JsonSerializer.Deserialize<List<AdminAccount>>(json, _opts) ?? []; }
            catch { /* fall through to migration */ }
        }

        // ── Migrate from legacy single-admin entry ──────────────────────
        var legacyHash = await settings.GetAsync(LegacyKey);
        if (!string.IsNullOrWhiteSpace(legacyHash))
        {
            var migrated = new List<AdminAccount> { new() { Username = "admin", PasswordHash = legacyHash } };
            await SaveAccountsAsync(settings, migrated);
            return migrated;
        }

        return [];
    }

    /// <summary>Persists the accounts list.</summary>
    public static async Task SaveAccountsAsync(ISettingsRepository settings, List<AdminAccount> accounts)
    {
        var json = JsonSerializer.Serialize(accounts);
        await settings.SetAsync(Key, json, Description);
    }
}
