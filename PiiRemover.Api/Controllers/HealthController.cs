using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using PiiRemover.Core.Licensing;

namespace PiiRemover.Api.Controllers;

[ApiController]
[Route("api/v1/health")]
public class HealthController : ControllerBase
{
    private static readonly DateTimeOffset _startTime = DateTimeOffset.UtcNow;

    private readonly LicenseInfo _license;
    private readonly IConfiguration _cfg;

    public HealthController(LicenseInfo license, IConfiguration cfg)
    {
        _license = license;
        _cfg     = cfg;
    }

    // GET /api/v1/health — no API-key required (monitoring / load-balancer probe)
    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var dbOk = await PingDbAsync();
        var licenseExpiry = _license.ExpiryDate;
        var daysLeft = licenseExpiry.DayNumber - DateOnly.FromDateTime(DateTime.UtcNow).DayNumber;
        var licenseStatus = licenseExpiry == DateOnly.MinValue ? "missing"
                          : daysLeft <= 0                      ? "expired"
                          : daysLeft <= 30                     ? "expiring_soon"
                                                               : "ok";

        var healthy = dbOk && licenseStatus is "ok" or "expiring_soon";

        var result = new
        {
            status        = healthy ? "healthy" : "degraded",
            version       = typeof(HealthController).Assembly.GetName().Version?.ToString() ?? "0.0.0",
            uptimeSeconds = (long)(DateTimeOffset.UtcNow - _startTime).TotalSeconds,
            db            = dbOk ? "ok" : "error",
            license = new
            {
                status  = licenseStatus,
                expiry  = licenseExpiry == DateOnly.MinValue ? null : (DateOnly?)licenseExpiry,
                daysLeft = licenseExpiry == DateOnly.MinValue ? (int?)null : (int?)daysLeft,
            },
        };

        return healthy ? Ok(result) : StatusCode(503, result);
    }

    private async Task<bool> PingDbAsync()
    {
        try
        {
            var connStr = _cfg.GetConnectionString("Default")
                          ?? $"Data Source={_cfg["Database:Path"] ?? "piiremovals.db"};Mode=ReadOnly";
            await using var conn = new SqliteConnection(connStr);
            await conn.OpenAsync();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1";
            await cmd.ExecuteScalarAsync();
            return true;
        }
        catch
        {
            return false;
        }
    }
}
