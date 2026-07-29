using System;
using System.Collections.Concurrent;
using System.Threading;

namespace Aws2Azure.Modules.DynamoDb.Internal;

/// <summary>
/// Simple in-memory cache for TableMetadata with time-based expiration.
/// Thread-safe via ConcurrentDictionary. Entries expire after TTL and are
/// lazily evicted on next access. Cache is cleared on table lifecycle ops.
/// </summary>
internal sealed class TableMetadataCache
{
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _keyGenerations = new(StringComparer.Ordinal);
    private readonly TimeSpan _ttl;

    /// <summary>
    /// Creates a cache with the specified TTL. Default is 5 minutes.
    /// </summary>
    public TableMetadataCache(TimeSpan? ttl = null)
    {
        _ttl = ttl ?? TimeSpan.FromMinutes(5);
    }

    /// <summary>
    /// Tries to get cached metadata for a table.
    /// Returns null if not cached or expired.
    /// </summary>
    public TableMetadata? TryGet(string cosmosEndpoint, string databaseName, string tableName)
    {
        var key = BuildKey(cosmosEndpoint, databaseName, tableName);
        if (_cache.TryGetValue(key, out var entry))
        {
            if (DateTime.UtcNow - entry.CachedAt < _ttl)
            {
                Interlocked.Increment(ref _hits);
                return entry.Metadata;
            }
            // Expired - remove lazily
            _cache.TryRemove(key, out _);
        }
        Interlocked.Increment(ref _misses);
        return null;
    }

    /// <summary>
    /// Caches metadata for a table. Only sets if the caller's per-table
    /// generation still matches current state (prevents stale reads from
    /// overwriting after invalidation/removal).
    /// </summary>
    public void Set(string cosmosEndpoint, string databaseName, string tableName, TableMetadata metadata, long generation)
    {
        var key = BuildKey(cosmosEndpoint, databaseName, tableName);
        var entry = new CacheEntry(metadata, DateTime.UtcNow, generation);

        lock (_mutationGate)
        {
            var currentGeneration = _keyGenerations.TryGetValue(key, out var version) ? version : 0;
            if (currentGeneration != generation)
            {
                return;
            }

            // Only set if the per-table generation still matches current state.
            _cache.AddOrUpdate(key, entry, (_, existing) =>
                existing.Generation <= generation ? entry : existing);
        }
    }

    /// <summary>
    /// Invalidates cached metadata for a specific table by bumping its
    /// generation. Called on DeleteTable, UpdateTable.
    /// </summary>
    public void Invalidate(string cosmosEndpoint, string databaseName, string tableName)
    {
        var key = BuildKey(cosmosEndpoint, databaseName, tableName);
        lock (_mutationGate)
        {
            _keyGenerations.AddOrUpdate(key, 1, static (_, current) => current + 1);
            _cache.TryRemove(key, out _);
        }
    }

    /// <summary>
    /// Removes one cached entry without bumping the global generation. Use for
    /// fresh negative reads so only the affected table is evicted.
    /// </summary>
    public void Remove(string cosmosEndpoint, string databaseName, string tableName)
    {
        var key = BuildKey(cosmosEndpoint, databaseName, tableName);
        lock (_mutationGate)
        {
            var removedCached = _cache.TryRemove(key, out _);
            if (removedCached || _keyGenerations.ContainsKey(key))
            {
                _keyGenerations.AddOrUpdate(key, 1, static (_, current) => current + 1);
            }
        }
    }

    /// <summary>
    /// Gets the current per-table generation for use in Set() calls.
    /// </summary>
    public long GetGeneration(string cosmosEndpoint, string databaseName, string tableName)
    {
        var key = BuildKey(cosmosEndpoint, databaseName, tableName);
        return _keyGenerations.TryGetValue(key, out var generation) ? generation : 0;
    }

    /// <summary>
    /// Clears all cached entries. Useful for testing.
    /// </summary>
    public void Clear()
    {
        lock (_mutationGate)
        {
            _cache.Clear();
            _keyGenerations.Clear();
        }
    }

    /// <summary>
    /// Returns cache statistics for monitoring.
    /// </summary>
    public (long hits, long misses, int count) GetStats()
        => (Interlocked.Read(ref _hits), Interlocked.Read(ref _misses), _cache.Count);

    private static string BuildKey(string endpoint, string databaseName, string tableName)
        => string.Concat(endpoint, ":", databaseName, ":", tableName);

    private long _hits;
    private long _misses;
    private readonly object _mutationGate = new();

    private readonly record struct CacheEntry(TableMetadata Metadata, DateTime CachedAt, long Generation);
}
