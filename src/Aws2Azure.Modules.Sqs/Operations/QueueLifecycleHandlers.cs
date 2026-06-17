using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Aws2Azure.Modules.Sqs.Errors;
using Aws2Azure.Modules.Sqs.Internal;
using Aws2Azure.Modules.Sqs.WireProtocol;
using Aws2Azure.Modules.Sqs.Xml;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.Sqs.Operations;

/// <summary>
/// Slice-1 queue-lifecycle dispatch:
/// <c>CreateQueue</c>, <c>DeleteQueue</c>, <c>ListQueues</c>,
/// <c>GetQueueUrl</c>, <c>GetQueueAttributes</c>.
/// </summary>
internal static class QueueLifecycleHandlers
{
    /// <summary>Per-page cap Service Bus management API enforces.</summary>
    private const int SbPageSize = 100;

    /// <summary>SQS-imposed max for ListQueues (default + cap).</summary>
    private const int SqsListQueuesMax = 1000;

    public static Task HandleAsync(
        HttpContext context,
        SqsParseResult parsed,
        ServiceBusClient sb,
        CancellationToken ct) =>
        parsed.Operation switch
        {
            SqsOperation.CreateQueue          => CreateQueueAsync(context, parsed, sb, ct),
            SqsOperation.DeleteQueue          => DeleteQueueAsync(context, parsed, sb, ct),
            SqsOperation.GetQueueUrl          => GetQueueUrlAsync(context, parsed, sb, ct),
            SqsOperation.ListQueues           => ListQueuesAsync(context, parsed, sb, ct),
            SqsOperation.GetQueueAttributes   => GetQueueAttributesAsync(context, parsed, sb, ct),
            _                                 => WriteErrorAsync(context, parsed.Protocol,
                                                    SqsErrorMapping.NotImplemented(parsed.Operation)),
        };

    // --- CreateQueue ---------------------------------------------------

    private static async Task CreateQueueAsync(
        HttpContext context, SqsParseResult parsed, ServiceBusClient sb, CancellationToken ct)
    {
        if (!parsed.Parameters.TryGetValue("QueueName", out var queueName) || string.IsNullOrEmpty(queueName))
        {
            await WriteErrorAsync(context, parsed.Protocol,
                SqsErrorMapping.InvalidParameterValue("QueueName", "QueueName is required.")).ConfigureAwait(false);
            return;
        }
        if (!QueueName.IsValid(queueName))
        {
            await WriteErrorAsync(context, parsed.Protocol, SqsErrorMapping.QueueNameInvalid()).ConfigureAwait(false);
            return;
        }

        var attributes = SqsQueueAttributeParser.ExtractAttributes(parsed, "Attribute", contiguousQueryIndexes: true);
        var err = QueueAttributeTranslator.ToServiceBusProperties(queueName, attributes, out var props);
        if (err.IsError)
        {
            var mapping = err.Kind == QueueAttributeTranslator.QueueAttributeError.UnknownAttribute
                ? SqsErrorMapping.InvalidAttributeName(err.AttributeName)
                : SqsErrorMapping.InvalidAttributeValue(err.AttributeName, err.Message);
            await WriteErrorAsync(context, parsed.Protocol, mapping).ConfigureAwait(false);
            return;
        }

        var atomBody = AtomQueueXmlWriter.BuildQueueEntry(props);

        using (var response = await sb.CreateQueueAsync(queueName, atomBody, ct).ConfigureAwait(false))
        {
            if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                // SQS semantics: same name + same attributes = idempotent
                // success returning the existing URL; different attributes
                // = QueueNameExists. SB doesn't distinguish on the create
                // path, so we follow up with a GET to compare.
                if (await ExistingQueueMatchesAsync(sb, queueName, props, ct).ConfigureAwait(false))
                {
                    await SqsResponseWriter.WriteCreateQueueAsync(context, parsed.Protocol,
                        QueueUrlBuilder.Build(context, queueName)).ConfigureAwait(false);
                    return;
                }
                await WriteErrorAsync(context, parsed.Protocol, new SqsErrorMapping.Mapping(
                    StatusCodes.Status400BadRequest, "QueueNameExists",
                    "A queue with this name already exists but with different attributes.")).ConfigureAwait(false);
                return;
            }
            if (!response.IsSuccessStatusCode)
            {
                await WriteErrorAsync(context, parsed.Protocol,
                    SqsErrorMapping.FromServiceBus(response)).ConfigureAwait(false);
                return;
            }
        }

