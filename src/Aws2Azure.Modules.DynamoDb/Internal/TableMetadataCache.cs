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
    /// Caches metadata for a table. Only sets if generation matches current
    /// (prevents stale reads from overwriting after invalidation).
    /// </summary>
    public void Set(string cosmosEndpoint, string databaseName, string tableName, TableMetadata metadata, long generation)
    {
        var key = BuildKey(cosmosEndpoint, databaseName, tableName);
        var entry = new CacheEntry(metadata, DateTime.UtcNow, generation);
        
        // Only set if generation matches current (CAS to prevent race with invalidation)
        _cache.AddOrUpdate(key, entry, (_, existing) =>
            existing.Generation <= generation ? entry : existing);
    }

    /// <summary>
    /// Invalidates cached metadata for a specific table by bumping generation.
    /// Called on DeleteTable, UpdateTable.
    /// </summary>
    public void Invalidate(string cosmosEndpoint, string databaseName, string tableName)
    {
        var key = BuildKey(cosmosEndpoint, databaseName, tableName);
        // Bump global generation to invalidate any in-flight reads
        Interlocked.Increment(ref _generation);
        _cache.TryRemove(key, out _);
    }

    /// <summary>
    /// Gets current generation for use in Set() call.
    /// </summary>
    public long GetGeneration() => Interlocked.Read(ref _generation);

    /// <summary>
    /// Clears all cached entries. Useful for testing.
    /// </summary>
    public void Clear() => _cache.Clear();

    /// <summary>
    /// Returns cache statistics for monitoring.
    /// </summary>
    public (long hits, long misses, int count) GetStats()
        => (Interlocked.Read(ref _hits), Interlocked.Read(ref _misses), _cache.Count);

    private static string BuildKey(string endpoint, string databaseName, string tableName)
        => string.Concat(endpoint, ":", databaseName, ":", tableName);

    private long _hits;
    private long _misses;
    private long _generation;

    private readonly record struct CacheEntry(TableMetadata Metadata, DateTime CachedAt, long Generation);
}
