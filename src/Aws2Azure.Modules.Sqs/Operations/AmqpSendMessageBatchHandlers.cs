using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Aws2Azure.Amqp.Connection;
using Aws2Azure.Amqp.ServiceBus;
using Aws2Azure.Modules.Sqs.Errors;
using Aws2Azure.Modules.Sqs.Internal;
using Aws2Azure.Modules.Sqs.WireProtocol;
using Aws2Azure.Modules.Sqs.Xml;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.Sqs.Operations;

/// <summary>
/// AMQP variant of <c>SendMessageBatch</c> — used when the queue's
/// effective transport is
/// <see cref="Aws2Azure.Core.Configuration.SqsTransport.Amqp"/>.
///
/// <para>Reuses every piece of REST-side validation
/// (<see cref="SendMessageHandlers.ParseBatchEntriesQuery"/> /
/// <see cref="SendMessageHandlers.ParseBatchEntriesJson"/>, FIFO interlock,
/// id uniqueness, MaxBatchEntries/MaxBatchBytes caps and the
/// idempotency-key minting rule) so the SQS-visible contract is
/// identical to the REST path. The difference is partial-failure
/// granularity: AMQP gives us per-transfer dispositions, so a
/// well-formed batch with one rejected entry returns the other entries
/// as <c>Successful</c> (the REST path can only mark the whole batch
/// failed since SB's batch POST is all-or-nothing).</para>
/// </summary>
internal static class AmqpSendMessageBatchHandlers
{
    public static Task HandleAsync(
        HttpContext context,
        SqsParseResult parsed,
        IAmqpSenderProvider senders,
        CancellationToken ct) =>
        parsed.Operation switch
        {
            SqsOperation.SendMessageBatch => SendMessageBatchAsync(context, parsed, senders, ct),
            _                              => SendMessageHandlers.WriteErrorAsync(
                                                 context, parsed.Protocol,
                                                 SqsErrorMapping.NotImplemented(parsed.Operation)),
        };

