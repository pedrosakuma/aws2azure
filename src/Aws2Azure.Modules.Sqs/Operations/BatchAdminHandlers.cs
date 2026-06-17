using System;
using System.Collections.Concurrent;
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
/// from Slice 3 (<see cref="ServiceBusClient.DeleteLockedMessageAsync"/> /
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
/// <see cref="_purgeStarted"/> reproduces the SQS
/// <c>PurgeQueueInProgress</c> guard (one purge per minute per queue).</para>
/// </summary>
internal static class BatchAdminHandlers
{
    internal const int MaxBatchEntries = 10;
    internal const int MaxBatchConcurrency = 5;
    public static readonly TimeSpan PurgeCoolDown = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan PurgeBudget = TimeSpan.FromSeconds(60);

    // Per-(namespace, queue) timestamp of the last accepted PurgeQueue call.
    // The cool-down is purely advisory (SQS enforces it server-side); we
    // emulate it locally so callers see PurgeQueueInProgress instead of
    // hammering the SB drain loop. Bounded growth: an entry per queue ever
    // purged through this proxy — acceptable for the scope.
    private static readonly ConcurrentDictionary<string, DateTimeOffset> _purgeStarted =
        new(StringComparer.Ordinal);

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
            using var resp = await sb.RenewLockAsync(queueName, decoded.MessageId, decoded.LockToken, ct).ConfigureAwait(false);
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

