using System.Net.WebSockets;
using PiiRemover.Data.Repositories;

namespace PiiRemover.Api.Services;

/// <summary>
/// Background service that removes stale WebSocket connections every 60 seconds.
/// Prunes VideoConnections rows that have been inactive longer than the configured timeout.
/// </summary>
public class VideoConnectionCleanupService : BackgroundService
{
    private readonly VideoWebSocketManager _wsManager;
    private readonly IVideoConnectionRepository _connRepo;
    private readonly VideoSettings _settings;
    private readonly ILogger<VideoConnectionCleanupService> _logger;

    public VideoConnectionCleanupService(
        VideoWebSocketManager wsManager,
        IVideoConnectionRepository connRepo,
        VideoSettings settings,
        ILogger<VideoConnectionCleanupService> logger)
    {
        _wsManager = wsManager;
        _connRepo  = connRepo;
        _settings  = settings;
        _logger    = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(60), ct);
            try { await CleanupAsync(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Video WS cleanup error"); }
        }
    }

    private async Task CleanupAsync()
    {
        var idleMinutes = await _settings.GetWsIdleTimeoutAsync();

        // Close and remove in-memory connections that are no longer open
        foreach (var (id, ws, _) in _wsManager.GetAll().ToList())
        {
            if (ws.State != WebSocketState.Open)
            {
                _wsManager.Remove(id);
                await _connRepo.MarkInactiveAsync(id);
            }
        }

        // Prune old DB rows
        await _connRepo.PruneInactiveAsync(idleMinutes);
    }
}
