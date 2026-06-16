using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Aws2Azure.Amqp.ServiceBus;
using Aws2Azure.Modules.Sqs.Errors;
using Aws2Azure.Modules.Sqs.Internal;
using Aws2Azure.Modules.Sqs.WireProtocol;
using Aws2Azure.Modules.Sqs.Xml;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.Sqs.Operations;

internal static class AmqpSettleDispatcher
{
    // --- DeleteMessage -------------------------------------------------

    internal static async Task DeleteMessageAsync(
        HttpContext context, SqsParseResult parsed, IAmqpReceiverProvider receivers, CancellationToken ct)
    {
        var queueName = AmqpReceiveParameters.ExtractQueueName(parsed);
        if (queueName is null)
        {
            await AmqpReceiveParameters.WriteErrorAsync(context, parsed.Protocol,
                SqsErrorMapping.InvalidParameterValue("QueueUrl", "QueueUrl is required and must be a queue URL.")).ConfigureAwait(false);
            return;
        }
        if (!AmqpReceiveParameters.TryGetParam(parsed, "ReceiptHandle", out var handle))
        {
            await AmqpReceiveParameters.WriteErrorAsync(context, parsed.Protocol,
                SqsErrorMapping.MissingParameter("ReceiptHandle")).ConfigureAwait(false);
            return;
        }
        if (!AmqpReceiptHandle.TryDecode(handle, out var decoded))
        {
            await AmqpReceiveParameters.WriteErrorAsync(context, parsed.Protocol, SqsErrorMapping.ReceiptHandleInvalid()).ConfigureAwait(false);
            return;
        }
        // The handle carries the queue it was minted against; if the caller
        // points DeleteMessage at a different queue we treat the handle as
        // invalid rather than fanning out across receivers.
        if (!string.Equals(decoded.QueueName, queueName, StringComparison.OrdinalIgnoreCase))
        {
            await AmqpReceiveParameters.WriteErrorAsync(context, parsed.Protocol, SqsErrorMapping.ReceiptHandleInvalid()).ConfigureAwait(false);
            return;
        }

        ServiceBusReceiver? receiver;
        try
        {
            receiver = await TryAcquireSettleReceiverAsync(receivers, queueName, decoded.SessionId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await AmqpReceiveParameters.WriteErrorAsync(context, parsed.Protocol,
                AmqpErrorMapper.MapAmqpException(ex, "DeleteMessage")).ConfigureAwait(false);
            return;
        }
        if (receiver is null)
        {
            // FIFO + no cached session receiver for this session-id =
            // the lock that minted this handle is gone (session expired,
            // receiver invalidated, or proxy restarted). Surface as a
            // stale handle without acquiring a fresh session lock that
            // would block the MessageGroupId.
            await AmqpReceiveParameters.WriteErrorAsync(context, parsed.Protocol, SqsErrorMapping.ReceiptHandleInvalid()).ConfigureAwait(false);
            return;
        }

        bool settled;
        try
        {
            settled = await receiver.CompleteAsync(decoded.LockToken, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            await InvalidateSettleReceiverAsync(receivers, queueName, decoded.SessionId).ConfigureAwait(false);
            await AmqpReceiveParameters.WriteErrorAsync(context, parsed.Protocol,
                AmqpErrorMapper.MapSettleException(ex, "DeleteMessage")).ConfigureAwait(false);
            return;
        }
        if (!settled)
        {
            // Cache miss = already settled, lock expired, sender-settled
            // at receive time, or originated from a torn-down receiver
            // instance. SQS surfaces this as an invalid receipt handle.
            await AmqpReceiveParameters.WriteErrorAsync(context, parsed.Protocol, SqsErrorMapping.ReceiptHandleInvalid()).ConfigureAwait(false);
            return;
        }

        await SqsResponseWriter.WriteDeleteMessageAsync(context, parsed.Protocol).ConfigureAwait(false);
    }

    // --- ChangeMessageVisibility ---------------------------------------

    internal static async Task ChangeMessageVisibilityAsync(
        HttpContext context, SqsParseResult parsed, IAmqpReceiverProvider receivers, CancellationToken ct)
    {
        var queueName = AmqpReceiveParameters.ExtractQueueName(parsed);
        if (queueName is null)
        {
            await AmqpReceiveParameters.WriteErrorAsync(context, parsed.Protocol,
                SqsErrorMapping.InvalidParameterValue("QueueUrl", "QueueUrl is required and must be a queue URL.")).ConfigureAwait(false);
            return;
        }
        if (!AmqpReceiveParameters.TryGetParam(parsed, "ReceiptHandle", out var handle))
        {
            await AmqpReceiveParameters.WriteErrorAsync(context, parsed.Protocol,
                SqsErrorMapping.MissingParameter("ReceiptHandle")).ConfigureAwait(false);
            return;
        }
        if (!AmqpReceiptHandle.TryDecode(handle, out var decoded))
        {
            await AmqpReceiveParameters.WriteErrorAsync(context, parsed.Protocol, SqsErrorMapping.ReceiptHandleInvalid()).ConfigureAwait(false);
            return;
        }
        if (!string.Equals(decoded.QueueName, queueName, StringComparison.OrdinalIgnoreCase))
        {
            await AmqpReceiveParameters.WriteErrorAsync(context, parsed.Protocol, SqsErrorMapping.ReceiptHandleInvalid()).ConfigureAwait(false);
            return;
        }
        if (!AmqpReceiveParameters.TryParseBoundedInt(parsed, "VisibilityTimeout", min: 0, max: AmqpReceiveMessageHandlers.MaxVisibilityTimeoutSeconds, defaultValue: -1, out var visibility)
            || visibility < 0)
        {
            await AmqpReceiveParameters.WriteErrorAsync(context, parsed.Protocol, SqsErrorMapping.VisibilityTimeoutInvalid()).ConfigureAwait(false);
            return;
        }

        // VisibilityTimeout == 0 is SQS's "make this message immediately
        // available again" — closest SB primitive is Abandon on the
        // receiver link (no $management round-trip required, and it
        // honours the SB redelivery counter).
        if (visibility == 0)
        {
            ServiceBusReceiver? receiver;
            try
            {
                receiver = await TryAcquireSettleReceiverAsync(receivers, queueName, decoded.SessionId, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await AmqpReceiveParameters.WriteErrorAsync(context, parsed.Protocol,
                    AmqpErrorMapper.MapAmqpException(ex, "ChangeMessageVisibility")).ConfigureAwait(false);
                return;
            }
            if (receiver is null)
            {
                // Same stale-handle semantics as DeleteMessage above:
                // surface ReceiptHandleIsInvalid rather than opening a
                // fresh session lock.
                await AmqpReceiveParameters.WriteErrorAsync(context, parsed.Protocol, SqsErrorMapping.ReceiptHandleInvalid()).ConfigureAwait(false);
                return;
            }
            bool abandoned;
            try
            {
                abandoned = await receiver.AbandonAsync(decoded.LockToken, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                await InvalidateSettleReceiverAsync(receivers, queueName, decoded.SessionId).ConfigureAwait(false);
                await AmqpReceiveParameters.WriteErrorAsync(context, parsed.Protocol,
                    AmqpErrorMapper.MapSettleException(ex, "ChangeMessageVisibility")).ConfigureAwait(false);
                return;
            }
            if (!abandoned)
            {
                await AmqpReceiveParameters.WriteErrorAsync(context, parsed.Protocol, SqsErrorMapping.MessageNotInflight()).ConfigureAwait(false);
                return;
            }
            await SqsResponseWriter.WriteChangeMessageVisibilityAsync(context, parsed.Protocol).ConfigureAwait(false);
            return;
        }

        // visibility > 0 — round-trip RenewLock through the SB
        // $management link. Note SB always extends the lock by the
        // queue-level LockDuration (max 5 min); we can't honour
        // arbitrary visibility values. When the granted seconds differ
        // from the requested value we surface the divergence via the
        // Aws2Azure-VisibilityClamped header so anyone debugging the
        // proxy can tell what happened. See docs/gaps/sqs/ChangeMessageVisibility.yaml.
        //
        // Session-bound messages (v3 receipt handle) take the
        // session-flavoured renew path: SB extends the lock the broker
        // holds on the session itself, not on the individual delivery.
        // From the SQS-client perspective the effect is the same (the
        // group's in-flight messages stay locked) and the granted
        // duration / clamping semantics are identical.
        ServiceBusManagementClient mgmt;
        try
        {
            mgmt = await receivers.GetManagementClientAsync(queueName, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await AmqpReceiveParameters.WriteErrorAsync(context, parsed.Protocol,
                AmqpErrorMapper.MapAmqpException(ex, "ChangeMessageVisibility")).ConfigureAwait(false);
            return;
        }

        // FIFO: register session-link activity BEFORE the renew round-trip
        // so the idle-TTL sweeper (#262) can't dispose the cached receiver
        // while RenewSessionLockAsync is in flight (the management call is a
        // network round-trip; a slot already near the idle edge could
        // otherwise be swept mid-renew, defeating the renewal). No-op when no
        // receiver is cached.
        if (!string.IsNullOrEmpty(decoded.SessionId))
            _ = receivers.TryGetExistingSessionReceiver(queueName, decoded.SessionId);

        DateTimeOffset lockedUntil;
        try
        {
            lockedUntil = string.IsNullOrEmpty(decoded.SessionId)
                ? await mgmt.RenewLockAsync(decoded.LockToken, ct).ConfigureAwait(false)
                : await mgmt.RenewSessionLockAsync(decoded.SessionId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (ServiceBusManagementException ex)
        {
            await AmqpReceiveParameters.WriteErrorAsync(context, parsed.Protocol,
                AmqpErrorMapper.MapManagementException(ex, "ChangeMessageVisibility")).ConfigureAwait(false);
            return;
        }
        catch (Exception ex)
        {
            await receivers.InvalidateManagementClientAsync(queueName).ConfigureAwait(false);
            await AmqpReceiveParameters.WriteErrorAsync(context, parsed.Protocol,
                AmqpErrorMapper.MapAmqpException(ex, "ChangeMessageVisibility")).ConfigureAwait(false);
            return;
        }

        var grantedSeconds = Math.Max(0, (int)Math.Round((lockedUntil - DateTimeOffset.UtcNow).TotalSeconds));
        if (!string.IsNullOrEmpty(decoded.SessionId))
        {
            // Extend the idle window from renewal completion (the pre-renew
            // touch above protected the slot during the in-flight call).
            _ = receivers.TryGetExistingSessionReceiver(queueName, decoded.SessionId);
        }
        if (grantedSeconds != visibility)
        {
            context.Response.Headers["Aws2Azure-VisibilityClamped"] =
                string.Create(CultureInfo.InvariantCulture,
                    $"requested={visibility};granted={grantedSeconds}");
        }
        await SqsResponseWriter.WriteChangeMessageVisibilityAsync(context, parsed.Protocol).ConfigureAwait(false);
    }

    // --- DeleteMessageBatch / ChangeMessageVisibilityBatch -------------

    /// <summary>
    /// AMQP-flavoured DeleteMessageBatch. Mirrors the REST batch handler's
    /// shape (parallel per-entry fan-out bounded by
    /// <see cref="BatchAdminHandlers.MaxBatchConcurrency"/>, ID-ordered
    /// Successful/Failed aggregation) but each entry takes the same
    /// AMQP-flavoured settle path as <see cref="DeleteMessageAsync"/>:
    /// decode the AMQP receipt handle, look up the cached
    /// (session) receiver via
    /// <see cref="TryAcquireSettleReceiverAsync"/>, and call
    /// <see cref="ServiceBusReceiver.CompleteAsync"/>. Per-entry failures
    /// (stale handle, queue mismatch, lock-token cache miss, transport
    /// error) are surfaced as BatchResultErrorEntry items — the request
    /// only fails wholesale on parse / shape / queue-name errors.
    /// FIFO-aware: when the entry batch mixes session-ids the per-entry
    /// settle calls naturally route each to its own cached session
    /// receiver because <see cref="TryAcquireSettleReceiverAsync"/> keys
    /// on <c>(queueName, sessionId)</c>.
    /// </summary>
    internal static async Task DeleteMessageBatchAsync(
        HttpContext context, SqsParseResult parsed, IAmqpReceiverProvider receivers, CancellationToken ct)
    {
        var queueName = AmqpReceiveParameters.ExtractQueueName(parsed);
        if (queueName is null)
        {
            await AmqpReceiveParameters.WriteErrorAsync(context, parsed.Protocol,
                SqsErrorMapping.InvalidParameterValue("QueueUrl", "QueueUrl is required and must be a queue URL.")).ConfigureAwait(false);
            return;
        }
        if (!QueueName.IsValid(queueName))
        {
            await AmqpReceiveParameters.WriteErrorAsync(context, parsed.Protocol, SqsErrorMapping.QueueNameInvalid()).ConfigureAwait(false);
            return;
        }
        var entries = BatchAdminHandlers.ParseDeleteEntries(parsed);
        if (entries is null)
        {
            await AmqpReceiveParameters.WriteErrorAsync(context, parsed.Protocol,
                SqsErrorMapping.InvalidParameterValue("Entries", "Could not parse batch entries.")).ConfigureAwait(false);
            return;
        }
        var ids = new List<string>(entries.Count);
        foreach (var e in entries) ids.Add(e.Id);
        if (!BatchAdminHandlers.ValidateBatchShape(ids, out var shapeError))
        {
            await AmqpReceiveParameters.WriteErrorAsync(context, parsed.Protocol, shapeError!.Value).ConfigureAwait(false);
            return;
        }

        var ok = new List<BatchEntryOk>(entries.Count);
        var failed = new List<BatchEntryError>();
        // Deferred invalidation: collect (sessionId) keys whose settle
        // receiver hit a transport error during fan-out, then invalidate
        // once after Task.WhenAll. This avoids disposing a receiver under
        // sibling entries that may still hold pending AMQP requests on
        // it, while still ensuring a broken cached client doesn't linger
        // across subsequent batches. Empty string == non-session handle.
        var receiversToInvalidate = new HashSet<string>(StringComparer.Ordinal);
        await BatchAdminHandlers.ForEachBoundedAsync(entries, BatchAdminHandlers.MaxBatchConcurrency, async entry =>
        {
            await AmqpBatchSettleEntries.DeleteBatchEntryAsync(
                    entry, queueName, receivers, ok, failed, receiversToInvalidate, ct)
                .ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        foreach (var sessionId in receiversToInvalidate)
        {
            await InvalidateSettleReceiverAsync(
                receivers, queueName, sessionId.Length == 0 ? null : sessionId).ConfigureAwait(false);
        }

        var order = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < entries.Count; i++) order[entries[i].Id] = i;
        ok.Sort((a, b) => order[a.Id].CompareTo(order[b.Id]));
        failed.Sort((a, b) => order[a.Id].CompareTo(order[b.Id]));

        await SqsResponseWriter.WriteDeleteMessageBatchAsync(context, parsed.Protocol, ok, failed).ConfigureAwait(false);
    }

    /// <summary>
    /// AMQP-flavoured ChangeMessageVisibilityBatch. Each entry follows the
    /// singular CMV path: <c>VisibilityTimeout == 0</c> takes the Abandon
    /// shortcut on the cached (session) receiver; positive values RenewLock
    /// through the SB <c>$management</c> link (session-aware when the
    /// handle is v3). Per-entry failures are folded into the batch
    /// response. Note: the granted seconds may differ from the requested
    /// value because SB always extends by the queue-level LockDuration —
    /// the REST CMV path surfaces this via the
    /// <c>Aws2Azure-VisibilityClamped</c> response header on the singular
    /// call, but the batch shape has no per-entry place to carry it, so
    /// clamping is silent here (matches the SQS batch contract: the
    /// response body just lists successes by Id).
    /// </summary>
    internal static async Task ChangeMessageVisibilityBatchAsync(
        HttpContext context, SqsParseResult parsed, IAmqpReceiverProvider receivers, CancellationToken ct)
    {
        var queueName = AmqpReceiveParameters.ExtractQueueName(parsed);
        if (queueName is null)
        {
            await AmqpReceiveParameters.WriteErrorAsync(context, parsed.Protocol,
                SqsErrorMapping.InvalidParameterValue("QueueUrl", "QueueUrl is required and must be a queue URL.")).ConfigureAwait(false);
            return;
        }
        if (!QueueName.IsValid(queueName))
        {
            await AmqpReceiveParameters.WriteErrorAsync(context, parsed.Protocol, SqsErrorMapping.QueueNameInvalid()).ConfigureAwait(false);
            return;
        }
        var entries = BatchAdminHandlers.ParseChangeVisEntries(parsed);
        if (entries is null)
        {
            await AmqpReceiveParameters.WriteErrorAsync(context, parsed.Protocol,
                SqsErrorMapping.InvalidParameterValue("Entries", "Could not parse batch entries.")).ConfigureAwait(false);
            return;
        }
        var ids = new List<string>(entries.Count);
        foreach (var e in entries) ids.Add(e.Id);
        if (!BatchAdminHandlers.ValidateBatchShape(ids, out var shapeError))
        {
            await AmqpReceiveParameters.WriteErrorAsync(context, parsed.Protocol, shapeError!.Value).ConfigureAwait(false);
            return;
        }

        var ok = new List<BatchEntryOk>(entries.Count);
        var failed = new List<BatchEntryError>();
        // Deferred invalidation (see DeleteMessageBatchAsync above).
        var receiversToInvalidate = new HashSet<string>(StringComparer.Ordinal);
        var mgmtFailed = 0;
        await BatchAdminHandlers.ForEachBoundedAsync(entries, BatchAdminHandlers.MaxBatchConcurrency, async entry =>
        {
            await AmqpBatchSettleEntries.ChangeVisibilityBatchEntryAsync(
                    entry,
                    queueName,
                    receivers,
                    ok,
                    failed,
                    receiversToInvalidate,
                    () => Interlocked.Exchange(ref mgmtFailed, 1),
                    ct)
                .ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        foreach (var sessionId in receiversToInvalidate)
        {
            await InvalidateSettleReceiverAsync(
                receivers, queueName, sessionId.Length == 0 ? null : sessionId).ConfigureAwait(false);
        }
        if (Volatile.Read(ref mgmtFailed) == 1)
        {
            await receivers.InvalidateManagementClientAsync(queueName).ConfigureAwait(false);
        }

        var order = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < entries.Count; i++) order[entries[i].Id] = i;
        ok.Sort((a, b) => order[a.Id].CompareTo(order[b.Id]));
        failed.Sort((a, b) => order[a.Id].CompareTo(order[b.Id]));

        await SqsResponseWriter.WriteChangeMessageVisibilityBatchAsync(context, parsed.Protocol, ok, failed).ConfigureAwait(false);
    }

    // --- settle helpers (FIFO-aware) -----------------------------------

    /// <summary>
    /// Picks the receiver that DeleteMessage / CMV(0) should settle the
    /// lock token on. v3 receipt handles carry a session-id and route
    /// to the <em>existing</em> cached session-bound receiver in the
    /// pool — returns <c>null</c> when no slot exists. The settle paths
    /// must <b>not</b> open a fresh session lock here: acquiring a new
    /// lock just to fail the lock-token lookup would starve the
    /// MessageGroupId until the session lock expires. v2 handles fall
    /// through to <see cref="IAmqpReceiverProvider.GetReceiverAsync"/>
    /// as before (the non-session receiver is allowed to open on
    /// demand).
    /// </summary>
    internal static async Task<ServiceBusReceiver?> TryAcquireSettleReceiverAsync(
        IAmqpReceiverProvider receivers, string queueName, string? sessionId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(sessionId))
            return await receivers.GetReceiverAsync(queueName, ct).ConfigureAwait(false);
        return receivers.TryGetExistingSessionReceiver(queueName, sessionId);
    }

    /// <summary>
    /// Mirror of <see cref="TryAcquireSettleReceiverAsync"/>: invalidates the
    /// pool slot that owns the receiver we just failed against, so a
    /// torn-down session-bound link is replaced on the next attempt
    /// instead of evicting the unrelated non-session receiver.
    /// </summary>
    private static Task InvalidateSettleReceiverAsync(
        IAmqpReceiverProvider receivers, string queueName, string? sessionId)
        => string.IsNullOrEmpty(sessionId)
            ? receivers.InvalidateAsync(queueName, closeConnection: false)
            : receivers.InvalidateSessionReceiverAsync(queueName, sessionId);

}
