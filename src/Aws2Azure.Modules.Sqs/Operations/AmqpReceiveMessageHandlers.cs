using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Aws2Azure.Amqp.Connection;
using Aws2Azure.Amqp.Framing;
using Aws2Azure.Amqp.ServiceBus;
using Aws2Azure.Modules.Sqs.Errors;
using Aws2Azure.Modules.Sqs.Internal;
using Aws2Azure.Modules.Sqs.WireProtocol;
using Aws2Azure.Modules.Sqs.Xml;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.Sqs.Operations;

/// <summary>
/// AMQP-backed dispatcher for the receive/delete/visibility ops. Sits
/// alongside <see cref="ReceiveMessageHandlers"/> (REST) and is
/// selected per-queue by
/// <see cref="Aws2Azure.Core.Configuration.SqsTransportResolver"/>.
///
/// <para>What this slice ships (8b.4c):</para>
/// <list type="bullet">
///   <item><c>ReceiveMessage</c> over native AMQP via
///   <see cref="ServiceBusReceiver.ReceiveBatchAsync"/>. The receiver's
///   in-flight cache (slice 8b.4b) keys each delivery by its
///   lock-token GUID so the stateless HTTP <c>DeleteMessage</c> can
///   settle it later.</item>
///   <item><c>DeleteMessage</c> routes by lock-token via
///   <see cref="ServiceBusReceiver.CompleteAsync(Guid, CancellationToken)"/>.
///   Cache miss → SQS <c>ReceiptHandleIsInvalid</c>.</item>
///   <item><c>ChangeMessageVisibility</c> round-trips through the SB
///   <c>$management</c> link: <c>com.microsoft:renew-lock</c> for
///   non-session deliveries and <c>com.microsoft:renew-session-lock</c>
///   for FIFO deliveries (v3 receipt handle carries the session-id).
///   SB always extends by the queue-level <c>LockDuration</c> — when
///   the granted seconds differ from the requested
///   <c>VisibilityTimeout</c> the response carries an
///   <c>Aws2Azure-VisibilityClamped</c> diagnostic header.
///   <c>VisibilityTimeout=0</c> short-circuits to AMQP Abandon on the
///   receiver link (no $management round-trip).</item>
/// </list>
///
/// <para>Dead-letter surfacing: when SB delivers a message from a
/// <c>/$DeadLetterQueue</c> subqueue, the annotation
/// <c>x-opt-deadletter-source</c> carries the originating queue name and
/// the application-properties <c>DeadLetterReason</c> /
/// <c>DeadLetterErrorDescription</c> carry the reason it was dead-lettered.
/// The proxy surfaces all three as system attributes:
/// <c>DeadLetterQueueSourceArn</c> (synthesised ARN using the placeholder
/// account id),
/// <c>Aws2Azure-DeadLetterReason</c>,
/// <c>Aws2Azure-DeadLetterErrorDescription</c>. They are emitted only
/// when the inbound message actually came from a DLQ; non-DLQ messages
/// never see these attributes. Subject to the same
/// <c>AttributeNames</c> filter as the standard system attributes.</para>
///
/// <para>Out of scope: long-polling (slice 8c).</para>
/// </summary>
internal static class AmqpReceiveMessageHandlers
{
    public const int MaxMessages = 10;
    public const int MaxVisibilityTimeoutSeconds = 43_200;
    public const int MaxWaitTimeSeconds = 20;

    /// <summary>Default short-poll wait when the caller didn't supply WaitTimeSeconds.</summary>
    private static readonly TimeSpan DefaultReceiveTimeout = TimeSpan.FromMilliseconds(50);

    public static Task HandleAsync(
        HttpContext context,
        SqsParseResult parsed,
        IAmqpReceiverProvider receivers,
        CancellationToken ct) =>
        parsed.Operation switch
        {
            SqsOperation.ReceiveMessage              => ReceiveMessageAsync(context, parsed, receivers, ct),
            SqsOperation.DeleteMessage               => DeleteMessageAsync(context, parsed, receivers, ct),
            SqsOperation.ChangeMessageVisibility     => ChangeMessageVisibilityAsync(context, parsed, receivers, ct),
            SqsOperation.DeleteMessageBatch          => DeleteMessageBatchAsync(context, parsed, receivers, ct),
            SqsOperation.ChangeMessageVisibilityBatch => ChangeMessageVisibilityBatchAsync(context, parsed, receivers, ct),
            _                                        => WriteErrorAsync(context, parsed.Protocol,
                                                           SqsErrorMapping.NotImplemented(parsed.Operation)),
        };

    // --- ReceiveMessage ------------------------------------------------

