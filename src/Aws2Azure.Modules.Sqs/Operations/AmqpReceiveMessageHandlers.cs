using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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
///   <item><c>ChangeMessageVisibility</c> short-circuits success and
///   stamps <c>Aws2Azure-VisibilityClamped</c> on the response.
///   Real <c>$management</c> renew-lock over AMQP is deferred to slice
///   8c; until then the AMQP path mirrors the REST handler's clamp
///   divergence.</item>
/// </list>
///
/// <para>Out of scope: long-polling (slice 8c), CMV via $management
/// (slice 8c), FIFO <c>MessageGroupId</c> round-trip (Slice 5 — REST
/// only until the bare-message parser gains <c>group-id</c>),
/// <c>x-opt-deadletter-source</c> system-attribute surfacing on
/// dead-lettered messages.</para>
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
            SqsOperation.ReceiveMessage          => ReceiveMessageAsync(context, parsed, receivers, ct),
            SqsOperation.DeleteMessage           => DeleteMessageAsync(context, parsed, receivers, ct),
            SqsOperation.ChangeMessageVisibility => ChangeMessageVisibilityAsync(context, parsed, receivers, ct),
            _                                    => WriteErrorAsync(context, parsed.Protocol,
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
        try
        {
            receiver = await receivers.GetReceiverAsync(queueName, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await WriteErrorAsync(context, parsed.Protocol,
                SqsErrorMapping.InternalError("AMQP connection failed: " + ex.Message)).ConfigureAwait(false);
            return;
        }

        IReadOnlyList<ServiceBusReceivedMessage> batch;
        try
        {
            var timeout = waitSeconds > 0 ? TimeSpan.FromSeconds(waitSeconds) : DefaultReceiveTimeout;
            batch = await receiver.ReceiveBatchAsync(maxMessages, timeout, ct).ConfigureAwait(false);
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
            // SAS key.
            await receivers.InvalidateAsync(queueName, closeConnection: false).ConfigureAwait(false);
            await WriteErrorAsync(context, parsed.Protocol,
                SqsErrorMapping.InternalError("AMQP receive failed: " + ex.Message)).ConfigureAwait(false);
            return;
        }

        var systemAttrFilter = ParseAttributeNames(parsed, "AttributeName");
        var collected = new List<ReceivedSqsMessage>(batch.Count);
        foreach (var msg in batch)
        {
            var built = BuildReceivedMessage(queueName, msg, systemAttrFilter);
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

        ServiceBusReceiver receiver;
        try
        {
            receiver = await receivers.GetReceiverAsync(queueName, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await WriteErrorAsync(context, parsed.Protocol,
                SqsErrorMapping.InternalError("AMQP connection failed: " + ex.Message)).ConfigureAwait(false);
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
            await receivers.InvalidateAsync(queueName, closeConnection: false).ConfigureAwait(false);
            await WriteErrorAsync(context, parsed.Protocol,
                SqsErrorMapping.InternalError("AMQP delete failed: " + ex.Message)).ConfigureAwait(false);
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

        // AMQP renew-lock travels over a Service Bus $management
        // request-response link (slice 8c). Until that lands the AMQP
        // path mirrors the REST handler's clamp divergence: we accept
        // the request, do NOT actually extend the lock, and signal the
        // divergence on the response header so anyone debugging traffic
        // can see what happened.
        //
        // Note: this differs from the REST handler in that we don't
        // even validate the lock-token is still in flight (REST gets
        // a 404 from SB on an expired/wrong lock). The CMV gap-doc
        // tracks this — slice 8c reconciles when the $management link
        // is wired up.
        _ = receivers; // suppress unused warning; reserved for future RenewLock wiring.

        context.Response.Headers["Aws2Azure-VisibilityClamped"] =
            visibility.ToString(CultureInfo.InvariantCulture);
        await SqsResponseWriter.WriteChangeMessageVisibilityAsync(context, parsed.Protocol).ConfigureAwait(false);
    }

    // --- SB AMQP message → SQS message translation ---------------------

    private static ReceivedSqsMessage? BuildReceivedMessage(
        string queueName, ServiceBusReceivedMessage msg, HashSet<string>? systemAttrFilter)
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

        var systemAttrs = BuildSystemAttributes(
            enqueuedTimeMillis, deliveryCount, sequenceNumber, systemAttrFilter);

        var handle = AmqpReceiptHandle.Encode(queueName, lockToken, lockedUntil);
        return new ReceivedSqsMessage(
            MessageId: messageId!,
            ReceiptHandle: handle,
            MD5OfBody: md5OfBody,
            Body: bodyText,
            MD5OfMessageAttributes: null,
            Attributes: systemAttrs,
            // Application-properties → SQS message-attributes round-trip
            // is deferred. SendMessage on the REST path emits an
            // Aws2Azure-AttrTypes header to round-trip the SQS DataType
            // tag; carrying that over AMQP needs a parallel mechanism
            // (e.g. an x-aws2azure-* application-property). Tracked in
            // the ReceiveMessage gap doc.
            MessageAttributes: null);
    }

    private static IReadOnlyDictionary<string, string>? BuildSystemAttributes(
        long? enqueuedTimeMillis, int deliveryCount, string? sequenceNumber, HashSet<string>? filter)
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
}
