using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Aws2Azure.Modules.Sqs.Errors;
using Aws2Azure.Modules.Sqs.Internal;
using Aws2Azure.Modules.Sqs.Xml;

namespace Aws2Azure.Modules.Sqs.Operations;

internal sealed class SqsQueueMetadataCache
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
    private static readonly SqsQueueMetadataCache Shared = new(MaxEntries, CacheTtl);

    private readonly int _maxEntries;
    private readonly TimeSpan _cacheTtl;
    private readonly ConcurrentDictionary<string, CacheEntry> _entries;

    internal SqsQueueMetadataCache(int maxEntries, TimeSpan cacheTtl)
    {
        _maxEntries = maxEntries;
        _cacheTtl = cacheTtl;
        _entries = new ConcurrentDictionary<string, CacheEntry>(StringComparer.Ordinal);
    }

    internal readonly record struct LookupResult(
        bool Success,
        SqsQueueTagStore.QueueMetadata Metadata,
        SqsErrorMapping.Mapping? Error);

    internal static Task<LookupResult> GetAsync(
        ServiceBusClient sb,
        string queueName,
        CancellationToken ct) =>
        Shared.GetAsyncCore(sb, queueName, ct);

    internal static void Set(ServiceBusClient sb, string queueName, QueueDescriptionProperties props) =>
        Shared.SetCore(
            sb.Namespace,
            queueName,
            SqsQueueTagStore.DecodeMetadata(props.UserMetadata),
            preferForEmptyReads: false,
            previousVersionEtag: null,
            previousUpdatedAt: null,
            writeVersionEtag: null,
            writeCompletedAtUtc: null);

    internal static void Set(ServiceBusClient sb, string queueName, SqsQueueTagStore.QueueMetadata metadata) =>
        Shared.SetCore(
            sb.Namespace,
            queueName,
            metadata,
            preferForEmptyReads: false,
            previousVersionEtag: null,
            previousUpdatedAt: null,
            writeVersionEtag: null,
            writeCompletedAtUtc: null);

    internal static void RememberSuccessfulWrite(
        ServiceBusClient sb,
        string queueName,
        QueueDescriptionProperties props,
        string? previousVersionEtag = null,
        DateTimeOffset? previousUpdatedAt = null,
        string? writeVersionEtag = null,
        DateTimeOffset? writeCompletedAtUtc = null) =>
        Shared.RememberSuccessfulWriteCore(
            sb.Namespace,
            queueName,
            SqsQueueTagStore.DecodeMetadata(props.UserMetadata),
            previousVersionEtag,
            previousUpdatedAt,
            writeVersionEtag,
            writeCompletedAtUtc);

    internal static void RememberSuccessfulWrite(
        ServiceBusClient sb,
        string queueName,
        SqsQueueTagStore.QueueMetadata metadata,
        string? previousVersionEtag = null,
        DateTimeOffset? previousUpdatedAt = null,
        string? writeVersionEtag = null,
        DateTimeOffset? writeCompletedAtUtc = null) =>
        Shared.RememberSuccessfulWriteCore(
            sb.Namespace,
            queueName,
            metadata,
            previousVersionEtag,
            previousUpdatedAt,
            writeVersionEtag,
            writeCompletedAtUtc);

    internal static void Invalidate(ServiceBusClient sb, string queueName) =>
        Shared._entries.TryRemove(BuildKey(sb.Namespace, queueName), out _);

    internal static void ApplyFreshSnapshot(
        ServiceBusClient sb,
        string queueName,
        string? freshReadEtag,
        QueueDescriptionProperties props) =>
        Shared.ApplyFreshSnapshotCore(sb.Namespace, queueName, freshReadEtag, props);

    internal static string? ExtractETag(HttpResponseMessage response)
    {
        var eTag = response.Headers.ETag?.Tag;
        if (string.IsNullOrWhiteSpace(eTag) &&
            response.Headers.TryGetValues("ETag", out var values))
        {
            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    eTag = value;
                    break;
                }
            }
        }

        return eTag;
    }

    internal static void ResetForTesting() => Shared._entries.Clear();

    internal void RememberSuccessfulWriteForTesting(
        string serviceBusNamespace,
        string queueName,
        SqsQueueTagStore.QueueMetadata metadata,
        string? previousVersionEtag,
        DateTimeOffset? previousUpdatedAt,
        string? writeVersionEtag,
        DateTimeOffset? writeCompletedAtUtc = null) =>
        RememberSuccessfulWriteCore(
            serviceBusNamespace,
            queueName,
            metadata,
            previousVersionEtag,
            previousUpdatedAt,
            writeVersionEtag,
            writeCompletedAtUtc);

    internal void ApplyFreshSnapshotForTesting(
        string serviceBusNamespace,
        string queueName,
        string? freshReadEtag,
        QueueDescriptionProperties props) =>
        ApplyFreshSnapshotCore(serviceBusNamespace, queueName, freshReadEtag, props);

    private async Task<LookupResult> GetAsyncCore(
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
                SetCore(
                    sb.Namespace,
                    queueName,
                    metadata,
                    preferForEmptyReads: false,
                    previousVersionEtag: null,
                    previousUpdatedAt: null,
                    writeVersionEtag: null,
                    writeCompletedAtUtc: null);
                return new LookupResult(true, SqsQueueTagStore.CloneMetadata(metadata), null);
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                return new LookupResult(false, new SqsQueueTagStore.QueueMetadata(), SqsErrorMapping.QueueDoesNotExist());
            }

            await Task.Delay(ParseRetryPollInterval, ct).ConfigureAwait(false);
        }
    }

    private void RememberSuccessfulWriteCore(
        string serviceBusNamespace,
        string queueName,
        SqsQueueTagStore.QueueMetadata metadata,
        string? previousVersionEtag,
        DateTimeOffset? previousUpdatedAt,
        string? writeVersionEtag,
        DateTimeOffset? writeCompletedAtUtc) =>
        SetCore(
            serviceBusNamespace,
            queueName,
            metadata,
            preferForEmptyReads: true,
            previousVersionEtag,
            previousUpdatedAt,
            writeVersionEtag,
            writeCompletedAtUtc);

    private void ApplyFreshSnapshotCore(
        string serviceBusNamespace,
        string queueName,
        string? freshReadEtag,
        QueueDescriptionProperties props)
    {
        var key = BuildKey(serviceBusNamespace, queueName);
        var now = DateTimeOffset.UtcNow;
        if (!_entries.TryGetValue(key, out var entry) || entry.ExpiresAtUtc <= now || !entry.PreferForEmptyReads)
        {
            return;
        }

        var comparison = CompareFreshReadVersion(entry, freshReadEtag, props.UpdatedAt);
        var existing = SqsQueueTagStore.DecodeMetadata(props.UserMetadata);
        if (!IsEmpty(existing))
        {
            if (comparison == FreshReadVersionComparison.MatchesPreviousBackendRead)
            {
                RepairFreshReadFromEntry(entry, props);
            }
            else
            {
                SetCore(
                    serviceBusNamespace,
                    queueName,
                    existing,
                    preferForEmptyReads: false,
                    previousVersionEtag: null,
                    previousUpdatedAt: null,
                    writeVersionEtag: freshReadEtag,
                    writeCompletedAtUtc: null);
            }

            return;
        }

        if (IsEmpty(entry.Metadata))
        {
            return;
        }

        if (comparison == FreshReadVersionComparison.DefinitelyNewer)
        {
            _entries.TryRemove(new KeyValuePair<string, CacheEntry>(key, entry));
            return;
        }

        if (comparison == FreshReadVersionComparison.Unknown)
        {
            return;
        }

        RepairFreshReadFromEntry(entry, props);
    }

    private bool TryGet(string serviceBusNamespace, string queueName, out SqsQueueTagStore.QueueMetadata metadata)
    {
        var key = BuildKey(serviceBusNamespace, queueName);
        var now = DateTimeOffset.UtcNow;
        if (_entries.TryGetValue(key, out var entry))
        {
            if (entry.ExpiresAtUtc > now)
            {
                metadata = SqsQueueTagStore.CloneMetadata(entry.Metadata);
                return true;
            }

            _entries.TryRemove(key, out _);
        }

        metadata = new SqsQueueTagStore.QueueMetadata();
        return false;
    }

    private void SetCore(
        string serviceBusNamespace,
        string queueName,
        SqsQueueTagStore.QueueMetadata metadata,
        bool preferForEmptyReads,
        string? previousVersionEtag,
        DateTimeOffset? previousUpdatedAt,
        string? writeVersionEtag,
        DateTimeOffset? writeCompletedAtUtc)
    {
        TrimExpired(DateTimeOffset.UtcNow);
        var key = BuildKey(serviceBusNamespace, queueName);
        if (_entries.Count >= _maxEntries && !_entries.ContainsKey(key))
        {
            return;
        }

        _entries[key] = new CacheEntry(
            SqsQueueTagStore.CloneMetadata(metadata),
            preferForEmptyReads,
            string.IsNullOrWhiteSpace(previousVersionEtag) ? null : previousVersionEtag,
            previousUpdatedAt,
            string.IsNullOrWhiteSpace(writeVersionEtag) ? null : writeVersionEtag,
            writeCompletedAtUtc ?? DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow + _cacheTtl);
    }

    private void TrimExpired(DateTimeOffset now)
    {
        foreach (var pair in _entries)
        {
            if (pair.Value.ExpiresAtUtc <= now)
            {
                _entries.TryRemove(pair.Key, out _);
            }
        }
    }

    private static FreshReadVersionComparison CompareFreshReadVersion(
        CacheEntry entry,
        string? freshReadEtag,
        DateTimeOffset? freshUpdatedAt)
    {
        if (!string.IsNullOrWhiteSpace(freshReadEtag))
        {
            if (!string.IsNullOrWhiteSpace(entry.WriteVersionEtag) &&
                string.Equals(entry.WriteVersionEtag, freshReadEtag, StringComparison.Ordinal))
            {
                return FreshReadVersionComparison.MatchesLocalWrite;
            }

            if (!string.IsNullOrWhiteSpace(entry.PreviousVersionEtag) &&
                string.Equals(entry.PreviousVersionEtag, freshReadEtag, StringComparison.Ordinal))
            {
                return FreshReadVersionComparison.MatchesPreviousBackendRead;
            }

            if (!string.IsNullOrWhiteSpace(entry.WriteVersionEtag) ||
                !string.IsNullOrWhiteSpace(entry.PreviousVersionEtag))
            {
                return FreshReadVersionComparison.DefinitelyNewer;
            }
        }

        if (entry.PreviousUpdatedAt is { } previousUpdatedAt &&
            freshUpdatedAt is { } updatedAt &&
            updatedAt == previousUpdatedAt)
        {
            return FreshReadVersionComparison.MatchesPreviousBackendRead;
        }

        if (entry.PreviousUpdatedAt is { } knownPreviousUpdatedAt &&
            freshUpdatedAt is { } newerUpdatedAt &&
            newerUpdatedAt > knownPreviousUpdatedAt)
        {
            return FreshReadVersionComparison.DefinitelyNewer;
        }

        return FreshReadVersionComparison.Unknown;
    }

    private static string BuildKey(string serviceBusNamespace, string queueName)
        => serviceBusNamespace + "|" + queueName;

    private static bool IsEmpty(SqsQueueTagStore.QueueMetadata metadata) =>
        metadata.Tags.Count == 0
        && metadata.DelaySeconds is null
        && metadata.ReceiveMessageWaitTimeSeconds is null
        && metadata.PurgeCooldownUntilUnixSeconds is null;

    private static void RepairFreshReadFromEntry(CacheEntry entry, QueueDescriptionProperties props)
    {
        if (!SqsQueueTagStore.TryEncodeMetadata(entry.Metadata, out var userMetadata))
        {
            return;
        }

        props.UserMetadata = string.IsNullOrEmpty(userMetadata) ? null : userMetadata;
    }

    private sealed record CacheEntry(
        SqsQueueTagStore.QueueMetadata Metadata,
        bool PreferForEmptyReads,
        string? PreviousVersionEtag,
        DateTimeOffset? PreviousUpdatedAt,
        string? WriteVersionEtag,
        DateTimeOffset WriteCompletedAtUtc,
        DateTimeOffset ExpiresAtUtc);

    private enum FreshReadVersionComparison
    {
        Unknown,
        MatchesLocalWrite,
        MatchesPreviousBackendRead,
        DefinitelyNewer,
    }
}
