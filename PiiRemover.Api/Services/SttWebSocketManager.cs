using System.Collections.Concurrent;
using System.Net.WebSockets;

namespace PiiRemover.Api.Services;

/// <summary>
/// In-memory registry for STT WebSocket sessions.
/// Same token-handshake pattern as VideoWebSocketManager.
/// </summary>
public class SttWebSocketManager
{
    private readonly ConcurrentDictionary<string, (int clientId, DateTime expiry)> _pendingTokens = new();
    private readonly ConcurrentDictionary<string, (WebSocket ws, int clientId)> _sessions = new();

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
        _sessions[id] = (ws, clientId);
        return id;
    }

    public void Remove(string sessionId) => _sessions.TryRemove(sessionId, out _);
}
