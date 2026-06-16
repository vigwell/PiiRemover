using PiiRemover.Core.Engines;
using PiiRemover.Core.Logging;
using PiiRemover.Data.Repositories;

namespace PiiRemover.Api.Services;

/// <summary>
/// Background service that periodically sends a minimal request to the Ollama server
/// to keep the configured model loaded in memory.
///
/// Ollama unloads a model after its keep_alive TTL (default 5 min) with no activity.
/// By pinging every N minutes we prevent that cold-start penalty on the next real call.
///
/// The ping sends a single-token generation with keep_alive = interval + 2 min so
/// the model stays hot even between ticks. All parameters are re-read from DB on
/// every tick so changes take effect without a restart.
///
/// Only runs when:
///   • ai:enabled        = true
///   • ai:warmupEnabled  = true
///   • Ollama is reachable and the configured model is available
/// </summary>
public sealed class OllamaWarmupService : BackgroundService
{
    // Check whether a warmup is needed every 30 s; actual warmup fires on interval.
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);

    // Inject IAiService (singleton) and cast to OllamaService for WarmupAsync.
    // Do NOT inject OllamaService directly — AddHttpClient registers it as transient,
    // so a direct constructor injection resolves a different instance than the singleton IAiService.
    private readonly OllamaService     _ollama;
    private readonly ISettingsRepository _settings;
    private readonly IPiiLogger        _logger;

    private DateTime _lastWarmedAt = DateTime.MinValue;

    public OllamaWarmupService(IAiService aiService, ISettingsRepository settings, IPiiLogger logger)
    {
        _ollama   = (OllamaService)aiService;
        _settings = settings;
        _logger   = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Brief startup delay — let the rest of the app initialise first.
        await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken).ConfigureAwait(false);
        _logger.LogInfo("OllamaWarmupService started.");

        using var timer = new PeriodicTimer(TickInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try   { await TryWarmupAsync(stoppingToken).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogError("OllamaWarmup tick error", null, ex); }
        }
    }

    private async Task TryWarmupAsync(CancellationToken ct)
    {
        // Read settings fresh every tick — changes take effect without restart.
        // Treat missing key as enabled — warmup is on by default.
        var warmupVal = await _settings.GetAsync(OllamaService.KeyWarmupEnabled).ConfigureAwait(false);
        var warmupEnabled = warmupVal == null || string.Equals(warmupVal, "true", StringComparison.OrdinalIgnoreCase);

        if (!warmupEnabled) return;
        if (!await _ollama.IsEnabledAsync().ConfigureAwait(false)) return;

        var intervalStr = await _settings.GetAsync(OllamaService.KeyWarmupIntervalMinutes).ConfigureAwait(false);
        var intervalMin = int.TryParse(intervalStr, out var v) && v >= 1 ? v : 4;

        if ((DateTime.UtcNow - _lastWarmedAt) < TimeSpan.FromMinutes(intervalMin)) return;

        _logger.LogInfo($"OllamaWarmup: sending keep-warm ping (interval={intervalMin} min)…");
        var ok = await _ollama.WarmupAsync(intervalMin, ct).ConfigureAwait(false);

        if (ok)
        {
            _lastWarmedAt = DateTime.UtcNow;
            await _settings.SetAsync(OllamaService.KeyWarmupLastAt,
                _lastWarmedAt.ToString("O"), "Timestamp of last successful LLM warmup").ConfigureAwait(false);
            _logger.LogInfo("OllamaWarmup: model kept warm successfully.");
        }
        else
        {
            _logger.LogError("OllamaWarmup: ping failed — Ollama may be down or model not loaded.", null, null);
        }
    }
}
