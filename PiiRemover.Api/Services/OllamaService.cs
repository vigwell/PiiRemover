using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PiiRemover.Core.Engines;
using PiiRemover.Data.Repositories;

namespace PiiRemover.Api.Services;

/// <summary>
/// HTTP client wrapper for a locally-running AI inference server.
/// Admin-facing UI calls this "AI Extraction Engine" — the underlying brand is not exposed.
/// Settings are read from the DB (ai:baseUrl, ai:model, ai:timeoutSeconds, ai:enabled)
/// so the admin can reconfigure at runtime without restarting the service.
/// Results are cached in-memory (up to 500 entries) so repeated processing of the same
/// document+description combination returns instantly without an AI round-trip.
/// </summary>
public class OllamaService : IAiService
{
    private readonly HttpClient _http;
    private readonly ISettingsRepository _settings;

    // DB setting keys — internal only, not shown in UI
    public const string KeyEnabled     = "ai:enabled";
    public const string KeyBaseUrl     = "ai:baseUrl";
    public const string KeyModel       = "ai:model";
    public const string KeyTimeoutSecs = "ai:timeoutSeconds";

    // Hard-coded defaults — seeded into DB on first use
    public const string DefaultBaseUrl     = "http://localhost";
    public const string DefaultModel       = "mistral:latest";
    public const string DefaultTimeoutSecs = "10";  // tighter default: fail fast if AI is slow

    // In-memory result cache: SHA256(text)+description → entities
    // Bounded at 500 entries; evicts oldest 50 when full.
    private readonly ConcurrentDictionary<string, (List<string> entities, long tick)> _cache = new();
    private const int CacheMax  = 500;
    private const int CacheEvict = 50;

    public OllamaService(HttpClient http, ISettingsRepository settings)
    {
        _http     = http;
        _settings = settings;
    }

    /// <summary>Returns true if the AI engine is enabled in the DB settings.</summary>
    public async Task<bool> IsEnabledAsync()
    {
        var val = await _settings.GetAsync(KeyEnabled);
        if (val is null)
        {
            await _settings.SetAsync(KeyEnabled, "false", "AI Extraction Engine enabled");
            return false;
        }
        return val.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Extracts all entity strings matching <paramref name="description"/> from <paramref name="text"/>.
    /// Returns empty list gracefully if AI engine is unreachable or times out.
    /// Results are cached in-memory — repeated calls with the same text+description return instantly.
    /// </summary>
    public async Task<List<string>> ExtractEntitiesAsync(
        string text, string description, CancellationToken ct = default)
    {
        // Skip trivially short text — nothing meaningful to find
        if (text.Length < 20) return [];

        var cacheKey = BuildCacheKey(text, description);
        if (_cache.TryGetValue(cacheKey, out var hit))
            return hit.entities;

        var baseUrl    = await GetSettingAsync(KeyBaseUrl,     DefaultBaseUrl);
        var model      = await GetSettingAsync(KeyModel,       DefaultModel);
        var timeoutStr = await GetSettingAsync(KeyTimeoutSecs, DefaultTimeoutSecs);
        var timeout    = int.TryParse(timeoutStr, out var t) ? t : 10;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeout));

        var results = new List<string>();
        foreach (var chunk in ChunkText(text, 3000, 200))
        {
            var chunkResults = await CallAiAsync(baseUrl, model, chunk, description, cts.Token);
            results.AddRange(chunkResults);
        }

