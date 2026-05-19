using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Aws2Azure.Modules.Sqs.Errors;
using Aws2Azure.Modules.Sqs.Internal;
using Aws2Azure.Modules.Sqs.WireProtocol;
using Aws2Azure.Modules.Sqs.Xml;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.Sqs.Operations;

/// <summary>
/// Slice-5 "long-tail" handlers: <c>ListDeadLetterSourceQueues</c> (real
/// implementation backed by the Service Bus queue metadata), plus the
/// tagging and permission ops which Service Bus has no native equivalent
/// for and are therefore implemented as queue-existence-validated stubs.
/// </summary>
internal static class TailHandlers
{
    private const int SbPageSize = 100;

    public static Task HandleAsync(
        HttpContext context,
        SqsParseResult parsed,
        ServiceBusClient sb,
        CancellationToken ct) =>
        parsed.Operation switch
        {
            SqsOperation.ListDeadLetterSourceQueues => ListDeadLetterSourceQueuesAsync(context, parsed, sb, ct),
            SqsOperation.ListQueueTags              => ListQueueTagsAsync(context, parsed, sb, ct),
            SqsOperation.TagQueue                   => TagQueueAsync(context, parsed, sb, ct),
            SqsOperation.UntagQueue                 => UntagQueueAsync(context, parsed, sb, ct),
            SqsOperation.AddPermission              => AddPermissionAsync(context, parsed, sb, ct),
            SqsOperation.RemovePermission           => RemovePermissionAsync(context, parsed, sb, ct),
            _                                       => WriteErrorAsync(context, parsed.Protocol,
                                                          SqsErrorMapping.NotImplemented(parsed.Operation)),
        };

    // --- ListDeadLetterSourceQueues -------------------------------------

    /// <summary>SQS hard cap on MaxResults for ListDeadLetterSourceQueues.</summary>
    private const int MaxResultsCap = 1000;