    private static async Task SendMessageBatchAsync(
        HttpContext context, SqsParseResult parsed, IAmqpSenderProvider senders, CancellationToken ct)
    {
        var queueName = SendMessageHandlers.ExtractQueueName(parsed);
        if (queueName is null)
        {
            await SendMessageHandlers.WriteErrorAsync(context, parsed.Protocol,
                SqsErrorMapping.InvalidParameterValue("QueueUrl", "QueueUrl is required and must be a queue URL.")).ConfigureAwait(false);
            return;
        }

        var entries = parsed.Protocol == SqsWireProtocol.AwsJson
            ? SendMessageHandlers.ParseBatchEntriesJson(parsed.JsonBody)
            : SendMessageHandlers.ParseBatchEntriesQuery(parsed.Parameters);

        if (entries is null)
        {
            await SendMessageHandlers.WriteErrorAsync(context, parsed.Protocol,
                SqsErrorMapping.InvalidParameterValue("Entries", "Could not parse batch entries.")).ConfigureAwait(false);
            return;
        }
        if (entries.Count == 0)
        {
            await SendMessageHandlers.WriteErrorAsync(context, parsed.Protocol, SqsErrorMapping.EmptyBatchRequest()).ConfigureAwait(false);
            return;
        }
        if (entries.Count > SendMessageHandlers.MaxBatchEntries)
        {
            await SendMessageHandlers.WriteErrorAsync(context, parsed.Protocol,
                SqsErrorMapping.TooManyEntriesInBatchRequest(entries.Count)).ConfigureAwait(false);
            return;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var totalBytes = 0;
        var batchIsFifoQueue = queueName.EndsWith(".fifo", StringComparison.Ordinal);
        foreach (var e in entries)
        {
            if (!SendMessageHandlers.IsValidBatchEntryId(e.Id))
            {
                await SendMessageHandlers.WriteErrorAsync(context, parsed.Protocol,
                    SqsErrorMapping.InvalidBatchEntryId(e.Id)).ConfigureAwait(false);
                return;
            }
            if (!seen.Add(e.Id))
            {
                await SendMessageHandlers.WriteErrorAsync(context, parsed.Protocol,
                    SqsErrorMapping.BatchEntryIdsNotDistinct(e.Id)).ConfigureAwait(false);
                return;
            }
            if (SqsFifoSendValidator.Validate(queueName, e.GroupId, e.DeduplicationId) is { } fifoError)
            {
                await SendMessageHandlers.WriteErrorAsync(context, parsed.Protocol,
                    fifoError).ConfigureAwait(false);
                return;
            }
            totalBytes += SendMessageHandlers.ComputeWireSize(e.BodyBytes.Length, e.Attributes);
        }
        if (totalBytes > SendMessageHandlers.MaxBatchBytes)
        {
            await SendMessageHandlers.WriteErrorAsync(context, parsed.Protocol,
                SqsErrorMapping.BatchRequestTooLong(totalBytes)).ConfigureAwait(false);
            return;
        }

        // Mint stable per-entry idempotency keys BEFORE any retry path so
        // a client retry of the same batch collapses under SB dedup.
        var idempotencyKeys = new string[entries.Count];
        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            idempotencyKeys[i] = batchIsFifoQueue ? e.DeduplicationId! : Guid.NewGuid().ToString();
        }

        ServiceBusAmqpSender sender;
        try
        {
            sender = await senders.GetSenderAsync(queueName, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Whole-batch failure (could not even open the link). Surface
            // every entry as failed so the caller can retry uniformly.
            var mapping = SqsErrorMapping.InternalError($"AMQP send link unavailable: {ex.GetType().Name}");
            var allFailed = new List<SendMessageBatchEntryError>(entries.Count);
            foreach (var e in entries)
            {
                allFailed.Add(new SendMessageBatchEntryError(
                    e.Id, mapping.Code, mapping.Message,
                    SenderFault: mapping.FaultType == SqsErrorResponse.FaultType.Sender));
            }
            await SqsResponseWriter.WriteSendMessageBatchAsync(
                context, parsed.Protocol, Array.Empty<SendMessageBatchEntryResult>(), allFailed).ConfigureAwait(false);
            return;
        }

        // Dispatch each entry over the same sender link; the sender's
        // internal gate serialises wire writes but per-message
        // dispositions still surface individually. We launch the sends
        // concurrently so the broker can pipeline ack handling — when
        // there's nothing to gate on, this collapses to a fast
        // sequential send.
        var sendTasks = new Task<bool>[entries.Count];
        var entryErrors = new SqsErrorMapping.Mapping?[entries.Count];
        for (var i = 0; i < entries.Count; i++)
        {
            sendTasks[i] = SendOneAsync(sender, entries[i], idempotencyKeys[i], entryErrors, i, ct);
        }
        await Task.WhenAll(sendTasks).ConfigureAwait(false);

        var successful = new List<SendMessageBatchEntryResult>(entries.Count);
        var failed = new List<SendMessageBatchEntryError>();
        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            if (sendTasks[i].Result)
            {
                var md5OfBody = SqsMessageMd5.OfBody(e.BodyBytes);
                var md5OfAttrs = e.Attributes.Count == 0 ? null : SqsMessageMd5.OfAttributes(e.Attributes);
                successful.Add(new SendMessageBatchEntryResult(
                    e.Id, idempotencyKeys[i], md5OfBody, md5OfAttrs, SequenceNumber: null));
            }
            else
            {
                var mapping = entryErrors[i] ?? SqsErrorMapping.InternalError("AMQP send failed.");
                failed.Add(new SendMessageBatchEntryError(
                    e.Id, mapping.Code, mapping.Message,
                    SenderFault: mapping.FaultType == SqsErrorResponse.FaultType.Sender));
            }
        }

        await SqsResponseWriter.WriteSendMessageBatchAsync(
            context, parsed.Protocol, successful, failed).ConfigureAwait(false);
    }

    private static async Task<bool> SendOneAsync(
        ServiceBusAmqpSender sender,
        SendMessageHandlers.BatchEntry entry,
        string idempotencyKey,
        SqsErrorMapping.Mapping?[] errors,
        int index,
        CancellationToken ct)
    {
        var msg = AmqpSendMessageHandlers.BuildAmqpMessage(
            body: entry.BodyBytes,
            attrs: entry.Attributes,
            messageId: idempotencyKey,
            groupId: entry.GroupId,
            delaySeconds: entry.DelaySeconds);
        try
        {
            await sender.SendAsync(msg, settled: false, ct).ConfigureAwait(false);
            return true;
        }
        catch (ServiceBusSendException ex)
        {
            errors[index] = SqsErrorMapping.InvalidParameterValue("MessageBody",
                $"Service Bus rejected the message ({ex.Outcome}).");
            return false;
        }
        catch (Exception ex)
        {
            errors[index] = SqsErrorMapping.InternalError($"AMQP send failed: {ex.GetType().Name}");
            return false;
        }
    }
}
