using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Aws2Azure.Modules.Sqs.Errors;
using Aws2Azure.Modules.Sqs.Internal;
using Aws2Azure.Modules.Sqs.WireProtocol;
using Aws2Azure.Modules.Sqs.Xml;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.Sqs.Operations;

/// <summary>
/// Slice-4 dispatch: <c>DeleteMessageBatch</c>,
/// <c>ChangeMessageVisibilityBatch</c>, <c>SetQueueAttributes</c>, and
/// <c>PurgeQueue</c>.
///
/// <para>The two batch operations parallel-fan-out the per-entry SB call
/// from Slice 3 (<see cref="ServiceBusClient.DeleteLockedMessageAsync"/>,
/// <see cref="ServiceBusClient.UnlockLockedMessageAsync"/> /
/// <see cref="ServiceBusClient.RenewLockAsync"/>) bounded by a small
/// concurrency cap, then aggregate <c>Successful</c> / <c>Failed</c> in
/// the SQS-shaped batch envelope.</para>
///
/// <para><c>SetQueueAttributes</c> does a read-merge-write: SB's management
/// API has whole-entity PUT semantics, so the handler GETs the current
/// QueueDescription, overlays the caller's patch via
/// <see cref="QueueAttributeTranslator.Merge"/>, and re-PUTs the full
/// Atom entry.</para>
///
/// <para><c>PurgeQueue</c> has no native SB equivalent — it is emulated by
/// a peek-lock + complete drain loop bounded by
/// <see cref="PurgeBudget"/>. A per-(namespace, queue) cool-down keyed by
/// the bounded cooldown tracker reproduces the SQS
/// <c>PurgeQueueInProgress</c> guard (one purge per minute per queue).</para>
/// </summary>
internal static class BatchAdminHandlers
{
    internal const int MaxBatchEntries = 10;
    internal const int MaxBatchConcurrency = 5;
    private const int QueueMetadataUpdateMaxAttempts = 3;
    public static readonly TimeSpan PurgeCoolDown = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan PurgeBudget = TimeSpan.FromSeconds(60);

    private static readonly PurgeCooldownTracker _purgeCooldowns = new(
        PurgeCoolDown,
        maxEntries: 1024);

    public static Task HandleAsync(
        HttpContext context,
        SqsParseResult parsed,
        ServiceBusClient sb,
        CancellationToken ct) =>
        parsed.Operation switch
        {
            SqsOperation.DeleteMessageBatch              => DeleteMessageBatchAsync(context, parsed, sb, ct),
            SqsOperation.ChangeMessageVisibilityBatch    => ChangeMessageVisibilityBatchAsync(context, parsed, sb, ct),
            SqsOperation.SetQueueAttributes              => SetQueueAttributesAsync(context, parsed, sb, ct),
            SqsOperation.PurgeQueue                      => PurgeQueueAsync(context, parsed, sb, ct),
            _                                            => WriteErrorAsync(context, parsed.Protocol,
                                                                SqsErrorMapping.NotImplemented(parsed.Operation)),
        };

    // --- DeleteMessageBatch / ChangeMessageVisibilityBatch ---------------

    internal sealed record ChangeVisEntry(string Id, string ReceiptHandle, int VisibilityTimeout);

    private static async Task DeleteMessageBatchAsync(
        HttpContext context, SqsParseResult parsed, ServiceBusClient sb, CancellationToken ct)
    {
        var queueName = ExtractQueueName(parsed);
        if (queueName is null)
        {
            await WriteErrorAsync(context, parsed.Protocol,
                SqsErrorMapping.InvalidParameterValue("QueueUrl", "QueueUrl is required and must be a queue URL.")).ConfigureAwait(false);
            return;
        }
        if (!QueueName.IsValid(queueName))
        {
            await WriteErrorAsync(context, parsed.Protocol, SqsErrorMapping.QueueNameInvalid()).ConfigureAwait(false);
            return;
        }

        var entries = ParseDeleteEntries(parsed);
        if (entries is null)
        {
            await WriteErrorAsync(context, parsed.Protocol,
                SqsErrorMapping.InvalidParameterValue("Entries", "Could not parse batch entries.")).ConfigureAwait(false);
            return;
        }
        if (!ValidateBatchShape(entries.Select(e => e.Id).ToList(), out var shapeError))
        {
            await WriteErrorAsync(context, parsed.Protocol, shapeError!.Value).ConfigureAwait(false);
            return;
        }

        var ok = new List<BatchEntryOk>(entries.Count);
        var failed = new List<BatchEntryError>();
        await ForEachBoundedAsync(entries, MaxBatchConcurrency, async entry =>
        {
            if (!ReceiptHandle.TryDecode(entry.ReceiptHandle, out var decoded))
            {
                lock (failed) failed.Add(new BatchEntryError(entry.Id, "ReceiptHandleIsInvalid",
                    "The input receipt handle is invalid.", SenderFault: true));
                return;
            }
            using var resp = await sb.DeleteLockedMessageAsync(queueName, decoded.MessageId, decoded.LockToken, ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                lock (ok) ok.Add(new BatchEntryOk(entry.Id));
                return;
            }
            var mapped = resp.StatusCode == HttpStatusCode.NotFound || resp.StatusCode == HttpStatusCode.Gone
                ? SqsErrorMapping.ReceiptHandleInvalid()
                : SqsErrorMapping.FromServiceBus(resp);
            lock (failed) failed.Add(new BatchEntryError(entry.Id, mapped.Code, mapped.Message,
                SenderFault: mapped.FaultType == SqsErrorResponse.FaultType.Sender));
        }, ct).ConfigureAwait(false);

        // Maintain caller-supplied Id order in the response.
        var order = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < entries.Count; i++) order[entries[i].Id] = i;
        ok.Sort((a, b) => order[a.Id].CompareTo(order[b.Id]));
        failed.Sort((a, b) => order[a.Id].CompareTo(order[b.Id]));

