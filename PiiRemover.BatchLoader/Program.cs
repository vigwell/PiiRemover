using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

// ── Load config ───────────────────────────────────────────────────────────────
var configPath = Path.Combine(AppContext.BaseDirectory, "batchloader.json");
if (!File.Exists(configPath))
{
    Console.Error.WriteLine($"[ERROR] Config not found: {configPath}");
    return 1;
}

BatchConfig cfg;
try
{
    cfg = JsonSerializer.Deserialize<BatchConfig>(
              await File.ReadAllTextAsync(configPath),
              new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
          ?? throw new InvalidOperationException("Null config");
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[ERROR] Cannot parse config: {ex.Message}");
    return 1;
}

if (string.IsNullOrWhiteSpace(cfg.ApiUrl))
{
    Console.Error.WriteLine("[ERROR] ApiUrl is required in batchloader.json");
    return 1;
}

// ── Session start ─────────────────────────────────────────────────────────────
int totalOk = 0, totalFail = 0;
var sessionSw = Stopwatch.StartNew();

var ocrParallel    = cfg.Ocr?.MaxParallel    > 0 ? cfg.Ocr.MaxParallel    : 3;
var redactParallel = cfg.Redact?.MaxParallel > 0 ? cfg.Redact.MaxParallel : 3;

Console.WriteLine("╔══════════════════════════════════════════════════════════╗");
Console.WriteLine("║           PiiRemover Batch Loader                        ║");
Console.WriteLine("╚══════════════════════════════════════════════════════════╝");
Console.WriteLine($"  Config : {configPath}");
Console.WriteLine($"  API    : {cfg.ApiUrl}");
Console.WriteLine($"  Move   : {(cfg.MoveProcessedToDone ? "yes — processed files moved to done/" : "no")}");
if (cfg.Ocr?.Enabled == true)
{
    Console.WriteLine($"  OCR    : {ocrParallel} parallel  |  input={cfg.Ocr.InputFolder}  |  exists={Directory.Exists(cfg.Ocr.InputFolder ?? "")}");
}
if (cfg.Redact?.Enabled == true)
{
    Console.WriteLine($"  Redact : {redactParallel} parallel  |  input={cfg.Redact.InputFolder}  |  exists={Directory.Exists(cfg.Redact.InputFolder ?? "")}");
}
Console.WriteLine();

// Trust the ASP.NET dev certificate when hitting localhost over HTTPS
var handler = new HttpClientHandler();
var uri = new Uri(cfg.ApiUrl);
if (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
    uri.Host == "127.0.0.1")
{
    handler.ServerCertificateCustomValidationCallback =
        HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
}

using var http = new HttpClient(handler);
http.BaseAddress = new Uri(cfg.ApiUrl.TrimEnd('/') + "/");
http.DefaultRequestHeaders.Add("X-Api-Key", cfg.ApiKey ?? string.Empty);
http.Timeout = TimeSpan.FromMinutes(5);

// ── OCR batch ─────────────────────────────────────────────────────────────────
if (cfg.Ocr?.Enabled == true)
{
    Console.WriteLine("── OCR ──────────────────────────────────────────────────────");
    var (ok, fail) = await ProcessBatchAsync(
        cfg.Ocr.InputFolder, cfg.Ocr.OutputFolder,
        "api/v1/ocr/extract", "text",
        cfg.Ocr.FilePatterns, ocrParallel,
        cfg.MoveProcessedToDone, http);
    totalOk += ok; totalFail += fail;
    Console.WriteLine();
}

// ── Redact batch ──────────────────────────────────────────────────────────────
if (cfg.Redact?.Enabled == true)
{
    Console.WriteLine("── Redact ───────────────────────────────────────────────────");
    var (ok, fail) = await ProcessBatchAsync(
        cfg.Redact.InputFolder, cfg.Redact.OutputFolder,
        "api/v1/redact/redact", "redactedText",
        cfg.Redact.FilePatterns, redactParallel,
        cfg.MoveProcessedToDone, http);
    totalOk += ok; totalFail += fail;
    Console.WriteLine();
}

sessionSw.Stop();
Console.WriteLine($"══ Done  ok={totalOk}  fail={totalFail}  elapsed={sessionSw.Elapsed:mm\\:ss\\.fff} ══");
return totalFail > 0 ? 2 : 0;

// ── Core batch processor (parallel) ──────────────────────────────────────────
static async Task<(int ok, int fail)> ProcessBatchAsync(
    string?       inputFolder,  string? outputFolder,
    string        endpoint,     string  responseField,
    List<string>? filePatterns, int     maxParallel,
    bool          moveToDone,   HttpClient http)
{
    if (string.IsNullOrWhiteSpace(inputFolder) || string.IsNullOrWhiteSpace(outputFolder))
    {
        Console.WriteLine("  [SKIP] Folder paths not configured.");
        return (0, 0);
    }

    if (!Directory.Exists(inputFolder))
    {
        var prev = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"  [SKIP] Input folder does not exist: {inputFolder}");
        Console.WriteLine($"         Create this folder and place files in it, then re-run.");
        Console.ForegroundColor = prev;
        return (0, 0);
    }

    Directory.CreateDirectory(outputFolder);
    Console.WriteLine($"  Output folder ready : {outputFolder}");

    // Collect files matching configured patterns
    var patterns = filePatterns?.Count > 0 ? filePatterns : null;
    IEnumerable<string> files;
    if (patterns is not null)
    {
        files = patterns
            .SelectMany(p => Directory.GetFiles(inputFolder, p))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }
    else
    {
        files = Directory.GetFiles(inputFolder)
                         .Where(f => !string.Equals(
                             Path.GetExtension(f), ".txt",
                             StringComparison.OrdinalIgnoreCase));
    }
    var files2 = files.OrderBy(f => f).ToArray();

    if (files2.Length == 0)
    {
        Console.WriteLine($"  [INFO] No files to process in: {inputFolder}");
        return (0, 0);
    }

    var patternsLabel = patterns is not null ? string.Join(", ", patterns) : "all files";
    Console.WriteLine($"  Input    : {inputFolder}  ({files2.Length} file(s), patterns: {patternsLabel})");
    Console.WriteLine($"  Output   : {outputFolder}");
    Console.WriteLine($"  Parallel : {maxParallel} concurrent calls");
    Console.WriteLine();

    string? doneFolder = null;
    if (moveToDone)
    {
        doneFolder = Path.Combine(inputFolder, "done");
        Directory.CreateDirectory(doneFolder);
        Console.WriteLine($"  Done   : {doneFolder}");
        Console.WriteLine();
    }

    // Thread-safe counters and console lock
    int ok = 0, fail = 0;
    int completed = 0;
    var consoleLock = new SemaphoreSlim(1, 1);
    var semaphore   = new SemaphoreSlim(maxParallel, maxParallel);

    // Capture loop variables — avoids closure capture issues in parallel lambdas
    var capturedOutput   = outputFolder!;
    var capturedDone     = doneFolder;
    var capturedEndpoint = endpoint;
    var capturedField    = responseField;
    var capturedMove     = moveToDone;
    var total            = files2.Length;

    // Launch all files in parallel, capped at maxParallel concurrent
    var tasks = files2.Select(async (file, i) =>
    {
        await semaphore.WaitAsync();
        try
        {
            var name    = Path.GetFileName(file);
            // Avoid collision: prefix with original extension so ocr/redact outputs never clash
            var outFile = Path.Combine(capturedOutput,
                              Path.GetFileNameWithoutExtension(name) + ".txt");
            var sw      = Stopwatch.StartNew();

            try
            {
                var mime = GuessMime(file);

                await using var stream = File.OpenRead(file);
                var multipart          = new MultipartFormDataContent();
                var part               = new StreamContent(stream);
                part.Headers.ContentType = new MediaTypeHeaderValue(mime);
                multipart.Add(part, "file", name);

                var resp = await http.PostAsync(capturedEndpoint, multipart);
                sw.Stop();

                var idx = Interlocked.Increment(ref completed);

                if (!resp.IsSuccessStatusCode)
                {
                    var body = await resp.Content.ReadAsStringAsync();
                    await PrintLineAsync(consoleLock,
                        $"  [{idx:D3}/{total:D3}] {name,-40} FAIL  {sw.ElapsedMilliseconds,5}ms  HTTP {(int)resp.StatusCode}  {Truncate(body, 80)}",
                        ConsoleColor.Red);
                    Interlocked.Increment(ref fail);
                    return;
                }

                var responseBody = await resp.Content.ReadAsStringAsync();
                JsonElement json;
                try   { json = JsonSerializer.Deserialize<JsonElement>(responseBody); }
                catch {
                    var idx2 = completed;
                    await PrintLineAsync(consoleLock,
                        $"  [{idx:D3}/{total:D3}] {name,-40} FAIL  {sw.ElapsedMilliseconds,5}ms  Bad JSON: {Truncate(responseBody, 80)}",
                        ConsoleColor.Red);
                    Interlocked.Increment(ref fail);
                    return;
                }

                string text;
                string extra = string.Empty;

                if (capturedField == "text")
                {
                    if (!json.TryGetProperty("text", out var textProp))
                    {
                        await PrintLineAsync(consoleLock,
                            $"  [{idx:D3}/{total:D3}] {name,-40} FAIL  {sw.ElapsedMilliseconds,5}ms  Missing 'text' in response: {Truncate(responseBody, 120)}",
                            ConsoleColor.Red);
                        Interlocked.Increment(ref fail);
                        return;
                    }
                    text = textProp.GetString() ?? string.Empty;
                    if (json.TryGetProperty("charCount", out var cc))
                        extra = $"  chars={cc.GetInt32()}";
                }
                else
                {
                    if (!json.TryGetProperty("redactedText", out var redactProp))
                    {
                        await PrintLineAsync(consoleLock,
                            $"  [{idx:D3}/{total:D3}] {name,-40} FAIL  {sw.ElapsedMilliseconds,5}ms  Missing 'redactedText' in response: {Truncate(responseBody, 120)}",
                            ConsoleColor.Red);
                        Interlocked.Increment(ref fail);
                        return;
                    }
                    text = redactProp.GetString() ?? string.Empty;
                    if (json.TryGetProperty("matchCount", out var mc))
                        extra = $"  matches={mc.GetInt32()}";
                }

                await File.WriteAllTextAsync(outFile, text, System.Text.Encoding.UTF8);

                await PrintLineAsync(consoleLock,
                    $"  [{idx:D3}/{total:D3}] {name,-40} OK    {sw.ElapsedMilliseconds,5}ms  -> {Path.GetFileName(outFile)}{extra}",
                    ConsoleColor.Green);
                Interlocked.Increment(ref ok);

                if (capturedMove && capturedDone is not null)
                {
                    var dest = Path.Combine(capturedDone, name);
                    if (File.Exists(dest)) File.Delete(dest);
                    File.Move(file, dest);
                }
            }
            catch (Exception ex)
            {
                sw.Stop();
                Interlocked.Increment(ref completed);
                await PrintLineAsync(consoleLock,
                    $"  [---/{total:D3}] {Path.GetFileName(file),-40} ERROR {sw.ElapsedMilliseconds,5}ms  {ex.GetType().Name}: {ex.Message}",
                    ConsoleColor.Red);
                Interlocked.Increment(ref fail);
            }
        }
        finally
        {
            semaphore.Release();
        }
    });

    await Task.WhenAll(tasks);

    Console.WriteLine();
    Console.WriteLine($"  Result: {ok} ok, {fail} failed");
    return (ok, fail);
}

static async Task PrintLineAsync(SemaphoreSlim lock_, string text, ConsoleColor color)
{
    await lock_.WaitAsync();
    try
    {
        var prev = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.WriteLine(text);
        Console.ForegroundColor = prev;
    }
    finally { lock_.Release(); }
}

static string GuessMime(string path) => Path.GetExtension(path).ToLowerInvariant() switch
{
    ".pdf"            => "application/pdf",
    ".png"            => "image/png",
    ".jpg" or ".jpeg" => "image/jpeg",
    ".tif" or ".tiff" => "image/tiff",
    ".bmp"            => "image/bmp",
    ".txt"            => "text/plain",
    _                 => "application/octet-stream",
};

static string Truncate(string s, int max) =>
    s.Length <= max ? s : s[..max] + "…";

// ── Config models ─────────────────────────────────────────────────────────────
record BatchConfig(
    string?       ApiUrl,
    string?       ApiKey,
    FolderConfig? Ocr,
    FolderConfig? Redact,
    bool          MoveProcessedToDone);

record FolderConfig(
    string?       InputFolder,
    string?       OutputFolder,
    bool          Enabled,
    int           MaxParallel,
    List<string>? FilePatterns);
