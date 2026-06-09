using System.Security.Cryptography;
using System.Text;
using PiiRemover.Data.Repositories;

namespace PiiRemover.Api.Middleware;

public class ApiKeyMiddleware
{
    private readonly RequestDelegate _next;

    public ApiKeyMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, IClientRepository clients)
    {
        if (!context.Request.Path.StartsWithSegments("/api"))
        {
            await _next(context);
            return;
        }

        // Public endpoints — no API key required
        if (context.Request.Path.StartsWithSegments("/api/v1/util") ||
            context.Request.Path.StartsWithSegments("/api/v1/health"))
        {
            await _next(context);
            return;
        }

        if (!context.Request.Headers.TryGetValue("X-Api-Key", out var rawKey) || string.IsNullOrWhiteSpace(rawKey))
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsync("Missing X-Api-Key header.");
            return;
        }

        var hash = HashKey(rawKey.ToString());
        var client = await clients.GetByApiKeyHashAsync(hash);
        if (client is null)
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsync("Invalid or inactive API key.");
            return;
        }

        context.Items["ClientId"] = client.Id;
        context.Items["ClientName"] = client.Name;
        await _next(context);
    }

    public static string HashKey(string rawKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawKey));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