    private static async Task ReceiveMessageAsync(
        HttpContext context, SqsParseResult parsed, IAmqpReceiverProvider receivers, CancellationToken ct)
    {
        var queueName = ExtractQueueName(parsed);
        if (queueName is null)
        {
            await WriteErrorAsync(context, parsed.Protocol,
                SqsErrorMapping.InvalidParameterValue("QueueUrl", "QueueUrl is required and must be a queue URL.")).ConfigureAwait(false);
            return;
        }

        if (!TryParseBoundedInt(parsed, "MaxNumberOfMessages", min: 1, max: MaxMessages, defaultValue: 1, out var maxMessages))
        {
            await WriteErrorAsync(context, parsed.Protocol, SqsErrorMapping.ReceiveLimitInvalid()).ConfigureAwait(false);
            return;
        }
        if (!TryParseBoundedInt(parsed, "WaitTimeSeconds", min: 0, max: MaxWaitTimeSeconds, defaultValue: 0, out var waitSeconds))
        {
            await WriteErrorAsync(context, parsed.Protocol, SqsErrorMapping.ReceiveWaitTimeInvalid()).ConfigureAwait(false);
            return;
        }
        // VisibilityTimeout is validated but not honoured — SB's AMQP lock
        // duration is the queue-level setting. Surface that divergence in
        // the gap doc; callers asking for a different visibility get the
        // queue-level value instead.
        if (!TryParseBoundedInt(parsed, "VisibilityTimeout", min: 0, max: MaxVisibilityTimeoutSeconds, defaultValue: -1, out _))
        {
            await WriteErrorAsync(context, parsed.Protocol, SqsErrorMapping.VisibilityTimeoutInvalid()).ConfigureAwait(false);
            return;
        }

        ServiceBusReceiver receiver;
        var isFifo = QueueName.IsFifo(queueName);
        try
        {
            // FIFO queues map to SB session-aware receive. The client
            // doesn't know the MessageGroupId in advance, so the broker
            // picks any available session and we adopt the resulting
            // receiver into the pool keyed by the resolved session-id
            // (slice 7c.3a). Subsequent settle requests with a v3
            // receipt handle route back via GetSessionReceiverAsync.
            receiver = isFifo
                ? await receivers.AcquireBrokerAssignedSessionReceiverAsync(queueName, ct).ConfigureAwait(false)
                : await receivers.GetReceiverAsync(queueName, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await WriteErrorAsync(context, parsed.Protocol,
                MapAmqpException(ex, "ReceiveMessage")).ConfigureAwait(false);
            return;
        }

        IReadOnlyList<ServiceBusReceivedMessage> batch;
        try
        {
            var timeout = waitSeconds > 0 ? TimeSpan.FromSeconds(waitSeconds) : DefaultReceiveTimeout;
            batch = await receiver.ReceiveBatchAsync(maxMessages, timeout, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Link- or connection-level failure: drop the receiver so the
            // next call rebuilds. Don't tear down the connection — a stale
            // link can be detached without affecting peers under the same
            // SAS key. For FIFO the cached entry is keyed by session-id
            // so we evict the session-receiver slot specifically.
            if (isFifo && receiver.SessionId is { } sessionId)
                await receivers.InvalidateSessionReceiverAsync(queueName, sessionId).ConfigureAwait(false);
            else
                await receivers.InvalidateAsync(queueName, closeConnection: false).ConfigureAwait(false);
            await WriteErrorAsync(context, parsed.Protocol,
                MapAmqpException(ex, "ReceiveMessage")).ConfigureAwait(false);
            return;
        }

        var systemAttrFilter = ParseAttributeNames(parsed, "AttributeName");
        var messageAttrFilter = ParseAttributeNames(parsed, "MessageAttributeName");
        var collected = new List<ReceivedSqsMessage>(batch.Count);
        foreach (var msg in batch)
        {
            var built = BuildReceivedMessage(queueName, msg, receiver.SessionId, systemAttrFilter, messageAttrFilter);
            if (built is null)
            {
                // No lock-token (sender-settled / non-16-byte tag) → we
                // can't mint a receipt-handle that DeleteMessage can settle.
                // Abandon (modified) so SB redelivers and another consumer
                // can try, rather than handing the client a useless handle.
                if (msg.LockToken is null)
                {
                    try { await receiver.AbandonAsync(msg, ct).ConfigureAwait(false); }
                    catch { /* best-effort */ }
                }
                continue;
            }
            collected.Add(built);
        }

        await SqsResponseWriter.WriteReceiveMessageAsync(context, parsed.Protocol, collected).ConfigureAwait(false);
    }

    // --- DeleteMessage -------------------------------------------------

    private static async Task DeleteMessageAsync(
        HttpContext context, SqsParseResult parsed, IAmqpReceiverProvider receivers, CancellationToken ct)
    {
        var queueName = ExtractQueueName(parsed);
        if (queueName is null)
        {
            await WriteErrorAsync(context, parsed.Protocol,
                SqsErrorMapping.InvalidParameterValue("QueueUrl", "QueueUrl is required and must be a queue URL.")).ConfigureAwait(false);
            return;
        }
        if (!TryGetParam(parsed, "ReceiptHandle", out var handle))
        {
            await WriteErrorAsync(context, parsed.Protocol,
                SqsErrorMapping.MissingParameter("ReceiptHandle")).ConfigureAwait(false);
            return;
        }
        if (!AmqpReceiptHandle.TryDecode(handle, out var decoded))
        {
            await WriteErrorAsync(context, parsed.Protocol, SqsErrorMapping.ReceiptHandleInvalid()).ConfigureAwait(false);
            return;
        }
        // The handle carries the queue it was minted against; if the caller
        // points DeleteMessage at a different queue we treat the handle as
        // invalid rather than fanning out across receivers.
        if (!string.Equals(decoded.QueueName, queueName, StringComparison.OrdinalIgnoreCase))
        {
            await WriteErrorAsync(context, parsed.Protocol, SqsErrorMapping.ReceiptHandleInvalid()).ConfigureAwait(false);
            return;
        }

        ServiceBusReceiver? receiver;
        try
        {
            receiver = await TryAcquireSettleReceiverAsync(receivers, queueName, decoded.SessionId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await WriteErrorAsync(context, parsed.Protocol,
                MapAmqpException(ex, "DeleteMessage")).ConfigureAwait(false);
            return;
        }
        if (receiver is null)
        {
            // FIFO + no cached session receiver for this session-id =
            // the lock that minted this handle is gone (session expired,
            // receiver invalidated, or proxy restarted). Surface as a
            // stale handle without acquiring a fresh session lock that
            // would block the MessageGroupId.
            await WriteErrorAsync(context, parsed.Protocol, SqsErrorMapping.ReceiptHandleInvalid()).ConfigureAwait(false);
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
            await WriteErrorAsync(context, parsed.Protocol,
                MapAmqpException(ex, "DeleteMessage")).ConfigureAwait(false);
            return;
        }
        if (!settled)
        {
            // Cache miss = already settled, lock expired, sender-settled
            // at receive time, or originated from a torn-down receiver
            // instance. SQS surfaces this as an invalid receipt handle.
            await WriteErrorAsync(context, parsed.Protocol, SqsErrorMapping.ReceiptHandleInvalid()).ConfigureAwait(false);
            return;
        }

        await SqsResponseWriter.WriteDeleteMessageAsync(context, parsed.Protocol).ConfigureAwait(false);
    }

    // --- ChangeMessageVisibility ---------------------------------------

    private static async Task ChangeMessageVisibilityAsync(
        HttpContext context, SqsParseResult parsed, IAmqpReceiverProvider receivers, CancellationToken ct)
    {
        var queueName = ExtractQueueName(parsed);
        if (queueName is null)
        {
            await WriteErrorAsync(context, parsed.Protocol,
                SqsErrorMapping.InvalidParameterValue("QueueUrl", "QueueUrl is required and must be a queue URL.")).ConfigureAwait(false);
            return;
        }
        if (!TryGetParam(parsed, "ReceiptHandle", out var handle))
        {
            await WriteErrorAsync(context, parsed.Protocol,
                SqsErrorMapping.MissingParameter("ReceiptHandle")).ConfigureAwait(false);
            return;
        }
        if (!AmqpReceiptHandle.TryDecode(handle, out var decoded))
        {
            await WriteErrorAsync(context, parsed.Protocol, SqsErrorMapping.ReceiptHandleInvalid()).ConfigureAwait(false);
            return;
        }
        if (!string.Equals(decoded.QueueName, queueName, StringComparison.OrdinalIgnoreCase))
        {
            await WriteErrorAsync(context, parsed.Protocol, SqsErrorMapping.ReceiptHandleInvalid()).ConfigureAwait(false);
            return;
        }
        if (!TryParseBoundedInt(parsed, "VisibilityTimeout", min: 0, max: MaxVisibilityTimeoutSeconds, defaultValue: -1, out var visibility)
            || visibility < 0)
        {
            await WriteErrorAsync(context, parsed.Protocol, SqsErrorMapping.VisibilityTimeoutInvalid()).ConfigureAwait(false);
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
                await WriteErrorAsync(context, parsed.Protocol,
                    MapAmqpException(ex, "ChangeMessageVisibility")).ConfigureAwait(false);
                return;
            }
            if (receiver is null)
            {
                // Same stale-handle semantics as DeleteMessage above:
                // surface ReceiptHandleIsInvalid rather than opening a
                // fresh session lock.
                await WriteErrorAsync(context, parsed.Protocol, SqsErrorMapping.ReceiptHandleInvalid()).ConfigureAwait(false);
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
                await WriteErrorAsync(context, parsed.Protocol,
                    MapAmqpException(ex, "ChangeMessageVisibility")).ConfigureAwait(false);
                return;
            }
            if (!abandoned)
            {
                await WriteErrorAsync(context, parsed.Protocol, SqsErrorMapping.MessageNotInflight()).ConfigureAwait(false);
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
            await WriteErrorAsync(context, parsed.Protocol,
                MapAmqpException(ex, "ChangeMessageVisibility")).ConfigureAwait(false);
            return;
        }

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
            await WriteErrorAsync(context, parsed.Protocol,
                MapManagementException(ex, "ChangeMessageVisibility")).ConfigureAwait(false);
            return;
        }
        catch (Exception ex)
        {
            await receivers.InvalidateManagementClientAsync(queueName).ConfigureAwait(false);
            await WriteErrorAsync(context, parsed.Protocol,
                MapAmqpException(ex, "ChangeMessageVisibility")).ConfigureAwait(false);
            return;
        }

        var grantedSeconds = Math.Max(0, (int)Math.Round((lockedUntil - DateTimeOffset.UtcNow).TotalSeconds));
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
    private static async Task DeleteMessageBatchAsync(
        HttpContext context, SqsParseResult parsed, IAmqpReceiverProvider receivers, CancellationToken ct)
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
        var entries = BatchAdminHandlers.ParseDeleteEntries(parsed);
        if (entries is null)
        {
            await WriteErrorAsync(context, parsed.Protocol,
                SqsErrorMapping.InvalidParameterValue("Entries", "Could not parse batch entries.")).ConfigureAwait(false);
            return;
        }
        var ids = new List<string>(entries.Count);
        foreach (var e in entries) ids.Add(e.Id);
        if (!BatchAdminHandlers.ValidateBatchShape(ids, out var shapeError))
        {
            await WriteErrorAsync(context, parsed.Protocol, shapeError!.Value).ConfigureAwait(false);
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
            if (!AmqpReceiptHandle.TryDecode(entry.ReceiptHandle, out var decoded) ||
                !string.Equals(decoded.QueueName, queueName, StringComparison.OrdinalIgnoreCase))
            {
                var m = SqsErrorMapping.ReceiptHandleInvalid();
                lock (failed) failed.Add(new BatchEntryError(entry.Id, m.Code, m.Message,
                    SenderFault: m.FaultType == SqsErrorResponse.FaultType.Sender));
                return;
            }

            ServiceBusReceiver? receiver;
            try
            {
                receiver = await TryAcquireSettleReceiverAsync(receivers, queueName, decoded.SessionId, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var m = MapAmqpException(ex, "DeleteMessageBatch");
                lock (failed) failed.Add(new BatchEntryError(entry.Id, m.Code, m.Message,
                    SenderFault: m.FaultType == SqsErrorResponse.FaultType.Sender));
                return;
            }
            if (receiver is null)
            {
                var m = SqsErrorMapping.ReceiptHandleInvalid();
                lock (failed) failed.Add(new BatchEntryError(entry.Id, m.Code, m.Message,
                    SenderFault: m.FaultType == SqsErrorResponse.FaultType.Sender));
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
                // Defer invalidation until after sibling entries finish
                // (see receiversToInvalidate above): disposing the
                // shared session receiver mid-batch can hang siblings.
                lock (receiversToInvalidate) receiversToInvalidate.Add(decoded.SessionId ?? string.Empty);
                var m = MapAmqpException(ex, "DeleteMessageBatch");
                lock (failed) failed.Add(new BatchEntryError(entry.Id, m.Code, m.Message,
                    SenderFault: m.FaultType == SqsErrorResponse.FaultType.Sender));
                return;
            }
            if (!settled)
            {
                var m = SqsErrorMapping.ReceiptHandleInvalid();
                lock (failed) failed.Add(new BatchEntryError(entry.Id, m.Code, m.Message,
                    SenderFault: m.FaultType == SqsErrorResponse.FaultType.Sender));
                return;
            }
            lock (ok) ok.Add(new BatchEntryOk(entry.Id));
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
    private static async Task ChangeMessageVisibilityBatchAsync(
        HttpContext context, SqsParseResult parsed, IAmqpReceiverProvider receivers, CancellationToken ct)
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
        var entries = BatchAdminHandlers.ParseChangeVisEntries(parsed);
        if (entries is null)
        {
            await WriteErrorAsync(context, parsed.Protocol,
                SqsErrorMapping.InvalidParameterValue("Entries", "Could not parse batch entries.")).ConfigureAwait(false);
            return;
        }
        var ids = new List<string>(entries.Count);
        foreach (var e in entries) ids.Add(e.Id);
        if (!BatchAdminHandlers.ValidateBatchShape(ids, out var shapeError))
        {
            await WriteErrorAsync(context, parsed.Protocol, shapeError!.Value).ConfigureAwait(false);
            return;
        }

        var ok = new List<BatchEntryOk>(entries.Count);
        var failed = new List<BatchEntryError>();
        // Deferred invalidation (see DeleteMessageBatchAsync above).
        var receiversToInvalidate = new HashSet<string>(StringComparer.Ordinal);
        var mgmtFailed = 0;
        await BatchAdminHandlers.ForEachBoundedAsync(entries, BatchAdminHandlers.MaxBatchConcurrency, async entry =>
        {
            if (entry.VisibilityTimeout < 0 || entry.VisibilityTimeout > MaxVisibilityTimeoutSeconds)
            {
                var m = SqsErrorMapping.VisibilityTimeoutInvalid();
                lock (failed) failed.Add(new BatchEntryError(entry.Id, m.Code, m.Message,
                    SenderFault: m.FaultType == SqsErrorResponse.FaultType.Sender));
                return;
            }
            if (!AmqpReceiptHandle.TryDecode(entry.ReceiptHandle, out var decoded) ||
                !string.Equals(decoded.QueueName, queueName, StringComparison.OrdinalIgnoreCase))
            {
                var m = SqsErrorMapping.ReceiptHandleInvalid();
                lock (failed) failed.Add(new BatchEntryError(entry.Id, m.Code, m.Message,
                    SenderFault: m.FaultType == SqsErrorResponse.FaultType.Sender));
                return;
            }

            if (entry.VisibilityTimeout == 0)
            {
                ServiceBusReceiver? receiver;
                try
                {
                    receiver = await TryAcquireSettleReceiverAsync(receivers, queueName, decoded.SessionId, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    var m = MapAmqpException(ex, "ChangeMessageVisibilityBatch");
                    lock (failed) failed.Add(new BatchEntryError(entry.Id, m.Code, m.Message,
                        SenderFault: m.FaultType == SqsErrorResponse.FaultType.Sender));
                    return;
                }
                if (receiver is null)
                {
                    var m = SqsErrorMapping.ReceiptHandleInvalid();
                    lock (failed) failed.Add(new BatchEntryError(entry.Id, m.Code, m.Message,
                        SenderFault: m.FaultType == SqsErrorResponse.FaultType.Sender));
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
                    // Defer invalidation (see DeleteMessageBatchAsync).
                    lock (receiversToInvalidate) receiversToInvalidate.Add(decoded.SessionId ?? string.Empty);
                    var m = MapAmqpException(ex, "ChangeMessageVisibilityBatch");
                    lock (failed) failed.Add(new BatchEntryError(entry.Id, m.Code, m.Message,
                        SenderFault: m.FaultType == SqsErrorResponse.FaultType.Sender));
                    return;
                }
                if (!abandoned)
                {
                    var m = SqsErrorMapping.MessageNotInflight();
                    lock (failed) failed.Add(new BatchEntryError(entry.Id, m.Code, m.Message,
                        SenderFault: m.FaultType == SqsErrorResponse.FaultType.Sender));
                    return;
                }
                lock (ok) ok.Add(new BatchEntryOk(entry.Id));
                return;
            }

            // visibility > 0 — RenewLock via $management (session-aware
            // when the handle carries a session-id).
            ServiceBusManagementClient mgmt;
            try
            {
                mgmt = await receivers.GetManagementClientAsync(queueName, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var m = MapAmqpException(ex, "ChangeMessageVisibilityBatch");
                lock (failed) failed.Add(new BatchEntryError(entry.Id, m.Code, m.Message,
                    SenderFault: m.FaultType == SqsErrorResponse.FaultType.Sender));
                return;
            }

            try
            {
                _ = string.IsNullOrEmpty(decoded.SessionId)
                    ? await mgmt.RenewLockAsync(decoded.LockToken, ct).ConfigureAwait(false)
                    : await mgmt.RenewSessionLockAsync(decoded.SessionId, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (ServiceBusManagementException ex)
            {
                var m = MapManagementException(ex, "ChangeMessageVisibilityBatch");
                lock (failed) failed.Add(new BatchEntryError(entry.Id, m.Code, m.Message,
                    SenderFault: m.FaultType == SqsErrorResponse.FaultType.Sender));
                return;
            }
            catch (Exception ex)
            {
                // Defer invalidation: sibling entries may still have
                // requests pending on the shared mgmt client. We flag
                // it here and invalidate once after Task.WhenAll
                // (see mgmtFailed handling below the fan-out).
                Interlocked.Exchange(ref mgmtFailed, 1);
                var m = MapAmqpException(ex, "ChangeMessageVisibilityBatch");
                lock (failed) failed.Add(new BatchEntryError(entry.Id, m.Code, m.Message,
                    SenderFault: m.FaultType == SqsErrorResponse.FaultType.Sender));
                return;
            }
            lock (ok) ok.Add(new BatchEntryOk(entry.Id));
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
    private static async Task<ServiceBusReceiver?> TryAcquireSettleReceiverAsync(
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

    // --- SB AMQP message → SQS message translation ---------------------

    internal static ReceivedSqsMessage? BuildReceivedMessage(
        string queueName, ServiceBusReceivedMessage msg, string? receiverSessionId, HashSet<string>? systemAttrFilter,
        HashSet<string>? messageAttrFilter = null)
    {
        if (msg.LockToken is not { } lockToken)
        {
            // Tag wasn't a 16-byte GUID — we have nothing to settle by
            // later. Caller will Abandon so SB redelivers.
            return null;
        }

        // SB-style message-id from properties.message-id; fall back to
        // the lock-token's textual form so the client always sees
        // something stable. Real Azure SB always sets message-id on
        // peer-sent messages; this fallback is only for fakes.
        var messageId = msg.MessageId;
        if (string.IsNullOrEmpty(messageId))
        {
            messageId = lockToken.ToString("D", CultureInfo.InvariantCulture);
        }

        // Body: SB AMQP messages carry a single data section.
        // ServiceBusReceivedMessage.Body materialises that, falling back
        // to an empty span when the body is application/value (rare for
        // SQS-emulating producers).
        var bodyBytes = msg.Body.ToArray();
        var bodyText = bodyBytes.Length == 0 ? string.Empty : Encoding.UTF8.GetString(bodyBytes);
        var md5OfBody = SqsMessageMd5.OfBody(bodyBytes);

        var annotations = msg.Annotations;
        var lockedUntil = annotations?.LockedUntil ?? DateTimeOffset.UtcNow.AddSeconds(30);
        var enqueuedTimeMillis = annotations?.EnqueuedTime?.ToUnixTimeMilliseconds();
        var sequenceNumber = annotations?.SequenceNumber?.ToString(CultureInfo.InvariantCulture);
        // SB AMQP delivery-count counts redeliveries (zero on first); SQS
        // ApproximateReceiveCount counts the current receive — add one.
        var deliveryCount = msg.DeliveryCount.HasValue ? (int)(msg.DeliveryCount.Value + 1) : 1;

        // DLQ surfacing: when the receiver is bound to an SB DLQ subqueue
        // (path ends with /$DeadLetterQueue), SB stamps the originating
        // queue name on `x-opt-deadletter-source` (annotation) and copies
        // the optional reason / description onto the dead-lettered
        // message's application-properties (DeadLetterReason /
        // DeadLetterErrorDescription). Surface them as system attributes
        // so AWS SDK clients can distinguish DLQ messages and read why
        // each was dead-lettered.
        var deadLetterSource = annotations?.DeadLetterSource;
        string? deadLetterReason = null;
        string? deadLetterDescription = null;
        if (!string.IsNullOrEmpty(deadLetterSource))
        {
            var appProps = msg.Message.ApplicationProperties;
            if (appProps is not null)
            {
                if (appProps.TryGetValue("DeadLetterReason", out var rv) && rv is string rs)
                    deadLetterReason = rs;
                if (appProps.TryGetValue("DeadLetterErrorDescription", out var dv) && dv is string ds)
                    deadLetterDescription = ds;
            }
        }

        // MessageGroupId: SB stamps the SQS MessageGroupId onto both
        // properties.group-id (slice 7c.2 parsed it) and the SB
        // session-id. Prefer the per-message value (it survives across
        // session-receiver rebinds) but fall back to the receiver's
        // bound session-id when the producer skipped the explicit
        // properties section.
        var messageGroupId = msg.Message.Properties.GroupId;
        if (string.IsNullOrEmpty(messageGroupId))
            messageGroupId = receiverSessionId;

        var systemAttrs = BuildSystemAttributes(
            enqueuedTimeMillis, deliveryCount, sequenceNumber, messageGroupId,
            deadLetterSource, deadLetterReason, deadLetterDescription, systemAttrFilter);

        // Application-properties → SQS MessageAttributes round-trip.
        // The sender (AmqpSendMessageHandlers / AmqpSendMessageBatchHandlers)
        // serialises each SQS attribute as an ApplicationProperty[name] +
        // a side-channel registry on ApplicationProperty[Aws2Azure-AttrTypes]
        // that records "name=DataType,…" (Binary values are base64-encoded
        // into the property value). Reconstruct here so the AMQP receive
        // path matches the REST round-trip and emits MD5OfMessageAttributes.
        var (messageAttrs, md5OfMessageAttrs) = BuildMessageAttributes(msg.Message.ApplicationProperties, messageAttrFilter);

        // FIFO receives stamp the bound session-id into the handle so
        // DeleteMessage / ChangeMessageVisibility can route back to the
        // same session receiver in the pool (slice 7c.3d). Non-FIFO
        // queues pass sessionId=null and the encoder emits v2.
        var handle = AmqpReceiptHandle.Encode(queueName, lockToken, lockedUntil, receiverSessionId);
        return new ReceivedSqsMessage(
            MessageId: messageId!,
            ReceiptHandle: handle,
            MD5OfBody: md5OfBody,
            Body: bodyText,
            MD5OfMessageAttributes: md5OfMessageAttrs,
            Attributes: systemAttrs,
            MessageAttributes: messageAttrs);
    }

    /// <summary>
    /// Mirrors <see cref="ReceiveMessageHandlers"/>' REST-path
    /// reconstruction: walks the AttrTypes registry, rebuilds the
    /// SQS-shaped <see cref="ReceivedSqsAttribute"/> map and computes
    /// MD5OfMessageAttributes via the shared helper. Returns
    /// <c>(null, null)</c> when no filter was supplied (clients that
    /// don't ask for <c>MessageAttributeName</c> get nothing on the
    /// REST path either).
    /// </summary>
    internal static (IReadOnlyDictionary<string, ReceivedSqsAttribute>?, string?) BuildMessageAttributes(
        IReadOnlyDictionary<string, object?>? appProps, HashSet<string>? filter)
    {
        if (filter is null || filter.Count == 0) return (null, null);
        if (appProps is null || appProps.Count == 0) return (null, null);
        if (!appProps.TryGetValue(SendMessageHandlers.AttrTypesHeader, out var registryObj)
            || registryObj is not string registryRaw
            || string.IsNullOrEmpty(registryRaw))
        {
            return (null, null);
        }

        var typeRegistry = ParseAttrTypeRegistry(registryRaw);
        if (typeRegistry.Count == 0) return (null, null);

        var includeAll = filter.Contains("All", StringComparer.OrdinalIgnoreCase);
        var attrs = new SortedDictionary<string, ReceivedSqsAttribute>(StringComparer.Ordinal);
        var md5Source = new SortedDictionary<string, SqsMessageAttribute>(StringComparer.Ordinal);

        foreach (var (name, dataType) in typeRegistry)
        {
            if (!includeAll && !filter.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            if (!appProps.TryGetValue(name, out var rawValue) || rawValue is not string raw) continue;

            var baseType = dataType;
            var dot = baseType.IndexOf('.', StringComparison.Ordinal);
            var headBase = dot < 0 ? baseType : baseType[..dot];

            ReceivedSqsAttribute received;
            SqsMessageAttribute forMd5;
            if (headBase == "Binary")
            {
                received = new ReceivedSqsAttribute(dataType, StringValue: null, BinaryValueBase64: raw);
                byte[] bytes;
                try { bytes = Convert.FromBase64String(raw); }
                catch (FormatException) { bytes = Array.Empty<byte>(); }
                forMd5 = new SqsMessageAttribute { DataType = dataType, BinaryValue = bytes };
            }
            else
            {
                received = new ReceivedSqsAttribute(dataType, StringValue: raw, BinaryValueBase64: null);
                forMd5 = new SqsMessageAttribute { DataType = dataType, StringValue = raw };
            }
            attrs[name] = received;
            md5Source[name] = forMd5;
        }
        if (attrs.Count == 0) return (null, null);
        return (attrs, SqsMessageMd5.OfAttributes(md5Source));
    }

    private static Dictionary<string, string> ParseAttrTypeRegistry(string raw)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            var name = pair[..eq];
            var type = pair[(eq + 1)..];
            if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(type))
            {
                dict[name] = type;
            }
        }
        return dict;
    }

    private static IReadOnlyDictionary<string, string>? BuildSystemAttributes(
        long? enqueuedTimeMillis, int deliveryCount, string? sequenceNumber,
        string? messageGroupId, string? deadLetterSource, string? deadLetterReason,
        string? deadLetterDescription, HashSet<string>? filter)
    {
        if (filter is null || filter.Count == 0) return null;

        var include = filter.Contains("All", StringComparer.OrdinalIgnoreCase);
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        void Add(string name, string value)
        {
            if (include || filter.Contains(name, StringComparer.OrdinalIgnoreCase)) dict[name] = value;
        }
        if (enqueuedTimeMillis.HasValue)
            Add("SentTimestamp", enqueuedTimeMillis.Value.ToString(CultureInfo.InvariantCulture));
        Add("ApproximateReceiveCount", deliveryCount.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrEmpty(sequenceNumber))
            Add("SequenceNumber", sequenceNumber!);
        if (!string.IsNullOrEmpty(messageGroupId))
            Add("MessageGroupId", messageGroupId!);
        if (!string.IsNullOrEmpty(deadLetterSource))
        {
            // DeadLetterQueueSourceArn is the SQS-canonical surface for
            // "this message came from a DLQ". The proxy doesn't model AWS
            // accounts/regions; synthesise a placeholder ARN consistent
            // with QueueUrlBuilder.PlaceholderAccountId and the us-east-1
            // region clients see in our virtual-host matrix.
            Add("DeadLetterQueueSourceArn",
                "arn:aws:sqs:us-east-1:" + QueueUrlBuilder.PlaceholderAccountId + ":" + deadLetterSource!);
            // Reason/Description have no AWS-standard counterpart; expose
            // under Aws2Azure-prefixed names so apps that know to look
            // for them get full SB dead-letter context without colliding
            // with future AWS attribute additions.
            if (!string.IsNullOrEmpty(deadLetterReason))
                Add("Aws2Azure-DeadLetterReason", deadLetterReason!);
            if (!string.IsNullOrEmpty(deadLetterDescription))
                Add("Aws2Azure-DeadLetterErrorDescription", deadLetterDescription!);
        }
        return dict.Count == 0 ? null : dict;
    }

    // --- Parameter parsing helpers -------------------------------------

    private static HashSet<string>? ParseAttributeNames(SqsParseResult parsed, string prefix)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        var dotPrefix = prefix + ".";
        foreach (var kv in parsed.Parameters)
        {
            if (kv.Key.StartsWith(dotPrefix, StringComparison.Ordinal) && !string.IsNullOrEmpty(kv.Value))
                set.Add(kv.Value);
        }
        if (parsed.Protocol == SqsWireProtocol.AwsJson && !string.IsNullOrEmpty(parsed.JsonBody))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(parsed.JsonBody);
                var pluralName = prefix + "s";
                if (doc.RootElement.TryGetProperty(pluralName, out var arr) && arr.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var v in arr.EnumerateArray())
                    {
                        if (v.ValueKind == System.Text.Json.JsonValueKind.String)
                        {
                            var s = v.GetString();
                            if (!string.IsNullOrEmpty(s)) set.Add(s);
                        }
                    }
                }
            }
            catch (System.Text.Json.JsonException) { /* protocol parser already validated */ }
        }
        return set.Count == 0 ? null : set;
    }

    private static bool TryParseBoundedInt(
        SqsParseResult parsed, string name, int min, int max, int defaultValue, out int value)
    {
        if (!parsed.Parameters.TryGetValue(name, out var raw) || string.IsNullOrEmpty(raw))
        {
            value = defaultValue;
            return true;
        }
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) ||
            value < min || value > max)
        {
            value = defaultValue;
            return false;
        }
        return true;
    }

    private static string? ExtractQueueName(SqsParseResult parsed) =>
        parsed.Parameters.TryGetValue("QueueUrl", out var url) ? QueueUrlBuilder.ExtractQueueName(url) : null;

    private static bool TryGetParam(SqsParseResult parsed, string key, out string value)
    {
        if (parsed.Parameters.TryGetValue(key, out var v) && v is not null)
        {
            value = v;
            return true;
        }
        value = string.Empty;
        return false;
    }

    private static Task WriteErrorAsync(HttpContext context, SqsWireProtocol protocol, SqsErrorMapping.Mapping mapping) =>
        SqsErrorResponse.WriteAsync(context, protocol, mapping.StatusCode, mapping.Code, mapping.Message, mapping.FaultType);

    /// <summary>
    /// Translates an exception from the AMQP stack into a structured
    /// SQS error mapping. <see cref="AmqpLinkException"/> and
    /// <see cref="AmqpConnectionException"/> carry the spec-classified
    /// <see cref="AmqpErrorKind"/> plus the peer's condition symbol,
    /// which <see cref="SqsErrorMapping.FromAmqp"/> uses to choose the
    /// right SQS code (lock-lost → <c>MessageNotInflight</c>,
    /// not-found → <c>NonExistentQueue</c>, throttled → 503, etc.).
    /// Anything else falls back to a generic 500 — but unlike the
    /// previous slice we no longer concatenate <c>ex.Message</c> into
    /// the response body (it can leak broker-internal diagnostics).
    /// </summary>
    private static SqsErrorMapping.Mapping MapAmqpException(Exception ex, string operation)
    {
        return ex switch
        {
            AmqpLinkException link => SqsErrorMapping.FromAmqp(link.Kind, link.PeerCondition, operation),
            AmqpConnectionException conn => SqsErrorMapping.FromAmqp(conn.Kind, conn.PeerCondition, operation),
            _ => SqsErrorMapping.InternalError($"aws2azure: AMQP {operation} failed."),
        };
    }

    /// <summary>
    /// Translates a <see cref="ServiceBusManagementException"/> (raised
    /// by the <c>$management</c> request-response link when SB returns a
    /// non-2xx <c>statusCode</c>) onto an SQS-shaped error. Prefers the
    /// AMQP error condition for classification when present; falls back
    /// to HTTP-style status-code buckets when the broker omits it.
    /// </summary>
    private static SqsErrorMapping.Mapping MapManagementException(
        ServiceBusManagementException ex, string operation)
    {
        if (!string.IsNullOrEmpty(ex.ErrorCondition))
        {
            var kind = AmqpErrorClassifier.Classify(ex.ErrorCondition);
            return SqsErrorMapping.FromAmqp(kind, ex.ErrorCondition, operation);
        }
        // 404 / 410 from SB $management = lock no longer valid for this
        // receiver — looks like an expired/settled message to the caller.
        if (ex.StatusCode == 404 || ex.StatusCode == 410)
            return SqsErrorMapping.MessageNotInflight();
        if (ex.StatusCode == 401 || ex.StatusCode == 403)
            return SqsErrorMapping.FromAmqp(AmqpErrorKind.Auth, condition: null, operation);
        if (ex.StatusCode == 429 || ex.StatusCode == 503)
            return SqsErrorMapping.FromAmqp(AmqpErrorKind.Throttled, condition: null, operation);
        if (ex.StatusCode >= 500)
            return SqsErrorMapping.FromAmqp(AmqpErrorKind.ServerFatal, condition: null, operation);
        return SqsErrorMapping.FromAmqp(AmqpErrorKind.ClientFatal, condition: null, operation);
    }
}
