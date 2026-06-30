using System.Net.Http.Json;
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
    public const string DefaultBaseUrl              = "http://localhost:11434";
    public const string DefaultModel                = "gemma3:1b";
    public const string DefaultTimeoutSecs          = "60";  // LLM can take 20-40 s on cold model load
    public const string KeyWarmupEnabled            = "ai:warmupEnabled";
    public const string KeyWarmupIntervalMinutes    = "ai:warmupIntervalMinutes";
    public const string KeyWarmupLastAt             = "ai:warmupLastAt";
    public const string DefaultWarmupIntervalMinutes = "4";  // just under Ollama's 5-min default keep_alive

    // Per-request scope: flows with the async execution context via AsyncLocal.
    // Set by PrefetchAsync (before first await), read by ExtractEntitiesAsync, cleared by ClearScope().
    // AsyncLocal ensures each concurrent request has its own isolated scope without any thread-ID tricks.
    private static readonly AsyncLocal<Dictionary<string, List<string>>?> _requestScope = new();

    // Enabled-flag cache — avoids a DB hit on every redaction call.
    // Invalidated explicitly when settings are saved; falls back to TTL as a safety net.
    private bool _enabledCache;
    private long _enabledCachedAt = 0; // 0 = never cached (any real TickCount64 > TTL)
    private const long EnabledCacheTtlMs = 60_000; // 1 minute safety-net TTL

    public OllamaService(HttpClient http, ISettingsRepository settings)
    {
        _http     = http;
        _settings = settings;
    }

    /// <summary>Returns true if the AI engine is enabled in the DB settings.</summary>
    /// <remarks>Result is cached in memory; call <see cref="InvalidateEnabledCache"/> after saving settings.</remarks>
    public async Task<bool> IsEnabledAsync()
    {
        if (Environment.TickCount64 - _enabledCachedAt < EnabledCacheTtlMs)
            return _enabledCache;

        var val = await _settings.GetAsync(KeyEnabled);
        if (val is null)
            await _settings.SetAsync(KeyEnabled, "false", "AI Extraction Engine enabled");

        _enabledCache    = val?.Equals("true", StringComparison.OrdinalIgnoreCase) ?? false;
        _enabledCachedAt = Environment.TickCount64;
        return _enabledCache;
    }

    /// <summary>Call this after saving AI settings so the next redaction sees the new value immediately.</summary>
    public void InvalidateEnabledCache() => _enabledCachedAt = 0;

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

        // Check request scope — read AsyncLocal value before any await
        var reqScope = _requestScope.Value;
        if (reqScope != null && reqScope.TryGetValue(description, out var scoped))
            return scoped;

        var baseUrl    = await GetSettingAsync(KeyBaseUrl,     DefaultBaseUrl);
        var model      = await GetSettingAsync(KeyModel,       DefaultModel);
        var timeoutStr = await GetSettingAsync(KeyTimeoutSecs, DefaultTimeoutSecs);
        var timeout    = int.TryParse(timeoutStr, out var t) ? t : 10;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeout));

        var results = new List<string>();
        foreach (var chunk in ChunkText(text, 2000, 150))
        {
            var chunkResults = await CallAiAsync(baseUrl, model, chunk, description, cts.Token);
            results.AddRange(chunkResults);
        }

        return results
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Clears the per-request result scope. Called by RedactionOrchestrator after Redact() completes.</summary>
    public void ClearScope() => _requestScope.Value = null;

    /// <summary>
    /// Extracts all requested entity types in a single batched Ollama call and pre-populates the cache.
    /// Called by RedactionOrchestrator before the per-pattern loop so that N LlmPrompt fields cost one
    /// AI round-trip instead of N.
    /// </summary>
    public async Task PrefetchAsync(string text, IList<string> descriptions, CancellationToken ct = default)
    {
        // Create the scope dictionary BEFORE the first await so it is set in the calling
        // execution context and remains visible after GetAwaiter().GetResult() returns.
        // AsyncLocal flows DOWN into async continuations; mutations to the dict are shared.
        var scope = _requestScope.Value
                    ?? new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        _requestScope.Value = scope;

        if (!await IsEnabledAsync()) return;
        if (descriptions == null || descriptions.Count == 0) return;

        // Skip descriptions already in scope for this request
        var pending = descriptions
            .Where(d => !string.IsNullOrWhiteSpace(d) && !scope.ContainsKey(d))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (pending.Count == 0) return;

        // Load settings in parallel — three independent DB reads
        var baseUrlTask    = GetSettingAsync(KeyBaseUrl,     DefaultBaseUrl);
        var modelTask      = GetSettingAsync(KeyModel,       DefaultModel);
        var timeoutStrTask = GetSettingAsync(KeyTimeoutSecs, DefaultTimeoutSecs);
        await Task.WhenAll(baseUrlTask, modelTask, timeoutStrTask).ConfigureAwait(false);
        var baseUrl    = await baseUrlTask;
        var model      = await modelTask;
        var timeoutStr = await timeoutStrTask;
        var timeout    = int.TryParse(timeoutStr, out var t) ? t : 60;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeout));

        foreach (var chunk in ChunkText(text, 2000, 150))
        {
            var results = await CallBatchAiAsync(baseUrl, model, chunk, pending, cts.Token).ConfigureAwait(false);
            for (int i = 0; i < pending.Count; i++)
            {
                var entities = (i < results.Count ? results[i] : [])
                    .Select(s => s.Trim())
                    .Where(s => s.Length > 0)
                    .ToList();
                var desc = pending[i];
                if (scope.TryGetValue(desc, out var existing))
                    entities = existing.Concat(entities)
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                scope[desc] = entities;
            }
        }
    }


    private async Task<List<List<string>>> CallBatchAiAsync(
        string baseUrl, string model, string textChunk, IList<string> descriptions, CancellationToken ct)
    {
        var numbered = string.Join("\n", descriptions.Select((d, i) => $"{i + 1}. {d}"));
        var prompt = $"""
You are a data extraction tool. From the document below, extract exact values for each numbered category.

STRICT OUTPUT FORMAT — no deviations:
- Under each category heading write the raw value copied from the document, one per line
- Do NOT write sentences, explanations, labels, or commentary of any kind
- Do NOT rephrase, summarize, or translate — copy text exactly as it appears
- Write NONE if a category is not found in the document

Categories:
{numbered}

---
{textChunk}
---

Results (EXACT FORMAT — number, category, colon, then values):
""";


        var requestBody = new OllamaGenerateRequest
        {
            Model      = model,
            Prompt     = prompt,
            Stream     = false,
            NumPredict = Math.Max(120, 60 * descriptions.Count)
        };

        var result = new List<List<string>>(descriptions.Count);
        for (int i = 0; i < descriptions.Count; i++) result.Add([]);

        try
        {
            var response = await _http.PostAsJsonAsync(
                $"{baseUrl.TrimEnd('/')}/api/generate", requestBody, ct);
            if (!response.IsSuccessStatusCode) return result;

            var generated = await response.Content.ReadFromJsonAsync<OllamaGenerateResponse>(
                cancellationToken: ct);
            if (generated?.Response is null) return result;

            // Line-by-line parser — handles both:
            //   "1. category: value"   (inline value)
            //   "1. category:\nvalue"  (value on next line)
            var headerRx = new System.Text.RegularExpressions.Regex(@"^(\d+)\.\s*(.*)");
            int currentIdx = -1;
            foreach (var rawLine in generated.Response.Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.Length == 0) continue;

                var hm = headerRx.Match(line);
                if (hm.Success && int.TryParse(hm.Groups[1].Value, out var n))
                {
                    currentIdx = n - 1; // 1-based → 0-based
                    if (currentIdx < 0 || currentIdx >= descriptions.Count) { currentIdx = -1; continue; }

                    // Extract inline value after colon, if present
                    var rest = hm.Groups[2].Value;
                    var colon = rest.IndexOf(':');
                    var inline = (colon >= 0 ? rest[(colon + 1)..] : rest).Trim()
                                    .TrimStart('-', '*', '•', '·').Trim();
                    if (inline.Length > 1 && !inline.Equals("NONE", StringComparison.OrdinalIgnoreCase))
                        result[currentIdx].Add(inline);
                    continue;
                }

                // Value line following a section header
                if (currentIdx >= 0)
                {
                    var val = line.TrimStart('-', '*', '•', '·').Trim();
                    if (val.Length > 1 && !val.Equals("NONE", StringComparison.OrdinalIgnoreCase))
                        result[currentIdx].Add(val);
                }
            }
        }
        catch { /* graceful — each description stays empty */ }

        return result;
    }

    /// <summary>
    /// Sends a minimal single-token request to keep the model loaded in Ollama memory.
    /// Sets keep_alive to (intervalMinutes + 2) so the model stays hot between ticks.
    /// Returns true on success, false on any error.
    /// </summary>
    public async Task<bool> WarmupAsync(int intervalMinutes = 4, CancellationToken ct = default)
    {
        try
        {
            var baseUrl = await GetSettingAsync(KeyBaseUrl, DefaultBaseUrl).ConfigureAwait(false);
            var model   = await GetSettingAsync(KeyModel,   DefaultModel).ConfigureAwait(false);

            // keep_alive expressed as "<n>m" — model stays loaded this long after each ping.
            var keepAlive = $"{intervalMinutes + 2}m";

            var requestBody = new OllamaGenerateRequest
            {
                Model      = model,
                Prompt     = "hi",   // minimal prompt — just enough to confirm the model is loaded
                Stream     = false,
                NumPredict = 1,
                KeepAlive  = keepAlive,
            };

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(90)); // generous — cold load can be slow

            var response = await _http.PostAsJsonAsync(
                $"{baseUrl.TrimEnd('/')}/api/generate", requestBody, cts.Token).ConfigureAwait(false);

            return response.IsSuccessStatusCode;
        }
        catch { return false; }
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

    /// <summary>
    /// Direct extraction call for the admin tester — bypasses the scope/prefetch mechanism
    /// and accepts an explicit model name so the tester can compare different models.
    /// </summary>
    public async Task<(List<string> Values, string PromptSent)> TestExtractAsync(
        string text, string description, string? modelOverride = null, CancellationToken ct = default)
    {
        if (text.Length < 5) return ([], "");
        var baseUrl    = await GetSettingAsync(KeyBaseUrl,     DefaultBaseUrl).ConfigureAwait(false);
        var model      = modelOverride ?? await GetSettingAsync(KeyModel, DefaultModel).ConfigureAwait(false);
        var timeoutStr = await GetSettingAsync(KeyTimeoutSecs, DefaultTimeoutSecs).ConfigureAwait(false);
        var timeout    = int.TryParse(timeoutStr, out var t) ? t : 60;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeout));

        var firstChunk   = text.Length > 3000 ? text[..3000] : text;
        var promptSent   = BuildPrompt(firstChunk, description);
        var allValues    = new List<string>();

        foreach (var chunk in ChunkText(text, 2000, 150))
        {
            var vals = await CallAiAsync(baseUrl, model, chunk, description, cts.Token).ConfigureAwait(false);
            allValues.AddRange(vals);
        }

        var distinct = allValues
            .Select(s => s.Trim())
            .Where(s => s.Length > 0 && !s.Equals("NONE", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return (distinct, promptSent);
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

    public static string BuildPrompt(string text, string description) => $"""
You are a data extraction tool. The document may be in Hebrew, English, or both.

STRICT RULES:
- Output ONLY the raw values copied exactly from the document, one per line
- Do NOT write sentences, explanations, labels, or commentary
- Do NOT rephrase, translate, or reorder — copy the exact characters as they appear
- If not found: write exactly NONE

What to extract: {description}

Examples of correct output:
  Query: 'patient full name'  →  משה יבגי
  Query: 'שם מטופל'           →  משה יבגי
  Query: 'phone number'       →  052-6488580
  Query: 'date of birth'      →  20/08/1955
  Query: 'doctor name'        →  משה אזרזר

---
{text}
---

{description}:
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
    [JsonPropertyName("model")]       public string  Model       { get; set; } = "";
    [JsonPropertyName("prompt")]      public string  Prompt      { get; set; } = "";
    [JsonPropertyName("stream")]      public bool    Stream      { get; set; } = false;
    [JsonPropertyName("temperature")] public double  Temperature { get; set; } = 0.0;
    [JsonPropertyName("seed")]        public int     Seed        { get; set; } = 42;
    [JsonPropertyName("num_predict")] public int     NumPredict  { get; set; } = 60;
    [JsonPropertyName("num_ctx")]     public int     NumCtx      { get; set; } = 2048;
    [JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("keep_alive")]  public string? KeepAlive   { get; set; }
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
