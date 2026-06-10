using System.Collections.Concurrent;
using PiiRemover.Core.Models;
using PiiRemover.Data.Repositories;

namespace PiiRemover.Api.Services;

/// <summary>
/// In-memory cache for PiiField + PiiPattern data.
/// Avoids a DB round-trip on every redaction request.
///
/// Cache entries expire after <see cref="Ttl"/> (default 5 min).
/// Call <see cref="Invalidate"/> immediately after any admin save/delete so the
/// next request picks up the new patterns without waiting for TTL expiry.
///
/// Thread-safe: singleton lifetime, concurrent dictionary + volatile read.
/// </summary>
public sealed class FieldsCache
{
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    private readonly IFieldRepository _repo;

    // Key: clientId as string ("" = global/null)
    private readonly ConcurrentDictionary<string, CacheEntry> _map = new();

    public FieldsCache(IFieldRepository repo) => _repo = repo;

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Returns cached fields, refreshing from DB if the entry is stale.</summary>
    public async Task<IEnumerable<PiiField>> GetFieldsAsync(int? clientId)
    {
        var key = Key(clientId);
        if (_map.TryGetValue(key, out var entry) && !entry.IsExpired)
            return entry.Fields;

        var fresh = (await _repo.GetFieldsWithPatternsAsync(clientId)).ToList();
        _map[key]  = new CacheEntry(fresh);
        return fresh;
    }

    /// <summary>Invalidate all cached entries (call after any admin catalog change).</summary>
    public void Invalidate() => _map.Clear();

    /// <summary>Invalidate the entry for a specific client only.</summary>
    public void Invalidate(int? clientId) => _map.TryRemove(Key(clientId), out _);

    // ── Internals ─────────────────────────────────────────────────────────────

    private static string Key(int? clientId) => clientId?.ToString() ?? string.Empty;

    private sealed class CacheEntry
    {
        private readonly DateTime _expiry;
        public IReadOnlyList<PiiField> Fields { get; }
        public bool IsExpired => DateTime.UtcNow >= _expiry;

        public CacheEntry(List<PiiField> fields)
        {
            Fields  = fields;
            _expiry = DateTime.UtcNow + Ttl;
        }
    }
}
