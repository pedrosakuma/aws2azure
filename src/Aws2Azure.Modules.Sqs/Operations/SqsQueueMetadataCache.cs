using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Aws2Azure.Modules.Sqs.Errors;
using Aws2Azure.Modules.Sqs.Internal;
using Aws2Azure.Modules.Sqs.Xml;

namespace Aws2Azure.Modules.Sqs.Operations;

internal static class SqsQueueMetadataCache
{
    private const int MaxEntries = 1024;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(1);
    private static readonly ConcurrentDictionary<string, CacheEntry> Entries =
        new(StringComparer.Ordinal);

    internal readonly record struct LookupResult(
        bool Success,
        SqsQueueTagStore.QueueMetadata Metadata,
        SqsErrorMapping.Mapping? Error);

    internal static async Task<LookupResult> GetAsync(
        ServiceBusClient sb,
        string queueName,
        CancellationToken ct)
    {
        if (TryGet(sb.Namespace, queueName, out var cached))
        {
            return new LookupResult(true, cached, null);
        }

        using var response = await sb.GetQueueAsync(queueName, ct).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return new LookupResult(false, new SqsQueueTagStore.QueueMetadata(), SqsErrorMapping.QueueDoesNotExist());
        }
        if (!response.IsSuccessStatusCode)
        {
            return new LookupResult(false, new SqsQueueTagStore.QueueMetadata(), SqsErrorMapping.FromServiceBus(response));
        }

        var xml = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(xml))
        {
            return new LookupResult(true, new SqsQueueTagStore.QueueMetadata(), null);
        }

        var entry = AtomQueueXmlReader.ParseQueueEntry(xml);
        if (entry is null)
        {
            return new LookupResult(false, new SqsQueueTagStore.QueueMetadata(),
                SqsErrorMapping.InternalError("aws2azure: failed to parse Service Bus queue description."));
        }

        var metadata = SqsQueueTagStore.DecodeMetadata(entry.Properties.UserMetadata);
        Set(sb.Namespace, queueName, metadata);
        return new LookupResult(true, SqsQueueTagStore.CloneMetadata(metadata), null);
    }

    internal static void Set(ServiceBusClient sb, string queueName, QueueDescriptionProperties props)
        => Set(sb.Namespace, queueName, SqsQueueTagStore.DecodeMetadata(props.UserMetadata));

    internal static void Set(ServiceBusClient sb, string queueName, SqsQueueTagStore.QueueMetadata metadata)
        => Set(sb.Namespace, queueName, metadata);

    internal static void Invalidate(ServiceBusClient sb, string queueName)
        => Entries.TryRemove(BuildKey(sb.Namespace, queueName), out _);

    private static bool TryGet(string serviceBusNamespace, string queueName, out SqsQueueTagStore.QueueMetadata metadata)
    {
        var key = BuildKey(serviceBusNamespace, queueName);
        var now = DateTimeOffset.UtcNow;
        if (Entries.TryGetValue(key, out var entry))
        {
            if (entry.ExpiresAtUtc > now)
            {
                metadata = SqsQueueTagStore.CloneMetadata(entry.Metadata);
                return true;
            }

            Entries.TryRemove(key, out _);
        }

        metadata = new SqsQueueTagStore.QueueMetadata();
        return false;
    }

    private static void Set(string serviceBusNamespace, string queueName, SqsQueueTagStore.QueueMetadata metadata)
    {
        TrimExpired(DateTimeOffset.UtcNow);
        var key = BuildKey(serviceBusNamespace, queueName);
        if (Entries.Count >= MaxEntries && !Entries.ContainsKey(key))
        {
            return;
        }

        Entries[key] = new CacheEntry(
            SqsQueueTagStore.CloneMetadata(metadata),
            DateTimeOffset.UtcNow + CacheTtl);
    }

    private static void TrimExpired(DateTimeOffset now)
    {
        foreach (var pair in Entries)
        {
            if (pair.Value.ExpiresAtUtc <= now)
            {
                Entries.TryRemove(pair.Key, out _);
            }
        }
    }

    private static string BuildKey(string serviceBusNamespace, string queueName)
        => serviceBusNamespace + "|" + queueName;

    private sealed record CacheEntry(
        SqsQueueTagStore.QueueMetadata Metadata,
        DateTimeOffset ExpiresAtUtc);
}
