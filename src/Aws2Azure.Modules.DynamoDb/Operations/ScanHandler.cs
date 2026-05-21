using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Aws2Azure.Modules.DynamoDb.Errors;
using Aws2Azure.Modules.DynamoDb.Expressions;
using Aws2Azure.Modules.DynamoDb.Internal;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.DynamoDb.Operations;

/// <summary>
/// Implements DynamoDB <c>Scan</c> as a cross-partition Cosmos SQL
/// query.
///
/// <para>Unlike <see cref="QueryHandler"/>, there is no partition-key
/// scoping: every request walks the container's logical partitions
/// (<c>x-ms-documentdb-query-enablecrosspartition: true</c>). This is
/// expensive — every Scan logs a cost warning via the standard
/// cancellation-token-aware logging pipeline.</para>
///
/// <para>Translation strategy:</para>
/// <list type="bullet">
///   <item><c>FilterExpression</c> is evaluated in-process so
///   <c>ScannedCount</c> reflects pre-filter rows and <c>Count</c>
///   reflects post-filter rows — matching DynamoDB semantics.</item>
///   <item><c>ProjectionExpression</c> is applied in-process. Same
///   subset as Query: top-level attributes / <c>#alias</c>.</item>
///   <item><c>Limit</c> caps the pre-filter (scanned) count, just
///   like DynamoDB. Pagination round-trips the Cosmos
///   <c>x-ms-continuation</c> token in a sentinel
///   <c>__a2a_continuation</c> attribute on
///   <c>LastEvaluatedKey</c>.</item>
/// </list>
///
/// <para>Not supported in this slice: <c>IndexName</c>, parallel
/// scan (<c>Segment</c> / <c>TotalSegments</c>), legacy
/// <c>ScanFilter</c> / <c>ConditionalOperator</c> /
/// <c>AttributesToGet</c>.</para>
/// </summary>
internal static class ScanHandler
{
    private const string ContinuationSentinelAttr = "__a2a_continuation";
    private const int MaxBatchSize = 1000;