        await SqsResponseWriter.WriteCreateQueueAsync(context, parsed.Protocol,
            QueueUrlBuilder.Build(context, queueName)).ConfigureAwait(false);
    }

    private static async Task<bool> ExistingQueueMatchesAsync(
        ServiceBusClient sb, string queueName, QueueDescriptionProperties requested, CancellationToken ct)
    {
        using var response = await sb.GetQueueAsync(queueName, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return false;
        var xml = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var entry = AtomQueueXmlReader.ParseQueueEntry(xml);
        if (entry is null) return false;

        return SecondsClose(requested.LockDurationSeconds,
                            entry.Properties.LockDurationSeconds,
                            QueueAttributeTranslator.DefaultVisibilityTimeoutSeconds)
            && SecondsClose(requested.DefaultMessageTimeToLiveSeconds,
                            entry.Properties.DefaultMessageTimeToLiveSeconds,
                            QueueAttributeTranslator.DefaultMessageRetentionPeriodSeconds)
            && MaxMessageSizeMatches(requested.MaxMessageSizeBytes, entry.Properties.MaxMessageSizeBytes)
            && (requested.RequiresSession ?? false) == (entry.Properties.RequiresSession ?? false)
            && (requested.RequiresDuplicateDetection ?? false) == (entry.Properties.RequiresDuplicateDetection ?? false);
    }

    /// <summary>
    /// SQS expresses MaximumMessageSize in bytes; Service Bus stores
    /// MaxMessageSizeInKilobytes rounded up. To make the idempotency
    /// compare lossless we normalise both sides to KiB before comparing,
    /// matching the value the writer would emit if we re-created the queue.
    /// </summary>
    private static bool MaxMessageSizeMatches(int? requestedBytes, int? existingBytes)
    {
        var reqB = requestedBytes ?? QueueAttributeTranslator.DefaultMaximumMessageSizeBytes;
        var exB = existingBytes ?? QueueAttributeTranslator.DefaultMaximumMessageSizeBytes;
        var reqKb = (int)Math.Ceiling(reqB / 1024.0);
        var exKb = (int)Math.Ceiling(exB / 1024.0);
        return reqKb == exKb;
    }

    private static bool SecondsClose(double? a, double? b, double defaultValue)
    {
        var aa = a ?? defaultValue;
        var bb = b ?? defaultValue;
        return Math.Abs(aa - bb) < 1;
    }

    // --- DeleteQueue ---------------------------------------------------

    private static async Task DeleteQueueAsync(
        HttpContext context, SqsParseResult parsed, ServiceBusClient sb, CancellationToken ct)
    {
        var queueName = ResolveQueueNameOrNull(parsed);
        if (queueName is null)
        {
            await WriteErrorAsync(context, parsed.Protocol,
                SqsErrorMapping.InvalidParameterValue("QueueUrl", "QueueUrl is required.")).ConfigureAwait(false);
            return;
        }
        if (!QueueName.IsValid(queueName))
        {
            await WriteErrorAsync(context, parsed.Protocol, SqsErrorMapping.QueueNameInvalid()).ConfigureAwait(false);
            return;
        }

        using var response = await sb.DeleteQueueAsync(queueName, ct).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            await WriteErrorAsync(context, parsed.Protocol, SqsErrorMapping.QueueDoesNotExist()).ConfigureAwait(false);
            return;
        }
        if (!response.IsSuccessStatusCode)
        {
            await WriteErrorAsync(context, parsed.Protocol,
                SqsErrorMapping.FromServiceBus(response)).ConfigureAwait(false);
            return;
        }
        await SqsResponseWriter.WriteDeleteQueueAsync(context, parsed.Protocol).ConfigureAwait(false);
    }

    // --- GetQueueUrl ---------------------------------------------------

    private static async Task GetQueueUrlAsync(
        HttpContext context, SqsParseResult parsed, ServiceBusClient sb, CancellationToken ct)
    {
        if (!parsed.Parameters.TryGetValue("QueueName", out var queueName) || string.IsNullOrEmpty(queueName))
        {
            await WriteErrorAsync(context, parsed.Protocol,
                SqsErrorMapping.InvalidParameterValue("QueueName", "QueueName is required.")).ConfigureAwait(false);
            return;
        }
        if (!QueueName.IsValid(queueName))
        {
            await WriteErrorAsync(context, parsed.Protocol, SqsErrorMapping.QueueNameInvalid()).ConfigureAwait(false);
            return;
        }

        // SQS GetQueueUrl returns NonExistentQueue when the queue is unknown,
        // so we verify against SB before synthesising the URL.
        using (var response = await sb.GetQueueAsync(queueName, ct).ConfigureAwait(false))
        {
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                await WriteErrorAsync(context, parsed.Protocol, SqsErrorMapping.QueueDoesNotExist()).ConfigureAwait(false);
                return;
            }
            if (!response.IsSuccessStatusCode)
            {
                await WriteErrorAsync(context, parsed.Protocol,
                    SqsErrorMapping.FromServiceBus(response)).ConfigureAwait(false);
                return;
            }
        }

        await SqsResponseWriter.WriteGetQueueUrlAsync(context, parsed.Protocol,
            QueueUrlBuilder.Build(context, queueName)).ConfigureAwait(false);
    }

    // --- ListQueues ----------------------------------------------------

    private static async Task ListQueuesAsync(
        HttpContext context, SqsParseResult parsed, ServiceBusClient sb, CancellationToken ct)
    {
        parsed.Parameters.TryGetValue("QueueNamePrefix", out var prefix);
        var maxResults = ParseMaxResults(parsed);
        var skip = ParseNextToken(parsed);

        var collected = new List<string>(Math.Min(maxResults, 64));
        var pageSize = Math.Min(SbPageSize, maxResults);
        var queriedSkip = skip;

        while (collected.Count < maxResults)
        {
            using var response = await sb.ListQueuesAsync(queriedSkip, pageSize, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                await WriteErrorAsync(context, parsed.Protocol,
                    SqsErrorMapping.FromServiceBus(response)).ConfigureAwait(false);
                return;
            }
            var xml = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var entries = AtomQueueXmlReader.ParseQueueFeed(xml);
            if (entries.Count == 0) break;

            // Track entries actually consumed from this page so the cursor
            // doesn't jump past prefix-matched-but-unreturned queues when we
            // stop mid-page on MaxResults.
            var consumedFromPage = 0;
            var hitCap = false;
            foreach (var entry in entries)
            {
                consumedFromPage++;
                if (!string.IsNullOrEmpty(prefix) &&
                    !entry.Name.StartsWith(prefix, StringComparison.Ordinal))
                {
                    continue;
                }
                collected.Add(QueueUrlBuilder.Build(context, entry.Name));
                if (collected.Count >= maxResults)
                {
                    hitCap = true;
                    break;
                }
            }
            queriedSkip += consumedFromPage;
            if (hitCap) break;
            if (entries.Count < pageSize) break;
        }

        string? nextToken = null;
        if (collected.Count >= maxResults)
        {
            // Confirm there is at least one more queue before issuing a token.
            using var probe = await sb.ListQueuesAsync(queriedSkip, 1, ct).ConfigureAwait(false);
            if (probe.IsSuccessStatusCode)
            {
                var probeXml = await probe.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (AtomQueueXmlReader.ParseQueueFeed(probeXml).Count > 0)
                {
                    nextToken = queriedSkip.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
            }
        }

        await SqsResponseWriter.WriteListQueuesAsync(context, parsed.Protocol, collected, nextToken).ConfigureAwait(false);
    }

    private static int ParseMaxResults(SqsParseResult parsed)
    {
        if (parsed.Parameters.TryGetValue("MaxResults", out var raw) &&
            int.TryParse(raw, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var max) &&
            max > 0)
        {
            return Math.Min(max, SqsListQueuesMax);
        }
        return SqsListQueuesMax;
    }

    private static int ParseNextToken(SqsParseResult parsed)
    {
        if (parsed.Parameters.TryGetValue("NextToken", out var raw) &&
            int.TryParse(raw, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var skip) &&
            skip >= 0)
        {
            return skip;
        }
        return 0;
    }

    // --- GetQueueAttributes -------------------------------------------

    private static async Task GetQueueAttributesAsync(
        HttpContext context, SqsParseResult parsed, ServiceBusClient sb, CancellationToken ct)
    {
        var queueName = ResolveQueueNameOrNull(parsed);
        if (queueName is null)
        {
            await WriteErrorAsync(context, parsed.Protocol,
                SqsErrorMapping.InvalidParameterValue("QueueUrl", "QueueUrl is required.")).ConfigureAwait(false);
            return;
        }
        if (!QueueName.IsValid(queueName))
        {
            await WriteErrorAsync(context, parsed.Protocol, SqsErrorMapping.QueueNameInvalid()).ConfigureAwait(false);
            return;
        }

        using var response = await sb.GetQueueAsync(queueName, ct).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            await WriteErrorAsync(context, parsed.Protocol, SqsErrorMapping.QueueDoesNotExist()).ConfigureAwait(false);
            return;
        }
        if (!response.IsSuccessStatusCode)
        {
            await WriteErrorAsync(context, parsed.Protocol,
                SqsErrorMapping.FromServiceBus(response)).ConfigureAwait(false);
            return;
        }
        var xml = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var entry = AtomQueueXmlReader.ParseQueueEntry(xml);
        if (entry is null)
        {
            await WriteErrorAsync(context, parsed.Protocol,
                SqsErrorMapping.InternalError("aws2azure: failed to parse Service Bus queue description."))
                .ConfigureAwait(false);
            return;
        }

        var requested = ExtractAttributeNames(parsed, "AttributeName");
        var allAttributes = QueueAttributeTranslator.ToSqsAttributes(entry.Properties);
        var filtered = FilterAttributes(allAttributes, requested);

        await SqsResponseWriter.WriteGetQueueAttributesAsync(context, parsed.Protocol, filtered)
            .ConfigureAwait(false);
    }

    private static IReadOnlyDictionary<string, string> FilterAttributes(
        IReadOnlyDictionary<string, string> all, IReadOnlyList<string> requested)
    {
        // SQS spec: empty list OR "All" returns everything.
        var includeAll = requested.Count == 0;
        for (var i = 0; i < requested.Count && !includeAll; i++)
        {
            if (string.Equals(requested[i], "All", StringComparison.Ordinal)) includeAll = true;
        }
        if (includeAll) return all;

        var filtered = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < requested.Count; i++)
        {
            if (all.TryGetValue(requested[i], out var v))
            {
                filtered[requested[i]] = v;
            }
        }
        return filtered;
    }

    // --- shared helpers ----------------------------------------------

    private static string? ResolveQueueNameOrNull(SqsParseResult parsed)
    {
        if (parsed.Parameters.TryGetValue("QueueUrl", out var url) && !string.IsNullOrEmpty(url))
        {
            return QueueUrlBuilder.ExtractQueueName(url) ?? url;
        }
        return null;
    }

    /// <summary>
    /// Reads SQS's flat AttributeName.N indexed members (Query) or the
    /// AttributeNames list (AWS JSON) into a plain string list.
    /// </summary>
    private static IReadOnlyList<string> ExtractAttributeNames(SqsParseResult parsed, string memberName)
    {
        var list = new List<string>();
        if (parsed.JsonBody is not null)
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(parsed.JsonBody);
                if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("AttributeNames", out var arr)
                    && arr.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var item in arr.EnumerateArray())
                    {
                        if (item.ValueKind == System.Text.Json.JsonValueKind.String)
                        {
                            var s = item.GetString();
                            if (!string.IsNullOrEmpty(s)) list.Add(s);
                        }
                    }
                    if (list.Count > 0) return list;
                }
            }
            catch (System.Text.Json.JsonException) { }
        }

        // Query protocol form: AttributeName.1, AttributeName.2, ...
        var i = 1;
        while (parsed.Parameters.TryGetValue($"{memberName}.{i}", out var v))
        {
            if (!string.IsNullOrEmpty(v)) list.Add(v);
            i++;
        }
        return list;
    }

    private static Task WriteErrorAsync(HttpContext context, SqsWireProtocol protocol, SqsErrorMapping.Mapping mapping) =>
        SqsParameterHelpers.WriteErrorAsync(context, protocol, mapping);
}
