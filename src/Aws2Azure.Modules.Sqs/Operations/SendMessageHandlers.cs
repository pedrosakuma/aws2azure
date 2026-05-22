using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
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
/// Slice-2 send dispatch: <c>SendMessage</c> and <c>SendMessageBatch</c>.
///
/// <para>Each SQS message is forwarded to Service Bus's runtime endpoint:</para>
/// <list type="bullet">
///   <item><c>SendMessage</c> → <c>POST /{queue}/messages</c> with the body
///         as the HTTP entity and a <c>BrokerProperties</c> header carrying
///         SQS system attributes (DeduplicationId, GroupId, DelaySeconds).</item>
///   <item><c>SendMessageBatch</c> → <c>POST /{queue}/messages</c> with
///         <c>Content-Type: application/vnd.microsoft.servicebus.json</c>
///         and a JSON array of envelopes.</item>
/// </list>
///
/// <para>Message attributes (key/value/type tuples) are preserved via two
/// parallel surfaces: each attribute becomes a raw SB application property
/// header (so receivers / non-SQS consumers can read it natively) and the
/// proxy stores the per-attribute SQS data type in a side header
/// <c>Aws2Azure-AttrTypes</c> so the slice-3 receive path can faithfully
/// reconstruct the original SQS <c>MessageAttributes</c> map.</para>
/// </summary>
internal static class SendMessageHandlers
{
    /// <summary>AWS SQS hard cap on a single SendMessage payload — body
    /// plus message attributes combined (1,048,576 bytes / 1 MiB; raised
    /// from 256 KiB in August 2025).</summary>
    public const int MaxBodyBytes = 1048576;

    /// <summary>AWS SQS hard cap on a SendMessageBatch — at most 10 entries.</summary>
    public const int MaxBatchEntries = 10;

    /// <summary>AWS SQS hard cap on aggregate batch payload — sum of every
    /// entry's body + attributes (1,048,576 bytes / 1 MiB).</summary>
    public const int MaxBatchBytes = 1048576;

    public const string AttrTypesHeader = "Aws2Azure-AttrTypes";

    /// <summary>
    /// SQS-faithful wire-size accounting: AWS counts the message body
    /// <em>plus</em> every message attribute (name UTF-8 bytes + data type
    /// UTF-8 bytes + value bytes — UTF-8 for String/Number, raw for Binary)
    /// against the 1 MiB cap. See the
    /// <a href="https://docs.aws.amazon.com/AWSSimpleQueueService/latest/SQSDeveloperGuide/quotas-messages.html">SQS
    /// message quotas</a> page (the cap was raised from 256 KiB to 1 MiB
    /// in August 2025). Larger payloads still require the AWS Extended
    /// Client Library, which stores the body in S3 and sends a small JSON
    /// pointer — that pointer flows through this proxy unchanged and
    /// resolves against the S3 module's Blob translation.
    /// </summary>
    internal static int ComputeWireSize(
        int bodyBytes,
        IReadOnlyDictionary<string, SqsMessageAttribute>? attributes)
    {
        var total = bodyBytes;
        if (attributes is null || attributes.Count == 0) return total;
        foreach (var (name, attr) in attributes)
        {
            total += Encoding.UTF8.GetByteCount(name);
            total += Encoding.UTF8.GetByteCount(attr.DataType);
            total += attr.IsBinary
                ? attr.BinaryValue.Length
                : Encoding.UTF8.GetByteCount(attr.StringValue ?? string.Empty);
        }
        return total;
    }

    public static Task HandleAsync(
        HttpContext context,
        SqsParseResult parsed,
        ServiceBusClient sb,
        CancellationToken ct) =>
        parsed.Operation switch
        {
            SqsOperation.SendMessage      => SendMessageAsync(context, parsed, sb, ct),
            SqsOperation.SendMessageBatch => SendMessageBatchAsync(context, parsed, sb, ct),
            _                              => WriteErrorAsync(context, parsed.Protocol,
                                                  SqsErrorMapping.NotImplemented(parsed.Operation)),
        };