    public static async Task HandleScanAsync(
        HttpContext ctx, byte[] body, CosmosClient cosmos, CancellationToken ct)
    {
        ScanRequest? req;
        try
        {
            req = JsonSerializer.Deserialize(body, ScanJsonContext.Default.ScanRequest);
        }
        catch (JsonException ex)
        {
            await CosmosOpsShared.WriteErrorAsync(ctx, 400, "SerializationException",
                "Malformed JSON: " + ex.Message).ConfigureAwait(false);
            return;
        }
        if (req is null || !DynamoDbNames.IsValidTableName(req.TableName))
        {
            await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                "TableName is required and must match [a-zA-Z0-9_.-]{3,255}.").ConfigureAwait(false);
            return;
        }
        if (!string.IsNullOrEmpty(req.IndexName))
        {
            await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                "Scanning secondary indexes is not yet supported by the proxy.").ConfigureAwait(false);
            return;
        }
        if (req.Segment is not null || req.TotalSegments is not null)
        {
            await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                "Parallel scan (Segment/TotalSegments) is not yet supported by the proxy.").ConfigureAwait(false);
            return;
        }
        if (HasContent(req.ScanFilter) || !string.IsNullOrEmpty(req.ConditionalOperator)
            || HasContent(req.AttributesToGet))
        {
            await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                "Legacy ScanFilter / ConditionalOperator / AttributesToGet are not supported; use FilterExpression and ProjectionExpression.").ConfigureAwait(false);
            return;
        }
        if (req.Limit is int lim && lim <= 0)
        {
            await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                "Limit must be a positive integer.").ConfigureAwait(false);
            return;
        }
        if (!IsAllowedSelect(req.Select, out var selectError))
        {
            await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException", selectError).ConfigureAwait(false);
            return;
        }

        IReadOnlyDictionary<string, string>? names;
        IReadOnlyDictionary<string, JsonElement>? values;
        try
        {
            names = TryMaterialiseNames(req.ExpressionAttributeNames);
            values = TryMaterialiseValues(req.ExpressionAttributeValues);
        }
        catch (ExpressionSyntaxException ex)
        {
            await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException", ex.Message).ConfigureAwait(false);
            return;
        }

        using var metaResult = await CosmosOpsShared.TryReadTableMetadataAsync(cosmos, req.TableName!, ct).ConfigureAwait(false);
        if (metaResult.Status == CosmosOpsShared.TableMetadataReadStatus.CosmosError)
        {
            await CosmosOpsShared.WriteCosmosErrorAsync(ctx, metaResult.ErrorResponse!, ct).ConfigureAwait(false);
            return;
        }
        if (metaResult.Status == CosmosOpsShared.TableMetadataReadStatus.NotFound)
        {
            await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ResourceNotFoundException",
                $"Table not found: {req.TableName}").ConfigureAwait(false);
            return;
        }

        ConditionNode? filter = null;
        IReadOnlyList<string>? projection = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(req.FilterExpression))
                filter = ConditionExpressionParser.Parse(req.FilterExpression!, names, values);
            if (!string.IsNullOrWhiteSpace(req.ProjectionExpression))
                projection = ProjectionExpressionParser.Parse(req.ProjectionExpression!, names);
        }
        catch (ExpressionSyntaxException ex)
        {
            await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                $"Invalid expression (offset {ex.Position}): {ex.Message}").ConfigureAwait(false);
            return;
        }

        string? continuationIn;
        try
        {
            continuationIn = ExtractContinuation(req.ExclusiveStartKey);
        }
        catch (FormatException ex)
        {
            await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                "ExclusiveStartKey is malformed: " + ex.Message).ConfigureAwait(false);
            return;
        }

        await ExecuteScanAsync(ctx, req, filter, projection, continuationIn, cosmos, ct).ConfigureAwait(false);
    }

    private static async Task ExecuteScanAsync(
        HttpContext ctx, ScanRequest req,
        ConditionNode? filter, IReadOnlyList<string>? projection, string? continuationIn,
        CosmosClient cosmos, CancellationToken ct)
    {
        bool countOnly = string.Equals(req.Select, "COUNT", StringComparison.OrdinalIgnoreCase);

        var queryBody = BuildScanQueryBody();
        var collLink = "dbs/" + cosmos.DatabaseName + "/colls/" + req.TableName;
        var collUri = "/" + collLink + "/docs";

        int wantedScanned = req.Limit ?? int.MaxValue;
        var items = new List<Dictionary<string, JsonElement>>();
        int scanned = 0;
        int matched = 0;
        string? continuationOut = continuationIn;

        while (true)
        {
            int remaining = wantedScanned == int.MaxValue
                ? MaxBatchSize
                : Math.Max(1, wantedScanned - scanned);
            int pageSize = Math.Min(MaxBatchSize, remaining);

            using var content = new StringContent(queryBody, Encoding.UTF8);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/query+json");

            var headers = new List<KeyValuePair<string, string>>
            {
                new("x-ms-documentdb-isquery", "true"),
                new("x-ms-documentdb-query-enablecrosspartition", "true"),
                new("x-ms-max-item-count", pageSize.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            };
            if (req.ConsistentRead == true)
            {
                headers.Add(new KeyValuePair<string, string>("x-ms-consistency-level", "Strong"));
            }
            if (!string.IsNullOrEmpty(continuationOut))
            {
                headers.Add(new KeyValuePair<string, string>("x-ms-continuation", continuationOut));
            }

            using var resp = await cosmos.SendAsync(
                HttpMethod.Post, "docs", collLink, collUri,
                content, headers, ct).ConfigureAwait(false);

            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ResourceNotFoundException",
                    $"Table not found: {req.TableName}").ConfigureAwait(false);
                return;
            }
            if (!resp.IsSuccessStatusCode)
            {
                await CosmosOpsShared.WriteCosmosErrorAsync(ctx, resp, ct).ConfigureAwait(false);
                return;
            }

            using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

            if (doc.RootElement.TryGetProperty("Documents", out var docsEl)
                && docsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var docEl in docsEl.EnumerateArray())
                {
                    var itemMap = ExtractItemEnvelope(docEl);
                    if (itemMap is null) continue;
                    scanned++;

                    if (filter is not null)
                    {
                        bool keep;
                        try { keep = ConditionEvaluator.Evaluate(filter, itemMap); }
                        catch (ConditionEvaluationException ex)
                        {
                            await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException", ex.Message).ConfigureAwait(false);
                            return;
                        }
                        if (!keep) continue;
                    }
                    matched++;
                    if (!countOnly)
                    {
                        items.Add(projection is null ? itemMap : Project(itemMap, projection));
                    }
                }
            }

            continuationOut = null;
            if (resp.Headers.TryGetValues("x-ms-continuation", out var ctValues))
            {
                foreach (var v in ctValues) { continuationOut = v; break; }
            }

            if (scanned >= wantedScanned) break;
            if (string.IsNullOrEmpty(continuationOut)) break;
        }

        var response = new ScanResponse
        {
            Items = countOnly ? null : items,
            Count = matched,
            ScannedCount = scanned,
            LastEvaluatedKey = string.IsNullOrEmpty(continuationOut) ? null : BuildContinuationKey(continuationOut),
        };
        await CosmosOpsShared.WriteJsonAsync(ctx, 200, response, ScanJsonContext.Default.ScanResponse)
            .ConfigureAwait(false);
    }

    // ----- helpers (mirror QueryHandler) ------------------------------

    /// <summary>
    /// Scan's Cosmos SQL is a constant — the only filter clause is
    /// <c>c._a2a = 'item'</c> to skip the table-metadata sidecar doc.
    /// All other filtering is in-process.
    /// </summary>
    private static string BuildScanQueryBody()
    {
        using var ms = new MemoryStream();
        var options = new JsonWriterOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        using (var writer = new Utf8JsonWriter(ms, options))
        {
            writer.WriteStartObject();
            writer.WriteString("query", "SELECT * FROM c WHERE c._a2a = 'item'");
            writer.WritePropertyName("parameters");
            writer.WriteStartArray();
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static Dictionary<string, JsonElement>? ExtractItemEnvelope(JsonElement docEl)
    {
        if (docEl.ValueKind != JsonValueKind.Object) return null;
        if (!docEl.TryGetProperty("item", out var envelope)
            || envelope.ValueKind != JsonValueKind.Object) return null;
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var prop in envelope.EnumerateObject())
        {
            result[prop.Name] = prop.Value.Clone();
        }
        return result;
    }

    private static Dictionary<string, JsonElement> Project(
        Dictionary<string, JsonElement> item, IReadOnlyList<string> paths)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var p in paths)
        {
            if (item.TryGetValue(p, out var v)) result[p] = v;
        }
        return result;
    }

    private static string? ExtractContinuation(JsonElement? exclusiveStartKey)
    {
        if (exclusiveStartKey is not { } esk) return null;
        if (esk.ValueKind != JsonValueKind.Object) return null;
        if (!esk.TryGetProperty(ContinuationSentinelAttr, out var sentinel)) return null;
        if (sentinel.ValueKind != JsonValueKind.Object) return null;
        if (!sentinel.TryGetProperty("S", out var sEl) || sEl.ValueKind != JsonValueKind.String)
            throw new FormatException("continuation attribute must be a typed string.");
        var b64 = sEl.GetString();
        if (string.IsNullOrEmpty(b64)) return null;
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(b64));
        }
        catch (FormatException)
        {
            throw new FormatException("continuation attribute is not valid base64.");
        }
    }

    private static Dictionary<string, JsonElement> BuildContinuationKey(string cosmosContinuation)
    {
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(cosmosContinuation));
        var json = $"{{\"S\":\"{b64}\"}}";
        using var doc = JsonDocument.Parse(json);
        return new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            [ContinuationSentinelAttr] = doc.RootElement.Clone(),
        };
    }

    private static bool IsAllowedSelect(string? select, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrEmpty(select)) return true;
        if (string.Equals(select, "ALL_ATTRIBUTES", StringComparison.Ordinal)
            || string.Equals(select, "COUNT", StringComparison.Ordinal)
            || string.Equals(select, "SPECIFIC_ATTRIBUTES", StringComparison.Ordinal))
            return true;
        if (string.Equals(select, "ALL_PROJECTED_ATTRIBUTES", StringComparison.Ordinal))
        {
            error = "Select=ALL_PROJECTED_ATTRIBUTES requires IndexName, which is not supported in this slice.";
            return false;
        }
        error = $"Select '{select}' is not a recognised value.";
        return false;
    }

    private static IReadOnlyDictionary<string, string>? TryMaterialiseNames(JsonElement? src)
    {
        if (src is not { } v || v.ValueKind != JsonValueKind.Object) return null;
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in v.EnumerateObject())
        {
            if (p.Value.ValueKind != JsonValueKind.String)
                throw new ExpressionSyntaxException(0, $"ExpressionAttributeNames['{p.Name}'] must be a string.");
            d[p.Name] = p.Value.GetString()!;
        }
        return d;
    }

    private static IReadOnlyDictionary<string, JsonElement>? TryMaterialiseValues(JsonElement? src)
    {
        if (src is not { } v || v.ValueKind != JsonValueKind.Object) return null;
        var d = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var p in v.EnumerateObject())
        {
            d[p.Name] = p.Value.Clone();
        }
        return d;
    }

    private static bool HasContent(JsonElement? el)
    {
        if (el is not { } v) return false;
        return v.ValueKind switch
        {
            JsonValueKind.Object => v.EnumerateObject().MoveNext(),
            JsonValueKind.Array => v.GetArrayLength() > 0,
            JsonValueKind.String => !string.IsNullOrEmpty(v.GetString()),
            JsonValueKind.Null or JsonValueKind.Undefined => false,
            _ => true,
        };
    }
}
