using System.Text.Json;
using PiiRemover.Core.Licensing;
using PiiRemover.Data.Repositories;

namespace PiiRemover.Api.Middleware;

public class LicenseMiddleware
{
    private readonly RequestDelegate _next;
    private readonly LicenseInfo _license;

    public LicenseMiddleware(RequestDelegate next, LicenseInfo license)
    {
        _next    = next;
        _license = license;
    }

    public async Task InvokeAsync(HttpContext context, IQuotaRepository quota)
    {
        if (!context.Request.Path.StartsWithSegments("/api"))
        {
            await _next(context);
            return;
        }

        // Expiry check
        if (_license.ExpiryDate < DateOnly.FromDateTime(DateTime.UtcNow))
        {
            await WriteError(context, 402, "license_expired",
                $"License expired on {_license.ExpiryDate:yyyy-MM-dd}. Please contact your vendor to renew.",
                new { expiredOn = _license.ExpiryDate.ToString("yyyy-MM-dd") });
            return;
        }

        // Quota check (0 = unlimited)
        if (_license.RequestQuota > 0)
        {
            var used = await quota.GetUsedAsync();
            if (used >= _license.RequestQuota)
            {
                await WriteError(context, 402, "quota_exhausted",
                    $"Request quota of {_license.RequestQuota:N0} has been exhausted. Please contact your vendor to upgrade.",
                    new { quotaLimit = _license.RequestQuota, used });
                return;
            }
        }

        await _next(context);
    }

    private static Task WriteError(HttpContext ctx, int status, string code, string message, object? extra = null)
    {
        ctx.Response.StatusCode  = status;
        ctx.Response.ContentType = "application/json";
        return ctx.Response.WriteAsync(JsonSerializer.Serialize(new { error = code, message, extra }));
    }
}