        var order = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < entries.Count; i++) order[entries[i].Id] = i;
        ok.Sort((a, b) => order[a.Id].CompareTo(order[b.Id]));
        failed.Sort((a, b) => order[a.Id].CompareTo(order[b.Id]));

        // Single advisory header — the per-entry duration is best-effort
        // (SB renew always uses the queue's LockDuration; see Slice 3 gap
        // doc). The batch handler keeps the header for parity with the
        // single-message ChangeMessageVisibility surface.
        context.Response.Headers["Aws2Azure-VisibilityClampedBatch"] = "true";

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
        // SetQueueAttributes is whole-entity replace against SB; attributes
        // with no native SB field (DelaySeconds, ReceiveMessageWaitTimeSeconds)
        // cannot be persisted yet because the proxy has no durable
        // metadata store. Rather than silently dropping them — and lying to
        // the next GetQueueAttributes / SendMessage caller — surface
        // InvalidAttributeName until the metadata store lands.
        foreach (var name in requestedAttrs.Keys)
        {
            if (name is "DelaySeconds" or "ReceiveMessageWaitTimeSeconds")
            {
                await WriteErrorAsync(context, parsed.Protocol,
                    SqsErrorMapping.InvalidAttributeNameForUpdate(name)).ConfigureAwait(false);
                return;
            }
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

        // Read existing description so we can PUT a fully-merged Atom entry
        // (SB has no PATCH for queue updates).
        using (var getResp = await sb.GetQueueAsync(queueName, ct).ConfigureAwait(false))
        {
            if (getResp.StatusCode == HttpStatusCode.NotFound)
            {
                await WriteErrorAsync(context, parsed.Protocol, SqsErrorMapping.QueueDoesNotExist()).ConfigureAwait(false);
                return;
            }
            if (!getResp.IsSuccessStatusCode)
            {
                await WriteErrorAsync(context, parsed.Protocol, SqsErrorMapping.FromServiceBus(getResp)).ConfigureAwait(false);
                return;
            }
            var xml = await getResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var entry = AtomQueueXmlReader.ParseQueueEntry(xml);
            if (entry is null)
            {
                await WriteErrorAsync(context, parsed.Protocol,
                    SqsErrorMapping.InternalError("aws2azure: failed to parse Service Bus queue description.")).ConfigureAwait(false);
                return;
            }

            var merged = QueueAttributeTranslator.Merge(entry.Properties, patch);
            var atomBody = AtomQueueXmlWriter.BuildQueueEntry(merged);
            using var putResp = await sb.UpdateQueueAsync(queueName, atomBody, ct).ConfigureAwait(false);
            if (!putResp.IsSuccessStatusCode)
            {
                await WriteErrorAsync(context, parsed.Protocol, SqsErrorMapping.FromServiceBus(putResp)).ConfigureAwait(false);
                return;
            }
        }

        await SqsResponseWriter.WriteSetQueueAttributesAsync(context, parsed.Protocol).ConfigureAwait(false);
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

        // SQS rule: at most one PurgeQueue per queue per 60 seconds.
        // Atomic compare-and-set: only one caller may transition the
        // timestamp into the active window even under concurrent purges.
        var key = sb.Namespace + "|" + queueName;
        var now = DateTimeOffset.UtcNow;
        while (true)
        {
            if (_purgeStarted.TryGetValue(key, out var last))
            {
                if (now - last < PurgeCoolDown)
                {
                    await WriteErrorAsync(context, parsed.Protocol, SqsErrorMapping.PurgeQueueInProgress(queueName)).ConfigureAwait(false);
                    return;
                }
                if (_purgeStarted.TryUpdate(key, now, last)) break;
            }
            else
            {
                if (_purgeStarted.TryAdd(key, now)) break;
            }
            // Lost the race; re-read and re-check.
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
        try
        {
            while (!purgeCt.IsCancellationRequested)
            {
                using var resp = await sb.PeekLockMessageAsync(queueName, TimeSpan.Zero, purgeCt).ConfigureAwait(false);
                if (resp.StatusCode == HttpStatusCode.NoContent || resp.StatusCode == (HttpStatusCode)204)
                {
                    break;
                }
                if (resp.StatusCode == HttpStatusCode.NotFound)
                {
                    await WriteErrorAsync(context, parsed.Protocol, SqsErrorMapping.QueueDoesNotExist()).ConfigureAwait(false);
                    return;
                }
                if (!resp.IsSuccessStatusCode)
                {
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
                    await WriteErrorAsync(context, parsed.Protocol, SqsErrorMapping.FromServiceBus(del)).ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Budget expired mid-drain: SQS contract allows partial purge;
            // return success and let the cool-down protect against immediate
            // retry.
        }

        await SqsResponseWriter.WritePurgeQueueAsync(context, parsed.Protocol).ConfigureAwait(false);
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

    private static List<(string Id, string ReceiptHandle)>? ParseDeleteEntriesJson(string? jsonBody)
    {
        if (string.IsNullOrEmpty(jsonBody)) return null;
        try
        {
            using var doc = JsonDocument.Parse(jsonBody);
            if (!doc.RootElement.TryGetProperty("Entries", out var arr) || arr.ValueKind != JsonValueKind.Array)
            {
                return null;
            }
            var entries = new List<(string, string)>(arr.GetArrayLength());
            foreach (var e in arr.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) return null;
                if (!e.TryGetProperty("Id", out var idEl) || idEl.ValueKind != JsonValueKind.String) return null;
                if (!e.TryGetProperty("ReceiptHandle", out var rhEl) || rhEl.ValueKind != JsonValueKind.String) return null;
                entries.Add((idEl.GetString()!, rhEl.GetString()!));
            }
            return entries;
        }
        catch (JsonException)
        {
            return null;
        }
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

    private static List<ChangeVisEntry>? ParseChangeVisEntriesJson(string? jsonBody)
    {
        if (string.IsNullOrEmpty(jsonBody)) return null;
        try
        {
            using var doc = JsonDocument.Parse(jsonBody);
            if (!doc.RootElement.TryGetProperty("Entries", out var arr) || arr.ValueKind != JsonValueKind.Array)
            {
                return null;
            }
            var entries = new List<ChangeVisEntry>(arr.GetArrayLength());
            foreach (var e in arr.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) return null;
                if (!e.TryGetProperty("Id", out var idEl) || idEl.ValueKind != JsonValueKind.String) return null;
                if (!e.TryGetProperty("ReceiptHandle", out var rhEl) || rhEl.ValueKind != JsonValueKind.String) return null;
                var vis = 0;
                if (e.TryGetProperty("VisibilityTimeout", out var vEl))
                {
                    if (vEl.ValueKind != JsonValueKind.Number || !vEl.TryGetInt32(out vis)) return null;
                }
                entries.Add(new ChangeVisEntry(idEl.GetString()!, rhEl.GetString()!, vis));
            }
            return entries;
        }
        catch (JsonException)
        {
            return null;
        }
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

    // Test-only hook so unit tests can reset cool-down state between runs.
    internal static void ResetPurgeCoolDownForTesting() => _purgeStarted.Clear();
}
