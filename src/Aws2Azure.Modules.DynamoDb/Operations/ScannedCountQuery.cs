using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Aws2Azure.Modules.DynamoDb.Expressions;
using Aws2Azure.Modules.DynamoDb.Internal;

namespace Aws2Azure.Modules.DynamoDb.Operations;

/// <summary>
/// Recovers DynamoDB's pre-filter <c>ScannedCount</c> for a Scan/Query
/// whose <c>FilterExpression</c> was (partially) pushed into the Cosmos
/// SQL by <see cref="FilterPushdownVisitor"/>.
///
/// <para>When a filter fragment is enforced server-side, the documents
/// Cosmos streams back are already filtered, so counting them yields
/// DynamoDB's <c>Count</c>, not its <c>ScannedCount</c>. To stay
/// wire-faithful — <c>ScannedCount</c> reflects items <em>examined</em>
/// before the filter — this issues a cheap server-side aggregate
/// (<c>SELECT VALUE COUNT(1)</c>) over the identical scan/key scope but
/// <em>without</em> the pushed filter.</para>
///
/// <para>Best-effort: returns <c>null</c> when the aggregate cannot be
/// read — non-success status, no numeric payload, or any non-cancellation
/// exception (transient transport blip, malformed body) — so callers fall
/// back to the streaming counter rather than failing an already-successful
/// request. Genuine caller cancellation still propagates. The aggregate may
/// arrive as one rolled-up number or as per-partition partials spread across
/// <c>Documents</c> entries and continuation pages, so every numeric value
/// seen is summed.</para>
/// </summary>
internal static class ScannedCountQuery
{
    public static async Task<int?> CountAsync(
        CosmosClient cosmos,
        string collLink,
        string collUri,
        string countSql,
        IReadOnlyList<CosmosSqlParameter> parameters,
        string? partitionKeyHeader,
        bool strong,
        CancellationToken ct)
    {
        long total = 0;
        bool sawNumber = false;
        string? continuation = null;
        using var body = CosmosQueryBody.Build(countSql, parameters);

        try
        {
            do
            {
                var headers = new List<KeyValuePair<string, string>>
                {
                    new("x-ms-documentdb-isquery", "true"),
                    new("x-ms-max-item-count", "1000"),
                };
                if (partitionKeyHeader is not null)
                {
                    headers.Add(new KeyValuePair<string, string>("x-ms-documentdb-partitionkey", partitionKeyHeader));
                }
                else
                {
                    headers.Add(new KeyValuePair<string, string>("x-ms-documentdb-query-enablecrosspartition", "true"));
                }
                if (strong)
                {
                    headers.Add(new KeyValuePair<string, string>("x-ms-consistency-level", "Strong"));
                }
                if (!string.IsNullOrEmpty(continuation))
                {
                    headers.Add(new KeyValuePair<string, string>("x-ms-continuation", continuation));
                }

                using var resp = await cosmos.SendAsync(
                    HttpMethod.Post, "docs", collLink, collUri,
                    body.WrittenMemory, "application/query+json", headers, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    return null;
                }

                using var cosmosBody = await CosmosOpsShared.ReadCosmosJsonBodyAsync(resp.Content, ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(cosmosBody.WrittenMemory);
                if (doc.RootElement.TryGetProperty("Documents", out var arr)
                    && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in arr.EnumerateArray())
                    {
                        if (TryReadAggregate(el, out var n))
                        {
                            total += n;
                            sawNumber = true;
                        }
                    }
                }

                continuation = null;
                if (resp.Headers.TryGetValues("x-ms-continuation", out var ctValues))
                {
                    foreach (var v in ctValues) { continuation = v; break; }
                }
            }
            while (!string.IsNullOrEmpty(continuation));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Best-effort: a transient transport blip or an unexpected aggregate
            // body must degrade to the streaming counter, never fail an
            // already-successful Scan/Query.
            return null;
        }

        if (!sawNumber)
        {
            return null;
        }
        return total > int.MaxValue ? int.MaxValue : (int)total;
    }

    /// <summary>
    /// <c>SELECT VALUE COUNT(1)</c> yields a bare number per partition page.
    /// Defensively also accepts an object envelope (<c>{"item": n}</c> /
    /// <c>{"$1": n}</c>) emitted by some gateway shapes.
    /// </summary>
    private static bool TryReadAggregate(JsonElement el, out long value)
    {
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out value))
        {
            return true;
        }
        if (el.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in el.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetInt64(out value))
                {
                    return true;
                }
            }
        }
        value = 0;
        return false;
    }
}
