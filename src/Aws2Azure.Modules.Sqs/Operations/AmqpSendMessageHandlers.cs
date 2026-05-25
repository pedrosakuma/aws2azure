using System;
using System.Collections.Generic;
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
/// AMQP variant of <see cref="SendMessageHandlers.SendMessage"/> — used
/// when the queue's effective transport is
/// <see cref="Aws2Azure.Core.Configuration.SqsTransport.Amqp"/>. Mirrors
/// the REST handler's validation and idempotency-key minting so the
/// SQS-visible behaviour is identical; only the wire to Service Bus
/// differs.
///
/// <para>SQS → AMQP mapping:</para>
/// <list type="bullet">
///   <item><c>MessageBody</c> → AMQP <c>data</c> section (UTF-8 bytes).</item>
///   <item><c>MessageGroupId</c> (FIFO only) → <c>properties.group-id</c>
///         (Service Bus session-id).</item>
///   <item>Idempotency key → <c>properties.message-id</c>. For FIFO
///         queues this is the caller's <c>MessageDeduplicationId</c>
///         (so SB's dedup window collapses retries); for standard
///         queues it's a fresh GUID stable across the retry loop so
///         dedup-enabled standard queues benefit equally.</item>
///   <item><c>DelaySeconds</c> &gt; 0 → message-annotations
///         <c>x-opt-scheduled-enqueue-time</c> (UTC timestamp).</item>
///   <item><c>MessageAttributes</c> → AMQP
///         <c>application-properties</c>: each attribute lands as a
///         string entry (binary attributes are base64-encoded, same
///         as the REST path's HTTP-header view) and the per-attribute
///         data types are carried in a side-channel entry
///         <see cref="SendMessageHandlers.AttrTypesHeader"/> so the
///         receive path can round-trip the original SQS view.</item>
/// </list>
/// </summary>
internal static class AmqpSendMessageHandlers
{
    public static Task HandleAsync(
        HttpContext context,
        SqsParseResult parsed,
        IAmqpSenderProvider senders,
        CancellationToken ct) =>
        parsed.Operation switch
        {
            SqsOperation.SendMessage => SendMessageAsync(context, parsed, senders, ct),
            _                         => SendMessageHandlers.WriteErrorAsync(
                                            context, parsed.Protocol,
                                            SqsErrorMapping.NotImplemented(parsed.Operation)),
        };

