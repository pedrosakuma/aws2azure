using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
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
/// Slice-3 receive-side dispatch: <c>ReceiveMessage</c>,
/// <c>DeleteMessage</c>, <c>ChangeMessageVisibility</c>.
///
/// <para>Service Bus's REST surface offers single-message peek-lock semantics
/// at <c>POST /{queue}/messages/head</c>. SQS's <c>ReceiveMessage</c> can
/// return up to 10 messages at once — Slice 3 emulates the batch by
/// short-poll-looping the SB endpoint with <c>timeout=0</c> until either
/// the requested count is reached or SB returns 204 (queue empty).</para>
///
/// <para>Long-polling (<c>WaitTimeSeconds &gt; 0</c>) is intentionally
/// rejected here and lands in Slice 4 — that lets Slice 3 stay focused on
/// the visibility / lock-token contract that <c>DeleteMessage</c> and
/// <c>ChangeMessageVisibility</c> depend on.</para>
/// </summary>
internal static class ReceiveMessageHandlers
{
    /// <summary>AWS hard cap on a single ReceiveMessage call.</summary>
    public const int MaxMessages = 10;

    /// <summary>AWS hard cap on VisibilityTimeout (12 hours).</summary>
    public const int MaxVisibilityTimeoutSeconds = 43_200;

    /// <summary>AWS hard cap on WaitTimeSeconds (Slice 4 implements polling).</summary>
    public const int MaxWaitTimeSeconds = 20;

    /// <summary>Side-channel header SendMessage emits to round-trip SQS attribute data types.</summary>
    public const string AttrTypesHeader = "Aws2Azure-AttrTypes";

    /// <summary>Soft cap on how long the per-message peek-lock loop will wait in aggregate before returning what it has.</summary>
    public static readonly TimeSpan ReceiveLoopBudget = TimeSpan.FromSeconds(5);

    public static Task HandleAsync(
        HttpContext context,
        SqsParseResult parsed,
        ServiceBusClient sb,
        CancellationToken ct) =>
        parsed.Operation switch
        {
            SqsOperation.ReceiveMessage          => ReceiveMessageAsync(context, parsed, sb, ct),
            SqsOperation.DeleteMessage           => DeleteMessageAsync(context, parsed, sb, ct),
            SqsOperation.ChangeMessageVisibility => ChangeMessageVisibilityAsync(context, parsed, sb, ct),
            _                                     => WriteErrorAsync(context, parsed.Protocol,
                                                          SqsErrorMapping.NotImplemented(parsed.Operation)),
        };

    // --- ReceiveMessage ------------------------------------------------

    private static async Task ReceiveMessageAsync(
        HttpContext context, SqsParseResult parsed, ServiceBusClient sb, CancellationToken ct)
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

        // VisibilityTimeout is accepted and validated, but SB's renew-lock
        // semantics don't let us set an arbitrary lock duration on the
        // initial receive — the queue's configured LockDuration wins. We
        // surface that divergence in the gap doc; callers asking for a
        // different visibility get the queue-level value instead.
        if (!TryParseBoundedInt(parsed, "VisibilityTimeout", min: 0, max: MaxVisibilityTimeoutSeconds, defaultValue: -1, out _))
        {
            await WriteErrorAsync(context, parsed.Protocol, SqsErrorMapping.VisibilityTimeoutInvalid()).ConfigureAwait(false);
            return;
        }

        var systemAttrFilter = ParseAttributeNames(parsed, "AttributeName");
        var messageAttrFilter = ParseAttributeNames(parsed, "MessageAttributeName");