    // --- SendMessage ---------------------------------------------------

    private static async Task SendMessageAsync(
        HttpContext context, SqsParseResult parsed, ServiceBusClient sb, CancellationToken ct)
    {
        var queueName = ExtractQueueName(parsed);
        if (queueName is null)
        {
            await WriteErrorAsync(context, parsed.Protocol,
                SqsErrorMapping.InvalidParameterValue("QueueUrl", "QueueUrl is required and must be a queue URL.")).ConfigureAwait(false);
            return;
        }

        if (!TryGetParam(parsed, "MessageBody", out var body))
        {
            await WriteErrorAsync(context, parsed.Protocol,
                SqsErrorMapping.MissingParameter("MessageBody")).ConfigureAwait(false);
            return;
        }
        var bodyBytes = Encoding.UTF8.GetBytes(body);

        if (!TryParseDelaySeconds(parsed, out var delaySeconds, out var delayError))
        {
            await WriteErrorAsync(context, parsed.Protocol, delayError!.Value).ConfigureAwait(false);
            return;
        }

        var attrResult = ParseMessageAttributes(parsed, "MessageAttribute");
        if (attrResult.IsError)
        {
            await WriteErrorAsync(context, parsed.Protocol,
                SqsErrorMapping.InvalidParameterValue("MessageAttributes", attrResult.ErrorMessage!)).ConfigureAwait(false);
            return;
        }
        var attrs = attrResult.Attributes ?? new Dictionary<string, SqsMessageAttribute>();

        if (ComputeWireSize(bodyBytes.Length, attrs) > MaxBodyBytes)
        {
            await WriteErrorAsync(context, parsed.Protocol, SqsErrorMapping.MessageTooLong()).ConfigureAwait(false);
            return;
        }

        TryGetParam(parsed, "MessageGroupId", out var groupId);
        TryGetParam(parsed, "MessageDeduplicationId", out var dedupId);

        var isFifoQueue = queueName.EndsWith(".fifo", StringComparison.Ordinal);
        if (isFifoQueue && string.IsNullOrEmpty(groupId))
        {
            await WriteErrorAsync(context, parsed.Protocol,
                SqsErrorMapping.MissingParameter("MessageGroupId")).ConfigureAwait(false);
            return;
        }
        if (isFifoQueue && string.IsNullOrEmpty(dedupId))
        {
            // AWS FIFO requires MessageDeduplicationId unless the queue is
            // configured with ContentBasedDeduplication=true. The proxy
            // doesn't track per-queue dedup configuration, so the safe and
            // AWS-compatible default is to reject. Auto-minting a random
            // GUID here would defeat SB's dedup window — every retry of
            // the same logical send would get a different MessageId — so
            // we never silently substitute.
            await WriteErrorAsync(context, parsed.Protocol,
                SqsErrorMapping.MissingParameter("MessageDeduplicationId")).ConfigureAwait(false);
            return;
        }
        if (!isFifoQueue && !string.IsNullOrEmpty(groupId))
        {
            await WriteErrorAsync(context, parsed.Protocol,
                SqsErrorMapping.InvalidParameterValue("MessageGroupId",
                    "MessageGroupId is only valid on FIFO queues.")).ConfigureAwait(false);
            return;
        }
        if (!isFifoQueue && !string.IsNullOrEmpty(dedupId))
        {
            await WriteErrorAsync(context, parsed.Protocol,
                SqsErrorMapping.InvalidParameterValue("MessageDeduplicationId",
                    "MessageDeduplicationId is only valid on FIFO queues.")).ConfigureAwait(false);
            return;
        }

        // Mint a stable idempotency key BEFORE entering the HTTP retry path
        // so every retry attempt of this logical send carries the same
        // SB MessageId. On queues with duplicate-detection enabled (Premium
        // SKU, or Standard via per-queue config) the broker collapses the
        // accidental duplicates into a single delivery; on others the
        // MessageId is at least stable across retries which clients can
        // use for their own dedup. For FIFO the caller-supplied dedup id
        // IS the MessageId (validated above to be present).
        var idempotencyKey = isFifoQueue ? dedupId! : Guid.NewGuid().ToString();

        var brokerProps = BuildBrokerProperties(messageId: idempotencyKey, sessionId: groupId, delaySeconds);
        var appHeaders = BuildAppPropertyHeaders(attrs);

        using var content = new ByteArrayContent(bodyBytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        using var response = await sb.SendMessageAsync(queueName, content, brokerProps, appHeaders, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            await WriteErrorAsync(context, parsed.Protocol, SqsErrorMapping.FromServiceBus(response)).ConfigureAwait(false);
            return;
        }

        // SB doesn't echo the MessageId — the SQS response carries the
        // idempotency key we minted above so AWS-SDK clients see a stable
        // MessageId even across silent broker-side dedup of retries.
        var md5OfBody = SqsMessageMd5.OfBody(bodyBytes);
        var md5OfAttrs = attrs.Count == 0 ? null : SqsMessageMd5.OfAttributes(attrs);

        await SqsResponseWriter.WriteSendMessageAsync(
            context, parsed.Protocol, idempotencyKey, md5OfBody, md5OfAttrs, sequenceNumber: null).ConfigureAwait(false);
    }

    // --- SendMessageBatch ----------------------------------------------

    private static async Task SendMessageBatchAsync(
        HttpContext context, SqsParseResult parsed, ServiceBusClient sb, CancellationToken ct)
    {
        var queueName = ExtractQueueName(parsed);
        if (queueName is null)
        {
            await WriteErrorAsync(context, parsed.Protocol,
                SqsErrorMapping.InvalidParameterValue("QueueUrl", "QueueUrl is required and must be a queue URL.")).ConfigureAwait(false);
            return;
        }

        var entries = parsed.Protocol == SqsWireProtocol.AwsJson
            ? ParseBatchEntriesJson(parsed.JsonBody)
            : ParseBatchEntriesQuery(parsed.Parameters);

        if (entries is null)
        {
            await WriteErrorAsync(context, parsed.Protocol,
                SqsErrorMapping.InvalidParameterValue("Entries", "Could not parse batch entries.")).ConfigureAwait(false);
            return;
        }
        if (entries.Count == 0)
        {
            await WriteErrorAsync(context, parsed.Protocol, SqsErrorMapping.EmptyBatchRequest()).ConfigureAwait(false);
            return;
        }
        if (entries.Count > MaxBatchEntries)
        {
            await WriteErrorAsync(context, parsed.Protocol,
                SqsErrorMapping.TooManyEntriesInBatchRequest(entries.Count)).ConfigureAwait(false);
            return;
        }

        // Validate ids: must be unique and follow the SQS character rules.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var totalBytes = 0;
        var batchIsFifoQueue = queueName.EndsWith(".fifo", StringComparison.Ordinal);
        foreach (var e in entries)
        {
            if (!IsValidBatchEntryId(e.Id))
            {
                await WriteErrorAsync(context, parsed.Protocol,
                    SqsErrorMapping.InvalidBatchEntryId(e.Id)).ConfigureAwait(false);
                return;
            }
            if (!seen.Add(e.Id))
            {
                await WriteErrorAsync(context, parsed.Protocol,
                    SqsErrorMapping.BatchEntryIdsNotDistinct(e.Id)).ConfigureAwait(false);
                return;
            }
            if (batchIsFifoQueue && string.IsNullOrEmpty(e.GroupId))
            {
                await WriteErrorAsync(context, parsed.Protocol,
                    SqsErrorMapping.MissingParameter("MessageGroupId")).ConfigureAwait(false);
                return;
            }
            if (batchIsFifoQueue && string.IsNullOrEmpty(e.DeduplicationId))
            {
                // Same rationale as the single-send path: never silently
                // substitute a random GUID for a missing dedup id on FIFO
                // — that would defeat SB's dedup window because retries
                // of the same logical send would each get a different
                // MessageId.
                await WriteErrorAsync(context, parsed.Protocol,
                    SqsErrorMapping.MissingParameter("MessageDeduplicationId")).ConfigureAwait(false);
                return;
            }
            if (!batchIsFifoQueue && !string.IsNullOrEmpty(e.GroupId))
            {
                await WriteErrorAsync(context, parsed.Protocol,
                    SqsErrorMapping.InvalidParameterValue("MessageGroupId",
                        "MessageGroupId is only valid on FIFO queues.")).ConfigureAwait(false);
                return;
            }
            if (!batchIsFifoQueue && !string.IsNullOrEmpty(e.DeduplicationId))
            {
                await WriteErrorAsync(context, parsed.Protocol,
                    SqsErrorMapping.InvalidParameterValue("MessageDeduplicationId",
                        "MessageDeduplicationId is only valid on FIFO queues.")).ConfigureAwait(false);
                return;
            }
            totalBytes += ComputeWireSize(e.BodyBytes.Length, e.Attributes);
        }
        if (totalBytes > MaxBatchBytes)
        {
            await WriteErrorAsync(context, parsed.Protocol,
                SqsErrorMapping.BatchRequestTooLong(totalBytes)).ConfigureAwait(false);
            return;
        }

        // Mint stable per-entry idempotency keys BEFORE the HTTP retry
        // path so every retry of the same batch carries the same
        // MessageId per entry. Same rationale as the single-send path;
        // FIFO entries are validated above to carry an explicit dedup id.
        var idempotencyKeys = new string[entries.Count];
        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            idempotencyKeys[i] = batchIsFifoQueue ? e.DeduplicationId! : Guid.NewGuid().ToString();
        }

        // Build the SB batch envelope.
        var batchJson = BuildSbBatchEnvelope(entries, idempotencyKeys);
        using var response = await sb.SendMessageBatchAsync(queueName, batchJson, ct).ConfigureAwait(false);

        // SB returns a single status for the whole batch. If anything failed
        // we mark every entry as failed with the upstream code — partial
        // success would require AMQP semantics SB REST does not expose.
        if (!response.IsSuccessStatusCode)
        {
            var mapping = SqsErrorMapping.FromServiceBus(response);
            var failed = new List<SendMessageBatchEntryError>(entries.Count);
            foreach (var e in entries)
            {
                failed.Add(new SendMessageBatchEntryError(
                    e.Id, mapping.Code, mapping.Message,
                    SenderFault: mapping.FaultType == SqsErrorResponse.FaultType.Sender));
            }
            await SqsResponseWriter.WriteSendMessageBatchAsync(
                context, parsed.Protocol, Array.Empty<SendMessageBatchEntryResult>(), failed).ConfigureAwait(false);
            return;
        }

        var successful = new List<SendMessageBatchEntryResult>(entries.Count);
        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            var messageId = idempotencyKeys[i];
            var md5OfBody = SqsMessageMd5.OfBody(e.BodyBytes);
            var md5OfAttrs = e.Attributes.Count == 0 ? null : SqsMessageMd5.OfAttributes(e.Attributes);
            successful.Add(new SendMessageBatchEntryResult(
                e.Id, messageId, md5OfBody, md5OfAttrs, SequenceNumber: null));
        }
        await SqsResponseWriter.WriteSendMessageBatchAsync(
            context, parsed.Protocol, successful, Array.Empty<SendMessageBatchEntryError>()).ConfigureAwait(false);
    }

    // --- Batch entry parsing -------------------------------------------

    internal sealed record BatchEntry(
        string Id, byte[] BodyBytes,
        string? GroupId, string? DeduplicationId, int DelaySeconds,
        Dictionary<string, SqsMessageAttribute> Attributes);

    internal static List<BatchEntry>? ParseBatchEntriesQuery(IReadOnlyDictionary<string, string> parameters)
    {
        // Keys look like SendMessageBatchRequestEntry.<i>.Id,
        // ...<i>.MessageBody, ...<i>.MessageGroupId,
        // ...<i>.MessageDeduplicationId, ...<i>.DelaySeconds,
        // ...<i>.MessageAttribute.<j>.<sub>.
        const string Prefix = "SendMessageBatchRequestEntry.";
        var groups = new SortedDictionary<int, Dictionary<string, string>>();
        foreach (var kv in parameters)
        {
            if (!kv.Key.StartsWith(Prefix, StringComparison.Ordinal)) continue;
            var rest = kv.Key.AsSpan(Prefix.Length);
            var dot = rest.IndexOf('.');
            if (dot <= 0) continue;
            if (!int.TryParse(rest[..dot], System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var idx))
                continue;
            var sub = rest[(dot + 1)..].ToString();
            if (!groups.TryGetValue(idx, out var bag))
            {
                bag = new Dictionary<string, string>(StringComparer.Ordinal);
                groups[idx] = bag;
            }
            bag[sub] = kv.Value;
        }
        if (groups.Count == 0) return new List<BatchEntry>();

        var entries = new List<BatchEntry>(groups.Count);
        foreach (var (idx, bag) in groups)
        {
            if (!bag.TryGetValue("Id", out var id)) return null;
            if (!bag.TryGetValue("MessageBody", out var body)) return null;
            bag.TryGetValue("MessageGroupId", out var groupId);
            bag.TryGetValue("MessageDeduplicationId", out var dedupId);

            var delay = 0;
            if (bag.TryGetValue("DelaySeconds", out var ds))
            {
                if (!int.TryParse(ds, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out var parsed) ||
                    parsed < 0 || parsed > 900)
                {
                    return null;
                }
                delay = parsed;
            }

            // Re-prefix attribute keys so SqsMessageAttributeParser.FromQuery
            // can chew them with its existing prefix machinery.
            var subParams = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var sub in bag)
            {
                if (sub.Key.StartsWith("MessageAttribute.", StringComparison.Ordinal))
                {
                    subParams[sub.Key] = sub.Value;
                }
            }
            var attrResult = SqsMessageAttributeParser.FromQuery(subParams, "MessageAttribute");
            if (attrResult.IsError) return null;

            entries.Add(new BatchEntry(
                id, Encoding.UTF8.GetBytes(body), groupId, dedupId, delay,
                new Dictionary<string, SqsMessageAttribute>(attrResult.Attributes!, StringComparer.Ordinal)));
        }
        return entries;
    }

    internal static List<BatchEntry>? ParseBatchEntriesJson(string? jsonBody)
    {
        if (string.IsNullOrEmpty(jsonBody)) return null;
        try
        {
            using var doc = JsonDocument.Parse(jsonBody);
            if (!doc.RootElement.TryGetProperty("Entries", out var entriesEl) ||
                entriesEl.ValueKind != JsonValueKind.Array)
            {
                return null;
            }
            var list = new List<BatchEntry>(entriesEl.GetArrayLength());
            foreach (var e in entriesEl.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) return null;
                if (!e.TryGetProperty("Id", out var idEl) || idEl.ValueKind != JsonValueKind.String) return null;
                if (!e.TryGetProperty("MessageBody", out var bodyEl) || bodyEl.ValueKind != JsonValueKind.String) return null;
                var id = idEl.GetString()!;
                var body = bodyEl.GetString()!;
                string? groupId = null, dedupId = null;
                var delay = 0;
                if (e.TryGetProperty("MessageGroupId", out var gEl) && gEl.ValueKind == JsonValueKind.String) groupId = gEl.GetString();
                if (e.TryGetProperty("MessageDeduplicationId", out var dEl) && dEl.ValueKind == JsonValueKind.String) dedupId = dEl.GetString();
                if (e.TryGetProperty("DelaySeconds", out var delayEl))
                {
                    if (delayEl.ValueKind != JsonValueKind.Number || !delayEl.TryGetInt32(out delay) ||
                        delay < 0 || delay > 900)
                    {
                        return null;
                    }
                }

                var attrs = new Dictionary<string, SqsMessageAttribute>(StringComparer.Ordinal);
                if (e.TryGetProperty("MessageAttributes", out var mAttrs))
                {
                    var attrResult = SqsMessageAttributeParser.FromJson(mAttrs);
                    if (attrResult.IsError) return null;
                    foreach (var kv in attrResult.Attributes!) attrs[kv.Key] = kv.Value;
                }

                list.Add(new BatchEntry(id, Encoding.UTF8.GetBytes(body), groupId, dedupId, delay, attrs));
            }
            return list;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // --- SB envelope builders ------------------------------------------

    private static string BuildBrokerProperties(string? messageId, string? sessionId, int delaySeconds)
    {
        var hasMessageId = !string.IsNullOrEmpty(messageId);
        var hasSession = !string.IsNullOrEmpty(sessionId);
        var hasDelay = delaySeconds > 0;
        if (!hasMessageId && !hasSession && !hasDelay) return string.Empty;

        var sb = new StringBuilder("{");
        var first = true;
        if (hasMessageId)
        {
            AppendStringProp(sb, ref first, "MessageId", messageId!);
        }
        if (hasSession)
        {
            AppendStringProp(sb, ref first, "SessionId", sessionId!);
        }
        if (hasDelay)
        {
            var enqueueAt = DateTimeOffset.UtcNow.AddSeconds(delaySeconds).ToString("yyyy-MM-ddTHH:mm:ssZ",
                System.Globalization.CultureInfo.InvariantCulture);
            AppendStringProp(sb, ref first, "ScheduledEnqueueTimeUtc", enqueueAt);
        }
        sb.Append('}');
        return sb.ToString();
    }

    private static void AppendStringProp(StringBuilder sb, ref bool first, string name, string value)
    {
        if (!first) sb.Append(',');
        first = false;
        sb.Append('"').Append(JsonEncodedText.Encode(name)).Append("\":\"")
          .Append(JsonEncodedText.Encode(value)).Append('"');
    }

    private static IReadOnlyDictionary<string, string>? BuildAppPropertyHeaders(
        IReadOnlyDictionary<string, SqsMessageAttribute> attrs)
    {
        if (attrs.Count == 0) return null;

        // Each SQS attribute becomes a single application-property HTTP
        // header. Binary attributes are base64-encoded so the value is
        // header-safe. The data-type registry is sent on the
        // Aws2Azure-AttrTypes side channel so the receive path can
        // round-trip the original SQS view without ambiguity.
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        var typeRegistry = new StringBuilder();
        var first = true;
        foreach (var kv in attrs)
        {
            var name = kv.Key;
            var attr = kv.Value;
            var headerValue = attr.IsBinary
                ? Convert.ToBase64String(attr.BinaryValue.Span)
                : (attr.StringValue ?? string.Empty);
            headers[name] = headerValue;

            if (!first) typeRegistry.Append(',');
            first = false;
            typeRegistry.Append(name).Append('=').Append(attr.DataType);
        }
        headers[AttrTypesHeader] = typeRegistry.ToString();
        return headers;
    }

    private static string BuildSbBatchEnvelope(IReadOnlyList<BatchEntry> entries, IReadOnlyList<string> messageIds)
    {
        // Service Bus expects an array of:
        //   { "Body": "...", "BrokerProperties": {...}, "UserProperties": {...} }
        // We embed bodies as UTF-8 strings (SQS bodies are always text).
        var sb = new StringBuilder("[");
        for (var i = 0; i < entries.Count; i++)
        {
            if (i > 0) sb.Append(',');
            var e = entries[i];
            sb.Append('{');
            sb.Append("\"Body\":\"").Append(JsonEncodedText.Encode(Encoding.UTF8.GetString(e.BodyBytes))).Append("\",");

            // BrokerProperties as a JSON sub-object string (per SB REST spec).
            // Inject the per-entry idempotency key as MessageId so retries
            // of the batch don't double-publish entries on dedup-enabled
            // queues. For FIFO with a caller-supplied DeduplicationId this
            // collapses to the dedup id (same value).
            var bp = BuildBrokerProperties(messageIds[i], e.GroupId, e.DelaySeconds);
            if (!string.IsNullOrEmpty(bp))
            {
                sb.Append("\"BrokerProperties\":").Append(bp).Append(',');
            }

            sb.Append("\"UserProperties\":{");
            var firstProp = true;
            foreach (var kv in e.Attributes)
            {
                if (!firstProp) sb.Append(',');
                firstProp = false;
                var headerValue = kv.Value.IsBinary
                    ? Convert.ToBase64String(kv.Value.BinaryValue.Span)
                    : (kv.Value.StringValue ?? string.Empty);
                sb.Append('"').Append(JsonEncodedText.Encode(kv.Key)).Append("\":\"")
                  .Append(JsonEncodedText.Encode(headerValue)).Append('"');
            }
            if (e.Attributes.Count > 0)
            {
                sb.Append(',');
            }
            var typeRegistry = new StringBuilder();
            var firstReg = true;
            foreach (var kv in e.Attributes)
            {
                if (!firstReg) typeRegistry.Append(',');
                firstReg = false;
                typeRegistry.Append(kv.Key).Append('=').Append(kv.Value.DataType);
            }
            if (typeRegistry.Length > 0)
            {
                sb.Append('"').Append(AttrTypesHeader).Append("\":\"")
                  .Append(JsonEncodedText.Encode(typeRegistry.ToString())).Append('"');
            }
            sb.Append('}'); // UserProperties
            sb.Append('}');
        }
        sb.Append(']');
        return sb.ToString();
    }

    // --- Misc utilities ------------------------------------------------

    internal static string? ExtractQueueName(SqsParseResult parsed) =>
        parsed.Parameters.TryGetValue("QueueUrl", out var url) ? QueueUrlBuilder.ExtractQueueName(url) : null;

    internal static bool TryGetParam(SqsParseResult parsed, string key, out string value)
    {
        if (parsed.Parameters.TryGetValue(key, out var v) && v is not null)
        {
            value = v;
            return true;
        }
        value = string.Empty;
        return false;
    }

    internal static bool TryParseDelaySeconds(
        SqsParseResult parsed, out int delaySeconds, out SqsErrorMapping.Mapping? error)
    {
        delaySeconds = 0;
        error = null;
        if (!parsed.Parameters.TryGetValue("DelaySeconds", out var raw) || string.IsNullOrEmpty(raw))
        {
            return true;
        }
        if (!int.TryParse(raw, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var v) || v < 0 || v > 900)
        {
            error = SqsErrorMapping.InvalidParameterValue("DelaySeconds",
                "DelaySeconds must be an integer 0..900.");
            return false;
        }
        delaySeconds = v;
        return true;
    }

    internal static SqsMessageAttributeParser.ParseResult ParseMessageAttributes(
        SqsParseResult parsed, string prefix)
    {
        if (parsed.Protocol == SqsWireProtocol.AwsJson && !string.IsNullOrEmpty(parsed.JsonBody))
        {
            try
            {
                using var doc = JsonDocument.Parse(parsed.JsonBody);
                if (doc.RootElement.TryGetProperty("MessageAttributes", out var attrs))
                {
                    return SqsMessageAttributeParser.FromJson(attrs);
                }
                return new SqsMessageAttributeParser.ParseResult(
                    new Dictionary<string, SqsMessageAttribute>(StringComparer.Ordinal), null, null);
            }
            catch (JsonException ex)
            {
                return new SqsMessageAttributeParser.ParseResult(null, "InvalidParameterValue", ex.Message);
            }
        }
        return SqsMessageAttributeParser.FromQuery(parsed.Parameters, prefix);
    }

    internal static bool IsValidBatchEntryId(string id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 80) return false;
        foreach (var c in id)
        {
            if (!(char.IsLetterOrDigit(c) || c is '-' or '_')) return false;
        }
        return true;
    }

    internal static Task WriteErrorAsync(HttpContext context, SqsWireProtocol protocol, SqsErrorMapping.Mapping mapping) =>
        SqsErrorResponse.WriteAsync(context, protocol, mapping.StatusCode, mapping.Code, mapping.Message, mapping.FaultType);
}