    private static async Task ListDeadLetterSourceQueuesAsync(
        HttpContext context, SqsParseResult parsed, ServiceBusClient sb, CancellationToken ct)
    {
        var targetName = ResolveQueueNameOrNull(parsed);
        if (targetName is null || !QueueName.IsValid(targetName))
        {
            await WriteErrorAsync(context, parsed.Protocol,
                SqsErrorMapping.InvalidParameterValue("QueueUrl",
                    "QueueUrl is required and must reference a valid queue.")).ConfigureAwait(false);
            return;
        }

        var maxResults = ParseMaxResults(parsed);
        var skip = ParseNextToken(parsed);

        // SQS returns NonExistentQueue when the target DLQ itself isn't a queue.
        using (var probe = await sb.GetQueueAsync(targetName, ct).ConfigureAwait(false))
        {
            if (probe.StatusCode == HttpStatusCode.NotFound)
            {
                await WriteErrorAsync(context, parsed.Protocol, SqsErrorMapping.QueueDoesNotExist()).ConfigureAwait(false);
                return;
            }
            if (!probe.IsSuccessStatusCode)
            {
                await WriteErrorAsync(context, parsed.Protocol, SqsErrorMapping.FromServiceBus(probe)).ConfigureAwait(false);
                return;
            }
        }

        // Walk SB queues from the supplied skip cursor; emit those whose
        // ForwardDeadLetteredMessagesTo names the target. Each SB page is
        // capped at 100; we keep paging until we have MaxResults matches or
        // observe a short page (end of namespace).
        var sources = new List<string>(Math.Min(maxResults, 64));
        var queriedSkip = skip;
        var hitCap = false;
        while (sources.Count < maxResults)
        {
            using var response = await sb.ListQueuesAsync(queriedSkip, SbPageSize, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                await WriteErrorAsync(context, parsed.Protocol, SqsErrorMapping.FromServiceBus(response)).ConfigureAwait(false);
                return;
            }
            var xml = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var entries = AtomQueueXmlReader.ParseQueueFeed(xml);
            if (entries.Count == 0) break;

            var consumedFromPage = 0;
            foreach (var entry in entries)
            {
                consumedFromPage++;
                if (!string.Equals(entry.Properties.ForwardDeadLetteredMessagesTo, targetName, StringComparison.Ordinal))
                {
                    continue;
                }
                sources.Add(QueueUrlBuilder.Build(context, entry.Name));
                if (sources.Count >= maxResults)
                {
                    hitCap = true;
                    break;
                }
            }
            queriedSkip += consumedFromPage;
            if (hitCap) break;
            if (entries.Count < SbPageSize) break;
        }

        // Only emit NextToken when there is at least one more SB queue past
        // the cursor — otherwise clients would see a never-empty cursor.
        string? nextToken = null;
        if (hitCap)
        {
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

        await SqsResponseWriter.WriteListDeadLetterSourceQueuesAsync(
            context, parsed.Protocol, sources, nextToken).ConfigureAwait(false);
    }

    private static int ParseMaxResults(SqsParseResult parsed)
    {
        if (parsed.Parameters.TryGetValue("MaxResults", out var raw) &&
            int.TryParse(raw, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var max) &&
            max > 0)
        {
            return Math.Min(max, MaxResultsCap);
        }
        return MaxResultsCap;
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

    // --- Tag stubs ------------------------------------------------------

    private static async Task ListQueueTagsAsync(
        HttpContext context, SqsParseResult parsed, ServiceBusClient sb, CancellationToken ct)
    {
        if (await EnsureQueueExistsAsync(context, parsed, sb, ct).ConfigureAwait(false) is false) return;
        await SqsResponseWriter.WriteListQueueTagsAsync(context, parsed.Protocol,
            new Dictionary<string, string>(StringComparer.Ordinal)).ConfigureAwait(false);
    }

    private static async Task TagQueueAsync(
        HttpContext context, SqsParseResult parsed, ServiceBusClient sb, CancellationToken ct)
    {
        if (await EnsureQueueExistsAsync(context, parsed, sb, ct).ConfigureAwait(false) is false) return;
        await SqsResponseWriter.WriteTagQueueAsync(context, parsed.Protocol).ConfigureAwait(false);
    }

    private static async Task UntagQueueAsync(
        HttpContext context, SqsParseResult parsed, ServiceBusClient sb, CancellationToken ct)
    {
        if (await EnsureQueueExistsAsync(context, parsed, sb, ct).ConfigureAwait(false) is false) return;
        await SqsResponseWriter.WriteUntagQueueAsync(context, parsed.Protocol).ConfigureAwait(false);
    }

    // --- Permission stubs (ownership-only) ------------------------------

    private static async Task AddPermissionAsync(
        HttpContext context, SqsParseResult parsed, ServiceBusClient sb, CancellationToken ct)
    {
        if (await EnsureQueueExistsAsync(context, parsed, sb, ct).ConfigureAwait(false) is false) return;
        await SqsResponseWriter.WriteAddPermissionAsync(context, parsed.Protocol).ConfigureAwait(false);
    }

    private static async Task RemovePermissionAsync(
        HttpContext context, SqsParseResult parsed, ServiceBusClient sb, CancellationToken ct)
    {
        if (await EnsureQueueExistsAsync(context, parsed, sb, ct).ConfigureAwait(false) is false) return;
        await SqsResponseWriter.WriteRemovePermissionAsync(context, parsed.Protocol).ConfigureAwait(false);
    }

    // --- shared helpers -------------------------------------------------

    /// <summary>
    /// Validates the QueueUrl param and confirms the underlying SB queue
    /// exists. Writes the appropriate error response and returns false on
    /// any failure; returns true to signal the caller can proceed.
    /// </summary>
    private static async Task<bool> EnsureQueueExistsAsync(
        HttpContext context, SqsParseResult parsed, ServiceBusClient sb, CancellationToken ct)
    {
        var queueName = ResolveQueueNameOrNull(parsed);
        if (queueName is null)
        {
            await WriteErrorAsync(context, parsed.Protocol,
                SqsErrorMapping.InvalidParameterValue("QueueUrl", "QueueUrl is required.")).ConfigureAwait(false);
            return false;
        }
        if (!QueueName.IsValid(queueName))
        {
            await WriteErrorAsync(context, parsed.Protocol, SqsErrorMapping.QueueNameInvalid()).ConfigureAwait(false);
            return false;
        }

        using var response = await sb.GetQueueAsync(queueName, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            await WriteErrorAsync(context, parsed.Protocol, SqsErrorMapping.QueueDoesNotExist()).ConfigureAwait(false);
            return false;
        }
        if (!response.IsSuccessStatusCode)
        {
            await WriteErrorAsync(context, parsed.Protocol, SqsErrorMapping.FromServiceBus(response)).ConfigureAwait(false);
            return false;
        }
        return true;
    }

    private static string? ResolveQueueNameOrNull(SqsParseResult parsed)
    {
        if (parsed.Parameters.TryGetValue("QueueUrl", out var url) && !string.IsNullOrEmpty(url))
        {
            return QueueUrlBuilder.ExtractQueueName(url) ?? url;
        }
        return null;
    }

    private static Task WriteErrorAsync(HttpContext context, SqsWireProtocol protocol, SqsErrorMapping.Mapping mapping) =>
        SqsErrorResponse.WriteAsync(context, protocol, mapping.StatusCode, mapping.Code, mapping.Message, mapping.FaultType);
}
