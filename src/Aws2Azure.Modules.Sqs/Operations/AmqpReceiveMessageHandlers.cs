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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
internal static partial class AmqpReceiveMessageHandlers
{
    private static readonly TimeSpan ReceiveBurstTailWait = TimeSpan.FromMilliseconds(50);

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
            SqsOperation.DeleteMessage               => AmqpSettleDispatcher.DeleteMessageAsync(context, parsed, receivers, ct),
            SqsOperation.ChangeMessageVisibility     => AmqpSettleDispatcher.ChangeMessageVisibilityAsync(context, parsed, receivers, ct),
            SqsOperation.DeleteMessageBatch          => AmqpSettleDispatcher.DeleteMessageBatchAsync(context, parsed, receivers, ct),
            SqsOperation.ChangeMessageVisibilityBatch => AmqpSettleDispatcher.ChangeMessageVisibilityBatchAsync(context, parsed, receivers, ct),
            _                                        => AmqpReceiveParameters.WriteErrorAsync(context, parsed.Protocol,
                                                           SqsErrorMapping.NotImplemented(parsed.Operation)),
        };

    // --- ReceiveMessage ------------------------------------------------

    private static async Task ReceiveMessageAsync(
        HttpContext context, SqsParseResult parsed, IAmqpReceiverProvider receivers, CancellationToken ct)
    {
        var queueName = AmqpReceiveParameters.ExtractQueueName(parsed);
        if (queueName is null)
        {
            await AmqpReceiveParameters.WriteErrorAsync(context, parsed.Protocol,
                SqsErrorMapping.InvalidParameterValue("QueueUrl", "QueueUrl is required and must be a queue URL.")).ConfigureAwait(false);
            return;
        }

        if (!AmqpReceiveParameters.TryParseBoundedInt(parsed, "MaxNumberOfMessages", min: 1, max: MaxMessages, defaultValue: 1, out var maxMessages))
        {
            await AmqpReceiveParameters.WriteErrorAsync(context, parsed.Protocol, SqsErrorMapping.ReceiveLimitInvalid()).ConfigureAwait(false);
            return;
        }
        if (!AmqpReceiveParameters.TryParseBoundedInt(parsed, "WaitTimeSeconds", min: 0, max: MaxWaitTimeSeconds, defaultValue: 0, out var waitSeconds))
        {
            await AmqpReceiveParameters.WriteErrorAsync(context, parsed.Protocol, SqsErrorMapping.ReceiveWaitTimeInvalid()).ConfigureAwait(false);
            return;
        }
        // VisibilityTimeout is validated but not honoured — SB's AMQP lock
        // duration is the queue-level setting. Surface that divergence in
        // the gap doc; callers asking for a different visibility get the
        // queue-level value instead.
        if (!AmqpReceiveParameters.TryParseBoundedInt(parsed, "VisibilityTimeout", min: 0, max: MaxVisibilityTimeoutSeconds, defaultValue: -1, out _))
        {
            await AmqpReceiveParameters.WriteErrorAsync(context, parsed.Protocol, SqsErrorMapping.VisibilityTimeoutInvalid()).ConfigureAwait(false);
            return;
        }

        var timeout = waitSeconds > 0 ? TimeSpan.FromSeconds(waitSeconds) : DefaultReceiveTimeout;

        ServiceBusReceiver receiver;
        var remaining = timeout;
        var isFifo = QueueName.IsFifo(queueName);
        try
        {
            // FIFO queues map to SB session-aware receive. The client
            // doesn't know the MessageGroupId in advance, so the broker
            // picks any available session and we adopt the resulting
            // receiver into the pool keyed by the resolved session-id
            // (slice 7c.3a). Subsequent settle requests with a v3
            // receipt handle route back via GetSessionReceiverAsync.
            if (isFifo)
            {
                var acquisition = await receivers
                    .AcquireBrokerAssignedSessionReceiverAsync(queueName, timeout, ct)
                    .ConfigureAwait(false);
                if (acquisition.Receiver is null)
                {
                    await SqsResponseWriter.WriteReceiveMessageAsync(
                        context, parsed.Protocol, Array.Empty<ReceivedSqsMessage>()).ConfigureAwait(false);
                    return;
                }
                receiver = acquisition.Receiver;
                remaining = timeout - acquisition.BrokerWaitElapsed;
            }
            else
            {
                receiver = await receivers.GetReceiverAsync(queueName, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await AmqpReceiveParameters.WriteErrorAsync(context, parsed.Protocol,
                AmqpErrorMapper.MapAmqpException(ex, "ReceiveMessage")).ConfigureAwait(false);
            return;
        }

        if (remaining <= TimeSpan.Zero)
        {
            if (isFifo && receiver.SessionId is { } expiredSessionId && receiver.InFlightCount == 0)
                await receivers.InvalidateSessionReceiverAsync(queueName, expiredSessionId).ConfigureAwait(false);
            await SqsResponseWriter.WriteReceiveMessageAsync(
                context, parsed.Protocol, Array.Empty<ReceivedSqsMessage>()).ConfigureAwait(false);
            return;
        }

        IReadOnlyList<ServiceBusReceivedMessage> batch;
        try
        {
            batch = await receiver.ReceiveBatchAsync(
                maxMessages,
                remaining,
                tailWait: ReceiveBurstTailWait,
                cancellationToken: ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (isFifo && receiver.SessionId is { } sessionId)
                await receivers.InvalidateSessionReceiverAsync(queueName, sessionId).ConfigureAwait(false);
            else
                await receivers.InvalidateAsync(queueName, closeConnection: false).ConfigureAwait(false);
            await AmqpReceiveParameters.WriteErrorAsync(context, parsed.Protocol,
                AmqpErrorMapper.MapAmqpException(ex, "ReceiveMessage")).ConfigureAwait(false);
            return;
        }

        if (isFifo && batch.Count == 0 && receiver.SessionId is { } emptySessionId && receiver.InFlightCount == 0)
            await receivers.InvalidateSessionReceiverAsync(queueName, emptySessionId).ConfigureAwait(false);

        AmqpReceiveParameters.ParseAttributeNameSets(parsed, out var systemAttrFilter, out var messageAttrFilter);
        var collected = new List<ReceivedSqsMessage>(batch.Count);
        foreach (var msg in batch)
        {
            var built = AmqpMessageTranslator.BuildReceivedMessage(queueName, msg, receiver.SessionId, systemAttrFilter, messageAttrFilter);
            if (built is null)
            {
                // No lock-token (sender-settled / non-16-byte tag) → we
                // can't mint a receipt-handle that DeleteMessage can settle.
                // Abandon (modified) so SB redelivers and another consumer
                // can try, rather than handing the client a useless handle.
                if (msg.LockToken is null)
                {
                    try { await receiver.AbandonAsync(msg, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        LogBestEffortAbandonFailed(context, queueName, ex);
                    }
                }
                continue;
            }
            collected.Add(built);
        }

        await SqsResponseWriter.WriteReceiveMessageAsync(context, parsed.Protocol, collected).ConfigureAwait(false);
    }

    private static void LogBestEffortAbandonFailed(HttpContext context, string queueName, Exception exception)
    {
        var loggerFactory = context.RequestServices.GetService<ILoggerFactory>();
        if (loggerFactory is null)
        {
            return;
        }

        BestEffortAbandonFailed(
            loggerFactory.CreateLogger("Aws2Azure.Modules.Sqs.Operations.AmqpReceiveMessageHandlers"),
            queueName,
            exception);
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Trace,
        Message = "Best-effort AMQP abandon failed for an SQS message without a lock token on queue '{QueueName}'.")]
    private static partial void BestEffortAbandonFailed(ILogger logger, string queueName, Exception exception);
}
