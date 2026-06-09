using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PiiRemover.Core.Licensing;
using PiiRemover.Core.Logging;
using PiiRemover.Data;
using PiiRemover.Data.Repositories;

namespace PiiRemover.Tests.Infrastructure;

/// <summary>
/// In-memory test host — uses a temp SQLite DB and a never-expiring stub license.
/// No Windows Event Log writes; logging is silenced.
/// </summary>
public class PiiWebAppFactory : WebApplicationFactory<Program>
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"piitest_{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Test");

        // Override DB path so Program.cs startup writes to the temp DB, not piiremovals.db
        builder.ConfigureAppConfiguration((_, cfg) =>
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Path"]    = _dbPath,
                ["License:FilePath"] = "nonexistent-test.lic" // triggers graceful stub path
            }));

        builder.ConfigureServices(services =>
        {
            // ── Swap license for an always-valid stub ─────────────────────────
            services.RemoveAll<LicenseInfo>();
            services.AddSingleton(new LicenseInfo
            {
                OrgName      = "Test Org",
                LicenseId    = "test-license",
                IssuedDate   = DateOnly.FromDateTime(DateTime.UtcNow),
                ExpiryDate   = DateOnly.FromDateTime(DateTime.UtcNow.AddYears(10)),
                RequestQuota = 0,
                MaxClients   = 999,
                Features     = ["ocr", "pdf", "redact"]
            });

            // ── Swap SQLite repos to use a temp DB ────────────────────────────
            var cs = $"Data Source={_dbPath};Cache=Shared";
            new SchemaInitializer(cs).Initialize();
            AdminSeeder.SeedAdminPassword(cs);
            PiiDataSeeder.Seed(cs);

            services.RemoveAll<IClientRepository>();
            services.RemoveAll<IFieldRepository>();
            services.RemoveAll<ILogRepository>();
            services.RemoveAll<IQuotaRepository>();
            services.RemoveAll<ISettingsRepository>();

            services.AddSingleton<IClientRepository>(_ => new ClientRepository(cs));
            services.AddSingleton<IFieldRepository>(_ => new FieldRepository(cs));
            services.AddSingleton<ILogRepository>(_ => new LogRepository(cs));
            services.AddSingleton<IQuotaRepository>(_ => new QuotaRepository(cs));
            services.AddSingleton<ISettingsRepository>(_ => new SettingsRepository(cs));

            // ── Silence logger (no Windows Event Log in CI) ───────────────────
            services.RemoveAll<IPiiLogger>();
            services.AddSingleton<IPiiLogger, NullPiiLogger>();
        });
    }

    // Retrieve the demo API key hash for auth headers in tests
    public static string DemoApiKey => "demo-api-key-changeme-12345";

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { /* best-effort */ }
        try { if (File.Exists(_dbPath + "-wal")) File.Delete(_dbPath + "-wal"); } catch { /* best-effort */ }
        try { if (File.Exists(_dbPath + "-shm")) File.Delete(_dbPath + "-shm"); } catch { /* best-effort */ }
    }
}

/// <summary>No-op logger — discards all events during tests.</summary>
file class NullPiiLogger : IPiiLogger
{
    public void LogRequest(PiiRequestLog entry) { }
    public void LogError(string operation, string? clientName, Exception ex, string? extraInfo = null) { }
    public void LogInfo(string message) { }
}