        var entities = results
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        StoreInCache(cacheKey, entities);
        return entities;
    }

    private static string BuildCacheKey(string text, string description)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        return $"{hash}|{description}";
    }

    private void StoreInCache(string key, List<string> entities)
    {
        if (_cache.Count >= CacheMax)
        {
            // Evict the oldest CacheEvict entries by tick
            var oldest = _cache
                .OrderBy(kv => kv.Value.tick)
                .Take(CacheEvict)
                .Select(kv => kv.Key)
                .ToList();
            foreach (var k in oldest) _cache.TryRemove(k, out _);
        }
        _cache[key] = (entities, Environment.TickCount64);
    }

    /// <summary>Pings the AI engine and returns status info for the admin health card.</summary>
    public async Task<OllamaHealthResult> CheckHealthAsync(CancellationToken ct = default)
    {
        var baseUrl = await GetSettingAsync(KeyBaseUrl, DefaultBaseUrl);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl.TrimEnd('/')}/api/tags");
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            sw.Stop();
            if (!resp.IsSuccessStatusCode)
                return new OllamaHealthResult { Ok = false, Error = $"HTTP {(int)resp.StatusCode}" };

            var json = await resp.Content.ReadAsStringAsync(ct);
            var doc  = JsonDocument.Parse(json);
            var models = new List<string>();
            if (doc.RootElement.TryGetProperty("models", out var arr))
                foreach (var m in arr.EnumerateArray())
                    if (m.TryGetProperty("name", out var nm))
                        models.Add(nm.GetString() ?? "");

            return new OllamaHealthResult { Ok = true, Models = models, LatencyMs = (int)sw.ElapsedMilliseconds };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new OllamaHealthResult { Ok = false, Error = ex.Message };
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task<List<string>> CallAiAsync(
        string baseUrl, string model, string textChunk, string description, CancellationToken ct)
    {
        var requestBody = new OllamaGenerateRequest
        {
            Model       = model,
            Prompt      = BuildPrompt(textChunk, description),
            Stream      = false,
            Temperature = 0.05,
            NumPredict  = 200
        };

        try
        {
            var response = await _http.PostAsJsonAsync(
                $"{baseUrl.TrimEnd('/')}/api/generate", requestBody, ct);
            if (!response.IsSuccessStatusCode) return [];

            var result = await response.Content.ReadFromJsonAsync<OllamaGenerateResponse>(
                cancellationToken: ct);
            if (result?.Response is null) return [];

            return result.Response
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim().TrimStart('-', '*', '•', '·').Trim())
                .Where(s => s.Length > 1)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static string BuildPrompt(string text, string description) => $"""
You extract personal data from Hebrew and English documents.
Find all occurrences of: '{description}'
Return ONLY the extracted values, one per line, no explanations, no numbering.

Examples:
- Query: 'patient full name'    Text: 'Patient: David Cohen'   → David Cohen
- Query: 'Israeli ID number'    Text: 'ת.ז. 123456789'         → 123456789
- Query: 'phone number'         Text: 'טל: 050-1234567'        → 050-1234567
- Query: 'doctor name'          Text: 'Dr. Sarah Levy'         → Sarah Levy

Text:
{text}

Extracted:
""";

    private static IEnumerable<string> ChunkText(string text, int chunkSize, int overlap)
    {
        if (text.Length <= chunkSize) { yield return text; yield break; }
        var pos = 0;
        while (pos < text.Length)
        {
            var len = Math.Min(chunkSize, text.Length - pos);
            yield return text.Substring(pos, len);
            pos += chunkSize - overlap;
        }
    }

    private async Task<string> GetSettingAsync(string dbKey, string defaultValue)
    {
        var val = await _settings.GetAsync(dbKey);
        if (val is not null) return val;
        await _settings.SetAsync(dbKey, defaultValue);
        return defaultValue;
    }
}

// ── DTOs ──────────────────────────────────────────────────────────────────────

internal sealed class OllamaGenerateRequest
{
    [JsonPropertyName("model")]       public string Model       { get; set; } = "";
    [JsonPropertyName("prompt")]      public string Prompt      { get; set; } = "";
    [JsonPropertyName("stream")]      public bool   Stream      { get; set; } = false;
    [JsonPropertyName("temperature")] public double Temperature { get; set; } = 0.05;
    [JsonPropertyName("num_predict")] public int    NumPredict  { get; set; } = 200;
}

internal sealed class OllamaGenerateResponse
{
    [JsonPropertyName("response")] public string? Response { get; set; }
    [JsonPropertyName("done")]     public bool    Done     { get; set; }
}

public sealed class OllamaHealthResult
{
    public bool         Ok        { get; set; }
    public string?      Error     { get; set; }
    public List<string> Models    { get; set; } = [];
    public int          LatencyMs { get; set; }
}