        // Long-poll semantics (Slice 4): the *first* peek-lock call uses SB's
        // native server-side wait (timeout query parameter, up to 20s — same
        // hard cap as SQS). If at least one message arrives, follow-up calls
        // for the rest of the requested batch use timeout=0 to drain quickly
        // without re-blocking. If the first call returns empty after the
        // long poll, the receive returns immediately with an empty list,
        // matching SQS's "long-poll returns as soon as a message is
        // available or WaitTimeSeconds elapses" contract.
        var collected = new List<ReceivedSqsMessage>(maxMessages);
        var followupBudget = DateTimeOffset.UtcNow + ReceiveLoopBudget + TimeSpan.FromSeconds(waitSeconds);
        for (var i = 0; i < maxMessages; i++)
        {
            if (ct.IsCancellationRequested) break;
            if (i > 0 && DateTimeOffset.UtcNow > followupBudget) break;

            var perCallTimeout = i == 0 ? TimeSpan.FromSeconds(waitSeconds) : TimeSpan.Zero;
            using var response = await sb.PeekLockMessageAsync(queueName, perCallTimeout, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NoContent ||
                response.StatusCode == (HttpStatusCode)204)
            {
                break;
            }
            if (!response.IsSuccessStatusCode)
            {
                await WriteErrorAsync(context, parsed.Protocol, SqsErrorMapping.FromServiceBus(response)).ConfigureAwait(false);
                return;
            }

            var msg = await BuildReceivedMessageAsync(queueName, response, systemAttrFilter, messageAttrFilter, ct).ConfigureAwait(false);
            if (msg is not null)
            {
                collected.Add(msg);
            }
        }

