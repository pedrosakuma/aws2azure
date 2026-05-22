using System.Collections.Concurrent;
using Aws2Azure.Core.Configuration;

namespace Aws2Azure.Modules.Kinesis.EventHubsRest;

public interface IEventHubMetadataCache
{
    ValueTask<EventHubDescription> GetEventHubAsync(
        EventHubsCredentials credentials,
        string namespaceFqdn,
        string eventHubName,
        CancellationToken cancellationToken);
}

internal sealed class EventHubMetadataCache : IEventHubMetadataCache
{
    private readonly IEventHubsManagementClient _client;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _ttl;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task<EventHubDescription>> _inflight = new(StringComparer.Ordinal);

    public EventHubMetadataCache(
        IEventHubsManagementClient client,
        TimeProvider? clock = null,
        TimeSpan? ttl = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _clock = clock ?? TimeProvider.System;
        _ttl = ttl ?? TimeSpan.FromMinutes(5);
    }

    public async ValueTask<EventHubDescription> GetEventHubAsync(
        EventHubsCredentials credentials,
        string namespaceFqdn,
        string eventHubName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceFqdn);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventHubName);

        if (_ttl <= TimeSpan.Zero)
        {
            return await _client.GetEventHubAsync(credentials, namespaceFqdn, eventHubName, cancellationToken)
                .ConfigureAwait(false);
        }

        var key = BuildCacheKey(credentials, namespaceFqdn, eventHubName);
        if (TryGetCached(key, out var cached))
        {
            return cached;
        }

        var fetchTask = _inflight.GetOrAdd(
            key,
            _ => FetchAndCacheAsync(key, credentials, namespaceFqdn, eventHubName, cancellationToken));

        try
        {
            return await fetchTask.ConfigureAwait(false);
        }
        finally
        {
            if (fetchTask.IsCompleted)
            {
                _inflight.TryRemove(KeyValuePair.Create(key, fetchTask));
            }
        }
    }

    private bool TryGetCached(string key, out EventHubDescription description)
    {
        if (_cache.TryGetValue(key, out var entry) && _clock.GetUtcNow() < entry.ExpiresAt)
        {
            description = entry.Description;
            return true;
        }

        description = default!;
        return false;
    }

    private async Task<EventHubDescription> FetchAndCacheAsync(
        string key,
        EventHubsCredentials credentials,
        string namespaceFqdn,
        string eventHubName,
        CancellationToken cancellationToken)
    {
        var description = await _client.GetEventHubAsync(credentials, namespaceFqdn, eventHubName, cancellationToken)
            .ConfigureAwait(false);
        _cache[key] = new CacheEntry(description, _clock.GetUtcNow().Add(_ttl));
        return description;
    }

    private static string BuildCacheKey(EventHubsCredentials credentials, string namespaceFqdn, string eventHubName)
    {
        string credentialMarker;
        if (!string.IsNullOrWhiteSpace(credentials.SasKeyName))
        {
            credentialMarker = "sas|" + credentials.SasKeyName.Trim();
        }
        else
        {
            credentialMarker = "aad|" + credentials.TenantId + "|" + credentials.ClientId;
        }

        return namespaceFqdn.Trim().ToLowerInvariant()
            + "|"
            + eventHubName.Trim().ToLowerInvariant()
            + "|"
            + credentialMarker;
    }

    private readonly record struct CacheEntry(EventHubDescription Description, DateTimeOffset ExpiresAt);
}
