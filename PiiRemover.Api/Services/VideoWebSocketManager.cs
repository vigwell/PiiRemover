using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace PiiRemover.Api.Services;

/// <summary>
/// In-memory registry for video processing WebSocket connections.
/// Manages handshake tokens and active connections; pushes job status events.
/// </summary>
public class VideoWebSocketManager
{
    private readonly ConcurrentDictionary<string, (int clientId, DateTime expiry)> _pendingTokens = new();
    private readonly ConcurrentDictionary<string, (WebSocket ws, int clientId)> _connections = new();

    public string IssueToken(int clientId, int expiryMinutes)
    {
        var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))
                           .ToLowerInvariant();
        _pendingTokens[token] = (clientId, DateTime.UtcNow.AddMinutes(expiryMinutes));
        return token;
    }

    public bool TryConsumeToken(string token, out int clientId)
    {
        clientId = 0;
        if (!_pendingTokens.TryRemove(token, out var entry)) return false;
        if (DateTime.UtcNow > entry.expiry) return false;
        clientId = entry.clientId;
        return true;
    }

    public string Register(WebSocket ws, int clientId)
    {
        var id = Guid.NewGuid().ToString("N");
        _connections[id] = (ws, clientId);
        return id;
    }

    public void Remove(string connectionId) => _connections.TryRemove(connectionId, out _);

    public IEnumerable<(string id, WebSocket ws, int clientId)> GetAll() =>
        _connections.Select(kv => (kv.Key, kv.Value.ws, kv.Value.clientId));

    public async Task SendToClientAsync(int clientId, object payload)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(payload,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        foreach (var (_, ws, cid) in GetAll())
        {
            if (cid != clientId) continue;
            if (ws.State != WebSocketState.Open) continue;
            try
            {
                await ws.SendAsync(json, WebSocketMessageType.Text, endOfMessage: true,
                    CancellationToken.None);
            }
            catch { /* connection may have closed concurrently */ }
        }
    }
}