        await SqsResponseWriter.WriteReceiveMessageAsync(context, parsed.Protocol, collected).ConfigureAwait(false);
    }

    // --- DeleteMessage -------------------------------------------------

    private static async Task DeleteMessageAsync(
        HttpContext context, SqsParseResult parsed, ServiceBusClient sb, CancellationToken ct)
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
        if (!ReceiptHandle.TryDecode(handle, out var decoded))
        {
            await WriteErrorAsync(context, parsed.Protocol, SqsErrorMapping.ReceiptHandleInvalid()).ConfigureAwait(false);
            return;
        }

        using var response = await sb.DeleteLockedMessageAsync(queueName, decoded.MessageId, decoded.LockToken, ct).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            await SqsResponseWriter.WriteDeleteMessageAsync(context, parsed.Protocol).ConfigureAwait(false);
            return;
        }

        // SB returns 404 either when the message has already been deleted
        // or when the lock has expired. SQS treats DeleteMessage as
        // idempotent on already-deleted messages but errors on an
        // expired lock — translate the SB 404 to the more specific SQS
        // ReceiptHandle/MessageNotInflight surface so client retries do
        // the right thing.
        if (response.StatusCode == HttpStatusCode.NotFound || response.StatusCode == HttpStatusCode.Gone)
        {
            await WriteErrorAsync(context, parsed.Protocol, SqsErrorMapping.ReceiptHandleInvalid()).ConfigureAwait(false);
            return;
        }
        await WriteErrorAsync(context, parsed.Protocol, SqsErrorMapping.FromServiceBus(response)).ConfigureAwait(false);
    }

    // --- ChangeMessageVisibility ---------------------------------------

    private static async Task ChangeMessageVisibilityAsync(
        HttpContext context, SqsParseResult parsed, ServiceBusClient sb, CancellationToken ct)
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
        if (!ReceiptHandle.TryDecode(handle, out var decoded))
        {
            await WriteErrorAsync(context, parsed.Protocol, SqsErrorMapping.ReceiptHandleInvalid()).ConfigureAwait(false);
            return;
        }
        if (!TryParseBoundedInt(parsed, "VisibilityTimeout", min: 0, max: MaxVisibilityTimeoutSeconds, defaultValue: -1, out var visibility) || visibility < 0)
        {
            await WriteErrorAsync(context, parsed.Protocol, SqsErrorMapping.VisibilityTimeoutInvalid()).ConfigureAwait(false);
            return;
        }

        // SB's renew-lock API extends the lock by the queue's configured
        // LockDuration — it doesn't accept an arbitrary new timeout. We
        // still issue the renew so the client's intent ("keep this
        // message in flight") is honoured, and we annotate the response
        // header so anyone debugging traffic can see the clamp.
        using var response = await sb.RenewLockAsync(queueName, decoded.MessageId, decoded.LockToken, ct).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            context.Response.Headers["Aws2Azure-VisibilityClamped"] = visibility.ToString(CultureInfo.InvariantCulture);
            await SqsResponseWriter.WriteChangeMessageVisibilityAsync(context, parsed.Protocol).ConfigureAwait(false);
            return;
        }
        if (response.StatusCode == HttpStatusCode.NotFound || response.StatusCode == HttpStatusCode.Gone)
        {
            await WriteErrorAsync(context, parsed.Protocol, SqsErrorMapping.ReceiptHandleInvalid()).ConfigureAwait(false);
            return;
        }
        await WriteErrorAsync(context, parsed.Protocol, SqsErrorMapping.FromServiceBus(response)).ConfigureAwait(false);
    }

    // --- SB response → SQS message translation -------------------------

    private static async Task<ReceivedSqsMessage?> BuildReceivedMessageAsync(
        string queueName,
        HttpResponseMessage response,
        HashSet<string>? systemAttrFilter,
        HashSet<string>? messageAttrFilter,
        CancellationToken ct)
    {
        // Decode BrokerProperties to get messageId, lockToken,
        // sequenceNumber, lockedUntilUtc.
        string? messageId = null, lockToken = null, sequenceNumber = null;
        string? sessionId = null;
        DateTimeOffset lockedUntil = DateTimeOffset.UtcNow.AddSeconds(30);
        long? enqueuedTime = null;
        int? deliveryCount = null;

        if (response.Headers.TryGetValues("BrokerProperties", out var bpValues))
        {
            var raw = bpValues.FirstOrDefault();
            if (!string.IsNullOrEmpty(raw))
            {
                try
                {
                    using var doc = JsonDocument.Parse(raw);
                    if (doc.RootElement.TryGetProperty("MessageId", out var mid) && mid.ValueKind == JsonValueKind.String)
                        messageId = mid.GetString();
                    if (doc.RootElement.TryGetProperty("LockToken", out var lt) && lt.ValueKind == JsonValueKind.String)
                        lockToken = lt.GetString();
                    if (doc.RootElement.TryGetProperty("SessionId", out var sid) && sid.ValueKind == JsonValueKind.String)
                        sessionId = sid.GetString();
                    if (doc.RootElement.TryGetProperty("SequenceNumber", out var sn) && sn.ValueKind == JsonValueKind.Number)
                        sequenceNumber = sn.GetInt64().ToString(CultureInfo.InvariantCulture);
                    if (doc.RootElement.TryGetProperty("LockedUntilUtc", out var lu) && lu.ValueKind == JsonValueKind.String &&
                        DateTimeOffset.TryParse(lu.GetString(), CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
                    {
                        lockedUntil = parsed;
                    }
                    if (doc.RootElement.TryGetProperty("EnqueuedTimeUtc", out var et) && et.ValueKind == JsonValueKind.String &&
                        DateTimeOffset.TryParse(et.GetString(), CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var enq))
                    {
                        enqueuedTime = enq.ToUnixTimeMilliseconds();
                    }
                    if (doc.RootElement.TryGetProperty("DeliveryCount", out var dc) && dc.ValueKind == JsonValueKind.Number)
                        deliveryCount = dc.GetInt32();
                }
                catch (JsonException)
                {
                    // Bad BrokerProperties → treat as malformed and skip.
                    return null;
                }
            }
        }

        // SB's REST also reports the lock token via the Location header
        // (`/{queue}/messages/{seq}/{lockToken}`) — fall back to that if
        // BrokerProperties was missing it.
        if (string.IsNullOrEmpty(lockToken) && response.Headers.Location is { } location)
        {
            var path = location.IsAbsoluteUri ? location.AbsolutePath : location.OriginalString;
            var segments = path.TrimEnd('/').Split('/');
            if (segments.Length >= 2)
            {
                lockToken = Uri.UnescapeDataString(segments[^1]);
                if (string.IsNullOrEmpty(sequenceNumber))
                {
                    sequenceNumber = Uri.UnescapeDataString(segments[^2]);
                }
            }
        }
        if (string.IsNullOrEmpty(messageId) || string.IsNullOrEmpty(lockToken))
        {
            // SB returned a message without enough metadata to ever delete
            // it — skip rather than handing the client a useless handle.
            return null;
        }

        var bodyBytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        var bodyText = Encoding.UTF8.GetString(bodyBytes);
        var md5OfBody = SqsMessageMd5.OfBody(bodyBytes);

        var typeRegistry = ParseAttrTypeRegistry(response);
        var (msgAttrs, md5OfAttrs) = BuildMessageAttributes(response, typeRegistry, messageAttrFilter);

        var systemAttrs = BuildSystemAttributes(enqueuedTime, deliveryCount, sequenceNumber,
            messageGroupId: sessionId, messageDeduplicationId: messageId, systemAttrFilter);

        var handle = ReceiptHandle.Encode(messageId!, lockToken!, sequenceNumber ?? string.Empty, lockedUntil);
        return new ReceivedSqsMessage(
            MessageId: messageId!,
            ReceiptHandle: handle,
            MD5OfBody: md5OfBody,
            Body: bodyText,
            MD5OfMessageAttributes: md5OfAttrs,
            Attributes: systemAttrs,
            MessageAttributes: msgAttrs);
    }

    private static IReadOnlyDictionary<string, string>? BuildSystemAttributes(
        long? enqueuedTimeMillis, int? deliveryCount, string? sequenceNumber,
        string? messageGroupId, string? messageDeduplicationId, HashSet<string>? filter)
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
        if (deliveryCount.HasValue)
            Add("ApproximateReceiveCount", deliveryCount.Value.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrEmpty(sequenceNumber))
            Add("SequenceNumber", sequenceNumber!);
        if (!string.IsNullOrEmpty(messageGroupId))
            Add("MessageGroupId", messageGroupId!);
        if (!string.IsNullOrEmpty(messageDeduplicationId))
            Add("MessageDeduplicationId", messageDeduplicationId!);
        return dict.Count == 0 ? null : dict;
    }

    private static (IReadOnlyDictionary<string, ReceivedSqsAttribute>?, string?) BuildMessageAttributes(
        HttpResponseMessage response,
        IReadOnlyDictionary<string, string> typeRegistry,
        HashSet<string>? filter)
    {
        if (filter is null || filter.Count == 0) return (null, null);
        var includeAll = filter.Contains("All", StringComparer.OrdinalIgnoreCase);

        var attrs = new SortedDictionary<string, ReceivedSqsAttribute>(StringComparer.Ordinal);
        var md5Source = new SortedDictionary<string, SqsMessageAttribute>(StringComparer.Ordinal);

        foreach (var (name, dataType) in typeRegistry)
        {
            if (!includeAll && !filter.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            if (!response.Headers.TryGetValues(name, out var values))
            {
                continue;
            }
            var raw = values.FirstOrDefault() ?? string.Empty;
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

    private static IReadOnlyDictionary<string, string> ParseAttrTypeRegistry(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues(AttrTypesHeader, out var values))
        {
            return new Dictionary<string, string>(0);
        }
        var raw = string.Join(",", values);
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

    // --- Parameter parsing helpers -------------------------------------

    private static HashSet<string>? ParseAttributeNames(SqsParseResult parsed, string prefix)
    {
        // Query protocol: AttributeName.1, AttributeName.2, ...
        // JSON protocol: AttributeNames: ["All"] (singular vs plural — SDKs use both).
        var set = new HashSet<string>(StringComparer.Ordinal);
        var dotPrefix = prefix + ".";
        foreach (var kv in parsed.Parameters)
        {
            if (kv.Key.StartsWith(dotPrefix, StringComparison.Ordinal) && !string.IsNullOrEmpty(kv.Value))
            {
                set.Add(kv.Value);
            }
        }
        if (parsed.Protocol == SqsWireProtocol.AwsJson && !string.IsNullOrEmpty(parsed.JsonBody))
        {
            try
            {
                using var doc = JsonDocument.Parse(parsed.JsonBody);
                var pluralName = prefix + "s";
                if (doc.RootElement.TryGetProperty(pluralName, out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var v in arr.EnumerateArray())
                    {
                        if (v.ValueKind == JsonValueKind.String)
                        {
                            var s = v.GetString();
                            if (!string.IsNullOrEmpty(s)) set.Add(s);
                        }
                    }
                }
            }
            catch (JsonException) { /* ignore — same protocol parser already validated body */ }
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