    private static async Task SendMessageAsync(
        HttpContext context, SqsParseResult parsed, IAmqpSenderProvider senders, CancellationToken ct)
    {
        var queueName = SendMessageHandlers.ExtractQueueName(parsed);
        if (queueName is null)
        {
            await SendMessageHandlers.WriteErrorAsync(context, parsed.Protocol,
                SqsErrorMapping.InvalidParameterValue("QueueUrl", "QueueUrl is required and must be a queue URL.")).ConfigureAwait(false);
            return;
        }

        if (!SendMessageHandlers.TryGetParam(parsed, "MessageBody", out var body))
        {
            await SendMessageHandlers.WriteErrorAsync(context, parsed.Protocol,
                SqsErrorMapping.MissingParameter("MessageBody")).ConfigureAwait(false);
            return;
        }
        var bodyBytes = Encoding.UTF8.GetBytes(body);

        if (!SendMessageHandlers.TryParseDelaySeconds(parsed, out var delaySeconds, out var delayError))
        {
            await SendMessageHandlers.WriteErrorAsync(context, parsed.Protocol, delayError!.Value).ConfigureAwait(false);
            return;
        }

        var attrResult = SendMessageHandlers.ParseMessageAttributes(parsed, "MessageAttribute");
        if (attrResult.IsError)
        {
            await SendMessageHandlers.WriteErrorAsync(context, parsed.Protocol,
                SqsErrorMapping.InvalidParameterValue("MessageAttributes", attrResult.ErrorMessage!)).ConfigureAwait(false);
            return;
        }
        var attrs = attrResult.Attributes ?? new Dictionary<string, SqsMessageAttribute>();

        if (SendMessageHandlers.ComputeWireSize(bodyBytes.Length, attrs) > SendMessageHandlers.MaxBodyBytes)
        {
            await SendMessageHandlers.WriteErrorAsync(context, parsed.Protocol, SqsErrorMapping.MessageTooLong()).ConfigureAwait(false);
            return;
        }

        SendMessageHandlers.TryGetParam(parsed, "MessageGroupId", out var groupId);
        SendMessageHandlers.TryGetParam(parsed, "MessageDeduplicationId", out var dedupId);

        var isFifoQueue = queueName.EndsWith(".fifo", StringComparison.Ordinal);
        if (isFifoQueue && string.IsNullOrEmpty(groupId))
        {
            await SendMessageHandlers.WriteErrorAsync(context, parsed.Protocol,
                SqsErrorMapping.MissingParameter("MessageGroupId")).ConfigureAwait(false);
            return;
        }
        if (isFifoQueue && string.IsNullOrEmpty(dedupId))
        {
            await SendMessageHandlers.WriteErrorAsync(context, parsed.Protocol,
                SqsErrorMapping.MissingParameter("MessageDeduplicationId")).ConfigureAwait(false);
            return;
        }
        if (!isFifoQueue && !string.IsNullOrEmpty(groupId))
        {
            await SendMessageHandlers.WriteErrorAsync(context, parsed.Protocol,
                SqsErrorMapping.InvalidParameterValue("MessageGroupId",
                    "MessageGroupId is only valid on FIFO queues.")).ConfigureAwait(false);
            return;
        }
        if (!isFifoQueue && !string.IsNullOrEmpty(dedupId))
        {
            await SendMessageHandlers.WriteErrorAsync(context, parsed.Protocol,
                SqsErrorMapping.InvalidParameterValue("MessageDeduplicationId",
                    "MessageDeduplicationId is only valid on FIFO queues.")).ConfigureAwait(false);
            return;
        }

        // Idempotency-key contract identical to the REST handler.
        var idempotencyKey = isFifoQueue ? dedupId! : Guid.NewGuid().ToString();

        var amqpMessage = BuildAmqpMessage(bodyBytes, attrs, idempotencyKey, groupId, delaySeconds);

        try
        {
            var sender = await senders.GetSenderAsync(queueName, ct).ConfigureAwait(false);
            try
            {
                await sender.SendAsync(amqpMessage, settled: false, ct).ConfigureAwait(false);
            }
            catch (ServiceBusSendException ex)
            {
                // Translate the broker's AMQP error condition into an
                // SQS-shaped error. Throttling (com.microsoft:server-busy)
                // and transient failures become 503 ServiceUnavailable
                // which the AWS SDK retries with exponential backoff;
                // fatal conditions surface as the appropriate 4xx codes.
                // The link itself is still healthy after a per-delivery
                // Rejected, so we keep the cached sender (no Invalidate).
                var kind = AmqpErrorClassifier.Classify(ex.ErrorCondition);
                var mapping = SqsErrorMapping.FromAmqp(kind, ex.ErrorCondition, "SendMessage");
                await SendMessageHandlers.WriteErrorAsync(context, parsed.Protocol, mapping).ConfigureAwait(false);
                return;
            }
            catch (Exception)
            {
                // Link- or connection-level failure: evict the cached
                // sender so the next request rebuilds it. Keep the
                // connection warm — caller can retry idempotently
                // because we use a stable MessageId.
                await senders.InvalidateSenderAsync(queueName, closeConnection: false).ConfigureAwait(false);
                throw;
            }
        }
        catch (Exception ex)
        {
            await SendMessageHandlers.WriteErrorAsync(context, parsed.Protocol,
                SqsErrorMapping.InternalError($"AMQP send failed: {ex.GetType().Name}")).ConfigureAwait(false);
            return;
        }

        var md5OfBody = SqsMessageMd5.OfBody(bodyBytes);
        var md5OfAttrs = attrs.Count == 0 ? null : SqsMessageMd5.OfAttributes(attrs);

        await SqsResponseWriter.WriteSendMessageAsync(
            context, parsed.Protocol, idempotencyKey, md5OfBody, md5OfAttrs, sequenceNumber: null).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the <see cref="AmqpMessage"/> that the SB sender link
    /// publishes. Visible for unit tests so the wire-level mapping can
    /// be asserted without spinning up a connection.
    /// </summary>
    internal static AmqpMessage BuildAmqpMessage(
        byte[] body,
        IReadOnlyDictionary<string, SqsMessageAttribute> attrs,
        string messageId,
        string? groupId,
        int delaySeconds)
    {
        var msg = new AmqpMessage
        {
            Properties = new AmqpProperties
            {
                MessageId = messageId,
                GroupId = string.IsNullOrEmpty(groupId) ? null : groupId,
            },
            Body = body,
        };

        if (attrs.Count > 0)
        {
            var ap = new Dictionary<string, object?>(attrs.Count + 1, StringComparer.Ordinal);
            var typeRegistry = new StringBuilder();
            var first = true;
            foreach (var kv in attrs)
            {
                var value = kv.Value.IsBinary
                    ? Convert.ToBase64String(kv.Value.BinaryValue.Span)
                    : (kv.Value.StringValue ?? string.Empty);
                ap[kv.Key] = value;

                if (!first) typeRegistry.Append(',');
                first = false;
                typeRegistry.Append(kv.Key).Append('=').Append(kv.Value.DataType);
            }
            ap[SendMessageHandlers.AttrTypesHeader] = typeRegistry.ToString();
            msg.ApplicationProperties = ap;
        }

        if (delaySeconds > 0)
        {
            msg.MessageAnnotations = new AmqpMessageAnnotations
            {
                ScheduledEnqueueTime = DateTimeOffset.UtcNow.AddSeconds(delaySeconds),
            };
        }

        return msg;
    }
}