        await SqsResponseWriter.WriteDeleteMessageBatchAsync(context, parsed.Protocol, ok, failed).ConfigureAwait(false);
    }

    private static async Task ChangeMessageVisibilityBatchAsync(
        HttpContext context, SqsParseResult parsed, ServiceBusClient sb, CancellationToken ct)
    {
        var queueName = ExtractQueueName(parsed);
        if (queueName is null)
        {
            await WriteErrorAsync(context, parsed.Protocol,
                SqsErrorMapping.InvalidParameterValue("QueueUrl", "QueueUrl is required and must be a queue URL.")).ConfigureAwait(false);
            return;
        }
        if (!QueueName.IsValid(queueName))
        {
            await WriteErrorAsync(context, parsed.Protocol, SqsErrorMapping.QueueNameInvalid()).ConfigureAwait(false);
            return;
        }

        var entries = ParseChangeVisEntries(parsed);
        if (entries is null)
        {
            await WriteErrorAsync(context, parsed.Protocol,
                SqsErrorMapping.InvalidParameterValue("Entries", "Could not parse batch entries.")).ConfigureAwait(false);
            return;
        }
        if (!ValidateBatchShape(entries.Select(e => e.Id).ToList(), out var shapeError))
        {
            await WriteErrorAsync(context, parsed.Protocol, shapeError!.Value).ConfigureAwait(false);
            return;
        }

        var ok = new List<BatchEntryOk>(entries.Count);
        var failed = new List<BatchEntryError>();
        await ForEachBoundedAsync(entries, MaxBatchConcurrency, async entry =>
        {
            if (entry.VisibilityTimeout < 0 || entry.VisibilityTimeout > ReceiveMessageHandlers.MaxVisibilityTimeoutSeconds)
            {
                var m = SqsErrorMapping.VisibilityTimeoutInvalid();
                lock (failed) failed.Add(new BatchEntryError(entry.Id, m.Code, m.Message,
                    SenderFault: m.FaultType == SqsErrorResponse.FaultType.Sender));
                return;
            }
            if (!ReceiptHandle.TryDecode(entry.ReceiptHandle, out var decoded))
            {
                lock (failed) failed.Add(new BatchEntryError(entry.Id, "ReceiptHandleIsInvalid",
                    "The input receipt handle is invalid.", SenderFault: true));
                return;
            }
            using var resp = entry.VisibilityTimeout == 0
                ? await sb.UnlockLockedMessageAsync(
                    queueName, decoded.MessageId, decoded.LockToken, ct).ConfigureAwait(false)
                : await sb.RenewLockAsync(
                    queueName, decoded.MessageId, decoded.LockToken, ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                lock (ok) ok.Add(new BatchEntryOk(entry.Id));
                return;
            }
            var mapped = resp.StatusCode == HttpStatusCode.NotFound
                ? SqsErrorMapping.ReceiptHandleInvalid()
                : SqsErrorMapping.FromServiceBus(resp);
            lock (failed) failed.Add(new BatchEntryError(entry.Id, mapped.Code, mapped.Message,
                SenderFault: mapped.FaultType == SqsErrorResponse.FaultType.Sender));
        }, ct).ConfigureAwait(false);

        var order = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < entries.Count; i++) order[entries[i].Id] = i;
        ok.Sort((a, b) => order[a.Id].CompareTo(order[b.Id]));
        failed.Sort((a, b) => order[a.Id].CompareTo(order[b.Id]));

        // Single advisory header — the per-entry duration is best-effort
        // (SB renew always uses the queue's LockDuration; see Slice 3 gap
        // doc). The batch handler keeps the header for parity with the
        // single-message ChangeMessageVisibility surface.
        if (entries.Any(static entry => entry.VisibilityTimeout > 0))
        {
            context.Response.Headers["Aws2Azure-VisibilityClampedBatch"] = "true";
        }

        await SqsResponseWriter.WriteChangeMessageVisibilityBatchAsync(context, parsed.Protocol, ok, failed).ConfigureAwait(false);
    }

    // --- SetQueueAttributes ---------------------------------------------

    private static async Task SetQueueAttributesAsync(
        HttpContext context, SqsParseResult parsed, ServiceBusClient sb, CancellationToken ct)
    {
        var queueName = ExtractQueueName(parsed);
        if (queueName is null)
        {
            await WriteErrorAsync(context, parsed.Protocol,
                SqsErrorMapping.InvalidParameterValue("QueueUrl", "QueueUrl is required and must be a queue URL.")).ConfigureAwait(false);
            return;
        }
        if (!QueueName.IsValid(queueName))
        {
            await WriteErrorAsync(context, parsed.Protocol, SqsErrorMapping.QueueNameInvalid()).ConfigureAwait(false);
            return;
        }

        var requestedAttrs = SqsQueueAttributeParser.ExtractAttributes(
            parsed, "Attribute", includeJsonPrimitiveValues: false);
        if (requestedAttrs.Count == 0)
        {
            // SQS treats an empty Attributes map as a no-op success.
            await SqsResponseWriter.WriteSetQueueAttributesAsync(context, parsed.Protocol).ConfigureAwait(false);
            return;
        }
        var err = QueueAttributeTranslator.ToServiceBusProperties(queueName, requestedAttrs, out var patch);
        if (err.IsError)
        {
            var mapping = err.Kind == QueueAttributeTranslator.QueueAttributeError.UnknownAttribute
                ? SqsErrorMapping.InvalidAttributeNameForUpdate(err.AttributeName)
                : SqsErrorMapping.InvalidAttributeValue(err.AttributeName, err.Message);
            await WriteErrorAsync(context, parsed.Protocol, mapping).ConfigureAwait(false);
            return;
        }

        for (var attempt = 0; attempt < QueueMetadataUpdateMaxAttempts; attempt++)
        {
            var read = await ReadQueueEntryWithETagAsync(context, parsed, sb, queueName, ct).ConfigureAwait(false);
            if (read is null)
            {
                return;
            }

            SqsQueueMetadataCache.ApplyFreshSnapshot(sb, queueName, read.Entry.Properties);
            SqsQueueTagStore.QueueMetadata? proxyMetadata = null;
            if (patch.DelaySeconds.HasValue || patch.ReceiveMessageWaitTimeSeconds.HasValue)
            {
                if (!SqsQueueTagStore.TryDecodeForMutation(
                        read.Entry.Properties.UserMetadata,
                        out var decodedMetadata,
                        out var metadataError))
                {
                    var invalidAttribute = patch.DelaySeconds.HasValue
                        ? "DelaySeconds"
                        : "ReceiveMessageWaitTimeSeconds";
                    await WriteErrorAsync(context, parsed.Protocol,
                        SqsErrorMapping.InvalidAttributeValue(
                            invalidAttribute,
                            metadataError ?? "Queue metadata is invalid.")).ConfigureAwait(false);
                    return;
                }

                proxyMetadata = decodedMetadata;
                if (patch.DelaySeconds.HasValue)
                {
                    proxyMetadata.DelaySeconds = NormalizeStoredValue(patch.DelaySeconds);
                }

                if (patch.ReceiveMessageWaitTimeSeconds.HasValue)
                {
                    proxyMetadata.ReceiveMessageWaitTimeSeconds = NormalizeStoredValue(patch.ReceiveMessageWaitTimeSeconds);
                }
            }

            var merged = QueueAttributeTranslator.Merge(read.Entry.Properties, patch);
            if (proxyMetadata is not null)
            {
                if (!SqsQueueTagStore.TryEncodeMetadata(proxyMetadata, out var userMetadata))
                {
                    var invalidAttribute = patch.DelaySeconds.HasValue
                        ? "DelaySeconds"
                        : "ReceiveMessageWaitTimeSeconds";
                    await WriteErrorAsync(context, parsed.Protocol,
                        SqsErrorMapping.InvalidAttributeValue(
                            invalidAttribute,
                            $"Serialized queue metadata exceeds the Azure Service Bus UserMetadata limit of {SqsQueueTagStore.UserMetadataMaxLength} characters."))
                        .ConfigureAwait(false);
                    return;
                }

                merged.UserMetadata = string.IsNullOrEmpty(userMetadata) ? null : userMetadata;
            }
            var atomBody = AtomQueueXmlWriter.BuildQueueEntry(merged);
            using var putResp = string.IsNullOrWhiteSpace(read.ETag)
                ? await sb.UpdateQueueAsync(queueName, atomBody, ct).ConfigureAwait(false)
                : await sb.UpdateQueueAsync(queueName, atomBody, read.ETag, ct).ConfigureAwait(false);
            if (!putResp.IsSuccessStatusCode)
            {
                if (putResp.StatusCode == HttpStatusCode.PreconditionFailed)
                {
                    continue;
                }

                await WriteErrorAsync(context, parsed.Protocol, SqsErrorMapping.FromServiceBus(putResp)).ConfigureAwait(false);
                return;
            }

            SqsQueueMetadataCache.RememberSuccessfulWrite(sb, queueName, merged);
            await SqsResponseWriter.WriteSetQueueAttributesAsync(context, parsed.Protocol).ConfigureAwait(false);
            return;
        }

        await WriteErrorAsync(context, parsed.Protocol, SqsErrorMapping.QueueTagUpdateConflict()).ConfigureAwait(false);
    }

    // --- PurgeQueue -----------------------------------------------------

    private static async Task PurgeQueueAsync(
        HttpContext context, SqsParseResult parsed, ServiceBusClient sb, CancellationToken ct)
    {
        var queueName = ExtractQueueName(parsed);
        if (queueName is null)
        {
            await WriteErrorAsync(context, parsed.Protocol,
                SqsErrorMapping.InvalidParameterValue("QueueUrl", "QueueUrl is required and must be a queue URL.")).ConfigureAwait(false);
            return;
        }
        if (!QueueName.IsValid(queueName))
        {
            await WriteErrorAsync(context, parsed.Protocol, SqsErrorMapping.QueueNameInvalid()).ConfigureAwait(false);
            return;
        }

        // SQS rule: at most one accepted PurgeQueue per queue per 60 seconds.
        // Two layers cooperate here:
        //  1. The bounded in-process tracker gives same-replica callers a
        //     fast reject without a network round trip (it removes expired
        //     keys opportunistically and has a hard entry cap, so
        //     high-cardinality queue names cannot grow static process state
        //     without bound).
        //  2. TryStartDistributedCooldownAsync persists the cool-down
        //     deadline in Service Bus QueueDescription.UserMetadata via an
        //     ETag compare-and-swap, so every proxy replica observes the
        //     same window (see docs/gaps/sqs/PurgeQueue.yaml). If the
        //     queue's UserMetadata is already owned by non-aws2azure content
        //     or too full to fit the cool-down marker, coordination degrades
        //     to the single-replica in-process tracker only — an explicit,
        //     documented tradeoff rather than a silent gap.
        var key = sb.Namespace + "|" + queueName;
        var now = DateTimeOffset.UtcNow;
        var start = _purgeCooldowns.TryStart(key, now);
        if (start == PurgeStartResult.InProgress)
        {
            await WriteErrorAsync(context, parsed.Protocol, SqsErrorMapping.PurgeQueueInProgress(queueName)).ConfigureAwait(false);
            return;
        }
        if (start == PurgeStartResult.CapacityExceeded)
        {
            await WriteErrorAsync(context, parsed.Protocol, SqsErrorMapping.PurgeStateCapacityExceeded()).ConfigureAwait(false);
            return;
        }

        var distributed = await TryStartDistributedCooldownAsync(context, parsed, sb, queueName, now, ct)
            .ConfigureAwait(false);
        if (distributed == DistributedCooldownOutcome.Failed)
        {
            // The failure response was already written by the GET/PUT
            // helper (NonExistentQueue, an upstream mapping, or a bounded
            // ETag-conflict exhaustion). No distributed marker was
            // committed on this path, so only the local reservation needs
            // releasing.
            _purgeCooldowns.Release(key, now);
            return;
        }
        if (distributed == DistributedCooldownOutcome.InProgress)
        {
            _purgeCooldowns.Release(key, now);
            await WriteErrorAsync(context, parsed.Protocol, SqsErrorMapping.PurgeQueueInProgress(queueName)).ConfigureAwait(false);
            return;
        }
        // Started or Unsupported (single-replica fallback): proceed to
        // drain, guarded at minimum by the local in-process reservation
        // taken above.

        // If TryStartDistributedCooldownAsync committed the deadline to
        // Service Bus, the local and distributed reservations must stay
        // symmetric: any drain failure below has to roll back *both*, or a
        // failed purge attempt (client got an error, no purge happened)
        // would still leave a live cross-replica cool-down in place and
        // incorrectly reject an immediate retry with PurgeQueueInProgress.
        var distributedUntilUnix = now.ToUnixTimeSeconds() + (long)PurgeCoolDown.TotalSeconds;
        var distributedStarted = distributed == DistributedCooldownOutcome.Started;

        async Task ReleaseReservationsAsync()
        {
            _purgeCooldowns.Release(key, now);
            if (distributedStarted)
            {
                // Best-effort: a failed rollback just means the retry waits
                // out the remainder of a window it never actually held —
                // the same behavior as before distributed coordination
                // existed. Use CancellationToken.None so an already-expired
                // budget or an aborted request doesn't prevent the attempt.
                await ClearDistributedCooldownAsync(sb, queueName, distributedUntilUnix, CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }

        // Drain: peek-lock + DELETE in a loop bounded by PurgeBudget.
        // SB has no native purge over REST; this matches the SQS contract
        // (eventually consistent; messages in flight are not deleted). The
        // budget is enforced via a linked CTS so in-flight SB calls are
        // cancelled once the deadline is reached, not just checked between
        // calls.
        using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budgetCts.CancelAfter(PurgeBudget);
        var purgeCt = budgetCts.Token;
        var accepted = false;
        try
        {
            while (!purgeCt.IsCancellationRequested)
            {
                using var resp = await sb.PeekLockMessageAsync(queueName, TimeSpan.Zero, purgeCt).ConfigureAwait(false);
                if (resp.StatusCode == HttpStatusCode.NoContent || resp.StatusCode == (HttpStatusCode)204)
                {
                    accepted = true;
                    break;
                }
                if (resp.StatusCode == HttpStatusCode.NotFound)
                {
                    await ReleaseReservationsAsync().ConfigureAwait(false);
                    await WriteErrorAsync(context, parsed.Protocol, SqsErrorMapping.QueueDoesNotExist()).ConfigureAwait(false);
                    return;
                }
                if (!resp.IsSuccessStatusCode)
                {
                    await ReleaseReservationsAsync().ConfigureAwait(false);
                    await WriteErrorAsync(context, parsed.Protocol, SqsErrorMapping.FromServiceBus(resp)).ConfigureAwait(false);
                    return;
                }

                if (!TryReadIdsForDelete(resp, out var msgId, out var lockToken)) continue;
                using var del = await sb.DeleteLockedMessageAsync(queueName, msgId, lockToken, purgeCt).ConfigureAwait(false);
                // Ignore 404/410 here — the lock may have already expired, in
                // which case the message will reappear and be purged on a
                // subsequent call. We only abort on hard upstream errors.
                if (!del.IsSuccessStatusCode && del.StatusCode != HttpStatusCode.NotFound
                    && del.StatusCode != HttpStatusCode.Gone)
                {
                    await ReleaseReservationsAsync().ConfigureAwait(false);
                    await WriteErrorAsync(context, parsed.Protocol, SqsErrorMapping.FromServiceBus(del)).ConfigureAwait(false);
                    return;
                }
            }
            ct.ThrowIfCancellationRequested();
            accepted = true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Budget expired mid-drain: SQS contract allows partial purge;
            // return success and let the cool-down protect against immediate
            // retry.
            accepted = true;
        }
        catch
        {
            await ReleaseReservationsAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            if (!accepted && ct.IsCancellationRequested)
                await ReleaseReservationsAsync().ConfigureAwait(false);
        }

        await SqsResponseWriter.WritePurgeQueueAsync(context, parsed.Protocol).ConfigureAwait(false);
    }

    /// <summary>
    /// Persists the SQS <c>PurgeQueueInProgress</c> 60-second cool-down
    /// deadline in Service Bus <c>QueueDescription.UserMetadata</c> using a
    /// GET + PUT-with-If-Match compare-and-swap, so all proxy replicas
    /// observe the same window (see docs/gaps/sqs/PurgeQueue.yaml). Falls
    /// back to <see cref="DistributedCooldownOutcome.Unsupported"/> — single
    /// replica coordination only, via <see cref="_purgeCooldowns"/> — when
    /// the queue's UserMetadata is already owned by non-aws2azure content or
    /// too full to fit the cool-down marker alongside existing tags.
    /// </summary>
    private static async Task<DistributedCooldownOutcome> TryStartDistributedCooldownAsync(
        HttpContext context,
        SqsParseResult parsed,
        ServiceBusClient sb,
        string queueName,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var nowUnix = now.ToUnixTimeSeconds();
        for (var attempt = 0; attempt < QueueMetadataUpdateMaxAttempts; attempt++)
        {
            var read = await ReadQueueEntryWithETagAsync(context, parsed, sb, queueName, ct).ConfigureAwait(false);
            if (read is null)
            {
                // ReadQueueEntryWithETagAsync already wrote the failure
                // response (NonExistentQueue or a mapped upstream error).
                return DistributedCooldownOutcome.Failed;
            }

            SqsQueueMetadataCache.ApplyFreshSnapshot(sb, queueName, read.Entry.Properties);
            if (!SqsQueueTagStore.TryDecodeForMutation(
                    read.Entry.Properties.UserMetadata, out var metadata, out _))
            {
                return DistributedCooldownOutcome.Unsupported;
            }

            if (metadata.PurgeCooldownUntilUnixSeconds is { } until && until > nowUnix)
            {
                return DistributedCooldownOutcome.InProgress;
            }

            metadata.PurgeCooldownUntilUnixSeconds = nowUnix + (long)PurgeCoolDown.TotalSeconds;
            if (!SqsQueueTagStore.TryEncodeMetadata(metadata, out var userMetadata))
            {
                // The queue's existing tags already consume the Service Bus
                // UserMetadata budget; degrade rather than block the purge on
                // unrelated tag content.
                return DistributedCooldownOutcome.Unsupported;
            }

            read.Entry.Properties.UserMetadata = string.IsNullOrEmpty(userMetadata) ? null : userMetadata;
            var atomBody = AtomQueueXmlWriter.BuildQueueEntry(read.Entry.Properties);
            using var putResp = string.IsNullOrWhiteSpace(read.ETag)
                ? await sb.UpdateQueueAsync(queueName, atomBody, ct).ConfigureAwait(false)
                : await sb.UpdateQueueAsync(queueName, atomBody, read.ETag, ct).ConfigureAwait(false);
            if (putResp.IsSuccessStatusCode)
            {
                SqsQueueMetadataCache.Set(sb, queueName, read.Entry.Properties);
                return DistributedCooldownOutcome.Started;
            }
            if (putResp.StatusCode == HttpStatusCode.PreconditionFailed)
            {
                continue;
            }

            await WriteErrorAsync(context, parsed.Protocol, SqsErrorMapping.FromServiceBus(putResp)).ConfigureAwait(false);
            return DistributedCooldownOutcome.Failed;
        }

        await WriteErrorAsync(context, parsed.Protocol, SqsErrorMapping.QueueTagUpdateConflict()).ConfigureAwait(false);
        return DistributedCooldownOutcome.Failed;
    }

    internal enum DistributedCooldownOutcome
    {
        Started,
        InProgress,
        Unsupported,
        Failed,
    }

    /// <summary>
    /// Best-effort rollback of the distributed cool-down marker committed by
    /// <see cref="TryStartDistributedCooldownAsync"/> when a purge fails
    /// *after* that marker was already persisted (queue deleted mid-drain,
    /// a peek-lock/delete error from Service Bus, or an unexpected
    /// exception). Without this, a failed purge attempt — the client got an
    /// error, no messages were actually purged — would still leave a live
    /// cross-replica cool-down in place and incorrectly reject an immediate
    /// retry with PurgeQueueInProgress for up to 60 seconds.
    ///
    /// This performs its own quiet GET + PUT-with-If-Match compare-and-swap
    /// (bounded by <see cref="QueueMetadataUpdateMaxAttempts"/>) and never
    /// writes an HTTP error response — the caller has already written (or is
    /// about to write) the real failure response for the drain, and this is
    /// purely cleanup. Every failure mode (queue gone, foreign metadata,
    /// exhausted retries, any exception) is swallowed: worst case a
    /// legitimate retry simply waits out the remainder of a cool-down window
    /// it never actually held, which is no worse than the fully in-process
    /// behavior that existed before distributed coordination. The marker is
    /// only cleared if it still equals <paramref name="expectedUntilUnix"/>,
    /// so a cool-down legitimately started by a concurrent replica after
    /// this failure (e.g. a retry that raced ahead and won) is never
    /// clobbered.
    /// </summary>
    private static async Task ClearDistributedCooldownAsync(
        ServiceBusClient sb, string queueName, long expectedUntilUnix, CancellationToken ct)
    {
        try
        {
            for (var attempt = 0; attempt < QueueMetadataUpdateMaxAttempts; attempt++)
            {
                using var getResp = await sb.GetQueueAsync(queueName, ct).ConfigureAwait(false);
                if (!getResp.IsSuccessStatusCode)
                {
                    // Queue gone, or some other GET failure: nothing to roll
                    // back.
                    return;
                }

                var xml = await getResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var entry = AtomQueueXmlReader.ParseQueueEntry(xml);
                if (entry is null) return;

                SqsQueueMetadataCache.ApplyFreshSnapshot(sb, queueName, entry.Properties);
                if (!SqsQueueTagStore.TryDecodeForMutation(
                        entry.Properties.UserMetadata, out var metadata, out _))
                {
                    // UserMetadata is no longer in the aws2azure envelope
                    // (or malformed) — nothing of ours left to clear.
                    return;
                }

                if (metadata.PurgeCooldownUntilUnixSeconds != expectedUntilUnix)
                {
                    // Already cleared, expired, or superseded by a
                    // concurrent replica's own legitimate cool-down —
                    // leave it alone.
                    return;
                }

                metadata.PurgeCooldownUntilUnixSeconds = null;
                if (!SqsQueueTagStore.TryEncodeMetadata(metadata, out var userMetadata))
                {
                    return;
                }

                entry.Properties.UserMetadata = string.IsNullOrEmpty(userMetadata) ? null : userMetadata;
                var atomBody = AtomQueueXmlWriter.BuildQueueEntry(entry.Properties);
                var eTag = ExtractETag(getResp);
                using var putResp = string.IsNullOrWhiteSpace(eTag)
                    ? await sb.UpdateQueueAsync(queueName, atomBody, ct).ConfigureAwait(false)
                    : await sb.UpdateQueueAsync(queueName, atomBody, eTag, ct).ConfigureAwait(false);
                if (putResp.IsSuccessStatusCode)
                {
                    SqsQueueMetadataCache.Set(sb, queueName, entry.Properties);
                    return;
                }
                if (putResp.StatusCode == HttpStatusCode.PreconditionFailed)
                {
                    continue;
                }

                // Any other upstream failure: give up quietly.
                return;
            }
        }
        catch
        {
            // Best-effort cleanup only — never let a rollback failure mask
            // the real error already being returned to the client.
        }
    }

    internal enum PurgeStartResult
    {
        Started,
        InProgress,
        CapacityExceeded,
    }

    internal sealed class PurgeCooldownTracker
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, DateTimeOffset> _started = new(StringComparer.Ordinal);
        private readonly TimeSpan _coolDown;
        private readonly int _maxEntries;

        public PurgeCooldownTracker(TimeSpan coolDown, int maxEntries)
        {
            if (coolDown <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(coolDown));
            if (maxEntries <= 0) throw new ArgumentOutOfRangeException(nameof(maxEntries));
            _coolDown = coolDown;
            _maxEntries = maxEntries;
        }

        public int Count
        {
            get
            {
                lock (_gate) return _started.Count;
            }
        }

        public PurgeStartResult TryStart(string key, DateTimeOffset now)
        {
            lock (_gate)
            {
                RemoveExpired(now);
                if (_started.ContainsKey(key))
                    return PurgeStartResult.InProgress;
                if (_started.Count >= _maxEntries)
                    return PurgeStartResult.CapacityExceeded;
                _started.Add(key, now);
                return PurgeStartResult.Started;
            }
        }

        public void Release(string key, DateTimeOffset startedAt)
        {
            lock (_gate)
            {
                if (_started.TryGetValue(key, out var current) && current == startedAt)
                    _started.Remove(key);
            }
        }

        public void Clear()
        {
            lock (_gate) _started.Clear();
        }

        private void RemoveExpired(DateTimeOffset now)
        {
            if (_started.Count == 0) return;
            List<string>? expired = null;
            foreach (var entry in _started)
            {
                if (now - entry.Value < _coolDown) continue;
                expired ??= new List<string>();
                expired.Add(entry.Key);
            }
            if (expired is null) return;
            foreach (var key in expired)
                _started.Remove(key);
        }
    }

    private static bool TryReadIdsForDelete(HttpResponseMessage resp, out string messageId, out string lockToken)
    {
        messageId = string.Empty;
        lockToken = string.Empty;
        if (resp.Headers.TryGetValues("BrokerProperties", out var bp))
        {
            var raw = bp.FirstOrDefault();
            if (!string.IsNullOrEmpty(raw))
            {
                try
                {
                    using var doc = JsonDocument.Parse(raw);
                    if (doc.RootElement.TryGetProperty("MessageId", out var mid) && mid.ValueKind == JsonValueKind.String)
                        messageId = mid.GetString() ?? string.Empty;
                    if (doc.RootElement.TryGetProperty("LockToken", out var lt) && lt.ValueKind == JsonValueKind.String)
                        lockToken = lt.GetString() ?? string.Empty;
                }
                catch (JsonException) { return false; }
            }
        }
        if (string.IsNullOrEmpty(lockToken) && resp.Headers.Location is { } loc)
        {
            var segments = (loc.IsAbsoluteUri ? loc.AbsolutePath : loc.OriginalString).TrimEnd('/').Split('/');
            if (segments.Length >= 1)
            {
                lockToken = Uri.UnescapeDataString(segments[^1]);
            }
        }
        return !string.IsNullOrEmpty(messageId) && !string.IsNullOrEmpty(lockToken);
    }

    // --- Entry parsing helpers ------------------------------------------

    internal static List<(string Id, string ReceiptHandle)>? ParseDeleteEntries(SqsParseResult parsed)
    {
        if (parsed.Protocol == SqsWireProtocol.AwsJson)
        {
            return ParseDeleteEntriesJson(parsed.JsonBody);
        }
        return ParseDeleteEntriesQuery(parsed.Parameters);
    }

    private static List<(string Id, string ReceiptHandle)>? ParseDeleteEntriesQuery(IReadOnlyDictionary<string, string> parameters)
    {
        const string Prefix = "DeleteMessageBatchRequestEntry.";
        var groups = GroupByIndex(parameters, Prefix);
        if (groups is null) return new List<(string, string)>();
        var entries = new List<(string Id, string ReceiptHandle)>(groups.Count);
        foreach (var (_, bag) in groups)
        {
            if (!bag.TryGetValue("Id", out var id) ||
                !bag.TryGetValue("ReceiptHandle", out var rh))
            {
                return null;
            }
            entries.Add((id, rh));
        }
        return entries;
    }

    private static List<(string Id, string ReceiptHandle)>? ParseDeleteEntriesJson(string? jsonBody) =>
        SqsBatchEntryJson.Parse<(string Id, string ReceiptHandle)>(jsonBody, TryParseDeleteEntryJson);

    private static bool TryParseDeleteEntryJson(JsonElement e, string id, out (string Id, string ReceiptHandle) value)
    {
        value = default;
        if (!e.TryGetProperty("ReceiptHandle", out var rhEl) || rhEl.ValueKind != JsonValueKind.String) return false;
        value = (id, rhEl.GetString()!);
        return true;
    }

    internal static List<ChangeVisEntry>? ParseChangeVisEntries(SqsParseResult parsed)
    {
        if (parsed.Protocol == SqsWireProtocol.AwsJson)
        {
            return ParseChangeVisEntriesJson(parsed.JsonBody);
        }
        return ParseChangeVisEntriesQuery(parsed.Parameters);
    }

    private static List<ChangeVisEntry>? ParseChangeVisEntriesQuery(IReadOnlyDictionary<string, string> parameters)
    {
        const string Prefix = "ChangeMessageVisibilityBatchRequestEntry.";
        var groups = GroupByIndex(parameters, Prefix);
        if (groups is null) return new List<ChangeVisEntry>();
        var entries = new List<ChangeVisEntry>(groups.Count);
        foreach (var (_, bag) in groups)
        {
            if (!bag.TryGetValue("Id", out var id) ||
                !bag.TryGetValue("ReceiptHandle", out var rh))
            {
                return null;
            }
            var vis = 0;
            if (bag.TryGetValue("VisibilityTimeout", out var v))
            {
                if (!int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out vis))
                {
                    return null;
                }
            }
            entries.Add(new ChangeVisEntry(id, rh, vis));
        }
        return entries;
    }

    private static List<ChangeVisEntry>? ParseChangeVisEntriesJson(string? jsonBody) =>
        SqsBatchEntryJson.Parse<ChangeVisEntry>(jsonBody, TryParseChangeVisEntryJson);

    private static bool TryParseChangeVisEntryJson(JsonElement e, string id, out ChangeVisEntry value)
    {
        value = null!;
        if (!e.TryGetProperty("ReceiptHandle", out var rhEl) || rhEl.ValueKind != JsonValueKind.String) return false;
        var vis = 0;
        if (e.TryGetProperty("VisibilityTimeout", out var vEl))
        {
            if (vEl.ValueKind != JsonValueKind.Number || !vEl.TryGetInt32(out vis)) return false;
        }
        value = new ChangeVisEntry(id, rhEl.GetString()!, vis);
        return true;
    }

    private static SortedDictionary<int, Dictionary<string, string>>? GroupByIndex(
        IReadOnlyDictionary<string, string> parameters, string prefix)
    {
        var groups = new SortedDictionary<int, Dictionary<string, string>>();
        foreach (var kv in parameters)
        {
            if (!kv.Key.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var rest = kv.Key.AsSpan(prefix.Length);
            var dot = rest.IndexOf('.');
            if (dot <= 0) continue;
            if (!int.TryParse(rest[..dot], NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx))
                continue;
            var sub = rest[(dot + 1)..].ToString();
            if (!groups.TryGetValue(idx, out var bag))
            {
                bag = new Dictionary<string, string>(StringComparer.Ordinal);
                groups[idx] = bag;
            }
            bag[sub] = kv.Value;
        }
        return groups;
    }

    // --- Shared batch validation ----------------------------------------

    /// <summary>
    /// Shared shape validation across both batch endpoints:
    /// EmptyBatchRequest / TooManyEntries / unique-Id / Id charset.
    /// </summary>
    internal static bool ValidateBatchShape(IReadOnlyList<string> ids, out SqsErrorMapping.Mapping? error)
    {
        if (ids.Count == 0)
        {
            error = SqsErrorMapping.EmptyBatchRequest();
            return false;
        }
        if (ids.Count > MaxBatchEntries)
        {
            error = SqsErrorMapping.TooManyEntriesInBatchRequest(ids.Count);
            return false;
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            if (!IsValidBatchEntryId(id))
            {
                error = SqsErrorMapping.InvalidBatchEntryId(id);
                return false;
            }
            if (!seen.Add(id))
            {
                error = SqsErrorMapping.BatchEntryIdsNotDistinct(id);
                return false;
            }
        }
        error = null;
        return true;
    }

    private static bool IsValidBatchEntryId(string id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 80) return false;
        foreach (var c in id)
        {
            if (!(char.IsLetterOrDigit(c) || c == '-' || c == '_')) return false;
        }
        return true;
    }

    internal static async Task ForEachBoundedAsync<T>(
        IReadOnlyList<T> items, int maxConcurrency, Func<T, Task> body, CancellationToken ct)
    {
        if (items.Count == 0) return;
        var sem = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        var tasks = new List<Task>(items.Count);
        try
        {
            foreach (var item in items)
            {
                // If scheduling is cancelled (WaitAsync throws OCE) we still
                // need to await any tasks already started before disposing
                // the semaphore — otherwise their `sem.Release()` raises
                // ObjectDisposedException and AMQP side effects keep running
                // on a torn-down call stack.
                await sem.WaitAsync(ct).ConfigureAwait(false);
                tasks.Add(Task.Run(async () =>
                {
                    try { await body(item).ConfigureAwait(false); }
                    finally { sem.Release(); }
                }, ct));
            }
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        finally
        {
            if (tasks.Count > 0)
            {
                try { await Task.WhenAll(tasks).ConfigureAwait(false); }
                catch { /* swallow — the original throw (cancel or body error) propagates from the try block. */ }
            }
            sem.Dispose();
        }
    }

    // --- Parameter extraction ------------------------------------------

    private static string? ExtractQueueName(SqsParseResult parsed) =>
        SqsParameterHelpers.ExtractQueueName(parsed);

    private static Task WriteErrorAsync(HttpContext context, SqsWireProtocol protocol, SqsErrorMapping.Mapping mapping) =>
        SqsParameterHelpers.WriteErrorAsync(context, protocol, mapping);

    private static int? NormalizeStoredValue(int? value)
        => value is > 0 ? value : null;

    private static async Task<QueueReadResult?> ReadQueueEntryWithETagAsync(
        HttpContext context,
        SqsParseResult parsed,
        ServiceBusClient sb,
        string queueName,
        CancellationToken ct)
    {
        using var getResp = await sb.GetQueueAsync(queueName, ct).ConfigureAwait(false);
        if (getResp.StatusCode == HttpStatusCode.NotFound)
        {
            await WriteErrorAsync(context, parsed.Protocol, SqsErrorMapping.QueueDoesNotExist()).ConfigureAwait(false);
            return null;
        }
        if (!getResp.IsSuccessStatusCode)
        {
            await WriteErrorAsync(context, parsed.Protocol, SqsErrorMapping.FromServiceBus(getResp)).ConfigureAwait(false);
            return null;
        }

        var xml = await getResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var entry = AtomQueueXmlReader.ParseQueueEntry(xml);
        if (entry is null)
        {
            await WriteErrorAsync(context, parsed.Protocol,
                SqsErrorMapping.InternalError("aws2azure: failed to parse Service Bus queue description.")).ConfigureAwait(false);
            return null;
        }

        var eTag = ExtractETag(getResp);

        return new QueueReadResult(entry, eTag);
    }

    private static string? ExtractETag(HttpResponseMessage resp)
    {
        var eTag = resp.Headers.ETag?.Tag;
        if (string.IsNullOrWhiteSpace(eTag) &&
            resp.Headers.TryGetValues("ETag", out var values))
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

    private sealed record QueueReadResult(AtomQueueXmlReader.QueueEntry Entry, string? ETag);

    // Test-only hook so unit tests can reset cool-down state between runs.
    internal static void ResetPurgeCoolDownForTesting() => _purgeCooldowns.Clear();
}
