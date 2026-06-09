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

Console.WriteLine("╔══════════════════════════════════════════════════════════╗");
Console.WriteLine("║           PiiRemover Batch Loader                        ║");
Console.WriteLine("╚══════════════════════════════════════════════════════════╝");
Console.WriteLine($"  API   : {cfg.ApiUrl}");
Console.WriteLine($"  Move  : {(cfg.MoveProcessedToDone ? "yes — processed files moved to done/" : "no")}");
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
        cfg.Ocr.FilePatterns,
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
        cfg.Redact.FilePatterns,
        cfg.MoveProcessedToDone, http);
    totalOk += ok; totalFail += fail;
    Console.WriteLine();
}

sessionSw.Stop();
Console.WriteLine($"══ Done  ok={totalOk}  fail={totalFail}  elapsed={sessionSw.Elapsed:mm\\:ss\\.fff} ══");
return totalFail > 0 ? 2 : 0;

// ── Core batch processor ──────────────────────────────────────────────────────
static async Task<(int ok, int fail)> ProcessBatchAsync(
    string?       inputFolder,  string? outputFolder,
    string        endpoint,     string  responseField,
    List<string>? filePatterns,
    bool          moveToDone,   HttpClient http)
{
    if (string.IsNullOrWhiteSpace(inputFolder) || string.IsNullOrWhiteSpace(outputFolder))
    {
        Console.WriteLine("  [SKIP] Folder paths not configured.");
        return (0, 0);
    }

    if (!Directory.Exists(inputFolder))
    {
        Console.WriteLine($"  [SKIP] Input folder not found: {inputFolder}");
        return (0, 0);
    }

    Directory.CreateDirectory(outputFolder);

    // Collect files matching configured patterns (default: all non-.txt files)
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
    Console.WriteLine($"  Input  : {inputFolder}  ({files2.Length} file(s), patterns: {patternsLabel})");
    Console.WriteLine($"  Output : {outputFolder}");
    Console.WriteLine();

    string? doneFolder = null;
    if (moveToDone)
    {
        doneFolder = Path.Combine(inputFolder, "done");
        Directory.CreateDirectory(doneFolder);
        Console.WriteLine($"  Done   : {doneFolder}");
        Console.WriteLine();
    }

    int ok = 0, fail = 0;

    for (int i = 0; i < files2.Length; i++)
    {
        var file    = files2[i];
        var name    = Path.GetFileName(file);
        var outFile = Path.Combine(outputFolder,
                          Path.GetFileNameWithoutExtension(name) + ".txt");
        var sw      = Stopwatch.StartNew();

        Console.Write($"  [{i + 1:D3}/{files2.Length:D3}] {name,-40} ");

        try
        {
            await using var stream  = File.OpenRead(file);
            var multipart           = new MultipartFormDataContent();
            var part                = new StreamContent(stream);
            part.Headers.ContentType = new MediaTypeHeaderValue(GuessMime(file));
            multipart.Add(part, "file", name);

            var resp = await http.PostAsync(endpoint, multipart);
            sw.Stop();

            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync();
                WriteColored($"FAIL  {sw.ElapsedMilliseconds,5}ms  HTTP {(int)resp.StatusCode}  {Truncate(body, 100)}", ConsoleColor.Red);
                fail++;
                continue;
            }

            var json = await resp.Content.ReadFromJsonAsync<JsonElement>();

            string text;
            string extra = string.Empty;

            if (responseField == "text")
            {
                text = json.GetProperty("text").GetString() ?? string.Empty;
                if (json.TryGetProperty("charCount", out var cc))
                    extra = $"  chars={cc.GetInt32()}";
            }
            else
            {
                text = json.GetProperty("redactedText").GetString() ?? string.Empty;
                if (json.TryGetProperty("matchCount", out var mc))
                    extra = $"  matches={mc.GetInt32()}";
            }

            await File.WriteAllTextAsync(outFile, text, System.Text.Encoding.UTF8);

            WriteColored($"OK    {sw.ElapsedMilliseconds,5}ms  -> {Path.GetFileName(outFile)}{extra}", ConsoleColor.Green);
            ok++;

            if (moveToDone && doneFolder is not null)
            {
                var dest = Path.Combine(doneFolder, name);
                if (File.Exists(dest)) File.Delete(dest);
                File.Move(file, dest);
            }
        }
        catch (Exception ex)
        {
            sw.Stop();
            WriteColored($"ERROR {sw.ElapsedMilliseconds,5}ms  {ex.Message}", ConsoleColor.Red);
            fail++;
        }
    }

    Console.WriteLine();
    Console.WriteLine($"  Result: {ok} ok, {fail} failed");
    return (ok, fail);
}

static void WriteColored(string text, ConsoleColor color)
{
    var prev = Console.ForegroundColor;
    Console.ForegroundColor = color;
    Console.WriteLine(text);
    Console.ForegroundColor = prev;
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

// ── Config model ──────────────────────────────────────────────────────────────
record BatchConfig(
    string?      ApiUrl,
    string?      ApiKey,
    FolderConfig? Ocr,
    FolderConfig? Redact,
    bool          MoveProcessedToDone);

record FolderConfig(
    string?       InputFolder,
    string?       OutputFolder,
    bool          Enabled,
    List<string>? FilePatterns);
