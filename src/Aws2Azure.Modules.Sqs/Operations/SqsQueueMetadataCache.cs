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
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(3);
    // Azure Service Bus's management-plane GetQueue can briefly answer a
    // recently-deleted queue with a 2xx response whose body isn't a
    // well-formed Atom <entry> (the delete itself already completed, but the
    // management-plane read view hasn't settled into a clean 404 yet). This
    // mirrors the eventual-consistency window already handled for ListQueues
    // in SqsRealAzureLoadQualificationTests — poll for a short bounded window
    // before concluding the queue is gone, confirmed against real Azure by
    // SqsRealAzureConformanceTests.SendMessage_to_deleted_queue_returns_native_nonexistent_queue_error.
    private static readonly TimeSpan ParseRetryWindow = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ParseRetryPollInterval = TimeSpan.FromMilliseconds(300);
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

        var deadline = DateTimeOffset.UtcNow + ParseRetryWindow;
        while (true)
        {
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
            if (entry is not null)
            {
                var metadata = SqsQueueTagStore.DecodeMetadata(entry.Properties.UserMetadata);
                Set(sb.Namespace, queueName, metadata, preferForEmptyReads: false);
                return new LookupResult(true, SqsQueueTagStore.CloneMetadata(metadata), null);
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                return new LookupResult(false, new SqsQueueTagStore.QueueMetadata(), SqsErrorMapping.QueueDoesNotExist());
            }

            await Task.Delay(ParseRetryPollInterval, ct).ConfigureAwait(false);
        }
    }

    internal static void Set(ServiceBusClient sb, string queueName, QueueDescriptionProperties props)
        => Set(sb.Namespace, queueName, SqsQueueTagStore.DecodeMetadata(props.UserMetadata), preferForEmptyReads: false);

    internal static void Set(ServiceBusClient sb, string queueName, SqsQueueTagStore.QueueMetadata metadata)
        => Set(sb.Namespace, queueName, metadata, preferForEmptyReads: false);

    internal static void RememberSuccessfulWrite(ServiceBusClient sb, string queueName, QueueDescriptionProperties props)
        => Set(sb.Namespace, queueName, SqsQueueTagStore.DecodeMetadata(props.UserMetadata), preferForEmptyReads: true);

    internal static void RememberSuccessfulWrite(ServiceBusClient sb, string queueName, SqsQueueTagStore.QueueMetadata metadata)
        => Set(sb.Namespace, queueName, metadata, preferForEmptyReads: true);

    internal static void Invalidate(ServiceBusClient sb, string queueName)
        => Entries.TryRemove(BuildKey(sb.Namespace, queueName), out _);

    internal static void ApplyFreshSnapshot(ServiceBusClient sb, string queueName, QueueDescriptionProperties props)
    {
        var key = BuildKey(sb.Namespace, queueName);
        var now = DateTimeOffset.UtcNow;
        if (!Entries.TryGetValue(key, out var entry) || entry.ExpiresAtUtc <= now || !entry.PreferForEmptyReads)
        {
            return;
        }

        var existing = SqsQueueTagStore.DecodeMetadata(props.UserMetadata);
        if (!IsEmpty(existing) || IsEmpty(entry.Metadata))
        {
            return;
        }

        var readThrough = SqsQueueTagStore.CloneMetadata(entry.Metadata);
        readThrough.PurgeCooldownUntilUnixSeconds = null;
        if (!SqsQueueTagStore.TryEncodeMetadata(readThrough, out var userMetadata))
        {
            return;
        }

        props.UserMetadata = string.IsNullOrEmpty(userMetadata) ? null : userMetadata;
    }

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

    private static void Set(
        string serviceBusNamespace,
        string queueName,
        SqsQueueTagStore.QueueMetadata metadata,
        bool preferForEmptyReads)
    {
        TrimExpired(DateTimeOffset.UtcNow);
        var key = BuildKey(serviceBusNamespace, queueName);
        if (Entries.Count >= MaxEntries && !Entries.ContainsKey(key))
        {
            return;
        }

        Entries[key] = new CacheEntry(
            SqsQueueTagStore.CloneMetadata(metadata),
            preferForEmptyReads,
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

    private static bool IsEmpty(SqsQueueTagStore.QueueMetadata metadata) =>
        metadata.Tags.Count == 0
        && metadata.DelaySeconds is null
        && metadata.ReceiveMessageWaitTimeSeconds is null
        && metadata.PurgeCooldownUntilUnixSeconds is null;

    private sealed record CacheEntry(
        SqsQueueTagStore.QueueMetadata Metadata,
        bool PreferForEmptyReads,
        DateTimeOffset ExpiresAtUtc);

    // Test-only hook so unit tests can isolate the bounded static cache.
    internal static void ResetForTesting() => Entries.Clear();
}
