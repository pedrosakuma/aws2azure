using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Aws2Azure.Amqp.ServiceBus;
using Aws2Azure.Modules.Sqs.Internal;
using Aws2Azure.Modules.Sqs.Xml;

namespace Aws2Azure.Modules.Sqs.Operations;

internal static class AmqpMessageTranslator
{
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
        var body = msg.Body.Span;
        var bodyText = body.IsEmpty ? string.Empty : Encoding.UTF8.GetString(body);
        var md5OfBody = SqsMessageMd5.OfBody(body);

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
        var deadLetterSource = NormalizeDeadLetterSource(annotations?.DeadLetterSource);
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
        if (!appProps.TryGetValue(SqsAttributeTypeRegistry.HeaderName, out var registryObj)
            || registryObj is not string registryRaw
            || string.IsNullOrEmpty(registryRaw))
        {
            return (null, null);
        }

        var typeRegistry = SqsAttributeTypeRegistry.Parse(registryRaw);
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

    private static string? NormalizeDeadLetterSource(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return null;
        var normalized = source.Trim('/');
        var slash = normalized.IndexOf('/');
        if (slash >= 0)
            normalized = normalized[..slash];
        return normalized.Length == 0 ? null : Uri.UnescapeDataString(normalized);
    }

}
