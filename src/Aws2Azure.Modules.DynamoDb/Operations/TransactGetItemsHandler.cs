using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Aws2Azure.Modules.DynamoDb.Expressions;
using Aws2Azure.Modules.DynamoDb.Internal;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.DynamoDb.Operations;

/// <summary>
/// DynamoDB <c>TransactGetItems</c> → fan-out of per-item Cosmos
/// strongly-consistent GETs. Cosmos has no cross-container ACID read,
/// but transactional get only requires that every read sees the latest
/// committed value, so strong consistency on each fan-out call is
/// behaviour-equivalent here.
///
/// <list type="bullet">
///   <item>At most 100 items per request; over the cap → <c>ValidationException</c>.</item>
///   <item>Responses are aligned positionally with TransactItems; missing items
///   emit an empty <c>{}</c> entry.</item>
///   <item>Every Cosmos GET sends <c>x-ms-consistency-level: Strong</c>.</item>
///   <item>Per-item <c>ProjectionExpression</c> + <c>ExpressionAttributeNames</c>
///   honoured (top-level / <c>#alias</c> only).</item>
///   <item>Any non-2xx, non-404 from Cosmos cancels the whole transaction with
///   <c>TransactionCanceledException</c> + per-item <c>CancellationReasons</c>.</item>
/// </list>
/// </summary>
internal static class TransactGetItemsHandler
{
    private const int MaxItemsPerCall = 100;
    private const int MaxParallelism = 16;

    public static async Task HandleTransactGetItemsAsync(
        HttpContext ctx, byte[] body, CosmosClient cosmos, CancellationToken ct)
    {
        TransactGetItemsRequest? req;
        try
        {
            req = JsonSerializer.Deserialize(body, TransactGetItemsJsonContext.Default.TransactGetItemsRequest);
        }
        catch (JsonException ex)
        {
            await CosmosOpsShared.WriteErrorAsync(ctx, 400, "SerializationException",
                "Malformed JSON: " + ex.Message).ConfigureAwait(false);
            return;
        }
        if (req is null || req.TransactItems is null || req.TransactItems.Count == 0)
        {
            await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                "TransactItems is required and must contain at least one Get entry.").ConfigureAwait(false);
            return;
        }
        if (req.TransactItems.Count > MaxItemsPerCall)
        {
            await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                $"TransactGetItems supports at most {MaxItemsPerCall} items per request.").ConfigureAwait(false);
            return;
        }

        var tableMeta = new Dictionary<string, TableMetadata>(StringComparer.Ordinal);
        var work = new WorkUnit[req.TransactItems.Count];

        for (int i = 0; i < req.TransactItems.Count; i++)
        {
            var entry = req.TransactItems[i];
            if (entry is null || entry.Get.ValueKind != JsonValueKind.Object)
            {
                await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                    $"TransactItems[{i}].Get is required and must be an object.").ConfigureAwait(false);
                return;
            }
            if (!entry.Get.TryGetProperty("TableName", out var tEl) || tEl.ValueKind != JsonValueKind.String)
            {
                await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                    $"TransactItems[{i}].Get.TableName is required.").ConfigureAwait(false);
                return;
            }
            var tableName = tEl.GetString()!;
            if (!DynamoDbNames.IsValidTableName(tableName))
            {
                await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                    $"Invalid TableName '{tableName}'.").ConfigureAwait(false);
                return;
            }
            if (!entry.Get.TryGetProperty("Key", out var keyEl) || keyEl.ValueKind != JsonValueKind.Object)
            {
                await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                    $"TransactItems[{i}].Get.Key is required and must be an object.").ConfigureAwait(false);
                return;
            }

            if (!tableMeta.TryGetValue(tableName, out var meta))
            {
                using var metaRead = await CosmosOpsShared.TryReadTableMetadataAsync(cosmos, tableName, ct).ConfigureAwait(false);
                if (metaRead.Status == CosmosOpsShared.TableMetadataReadStatus.CosmosError)
                {
                    await CosmosOpsShared.WriteCosmosErrorAsync(ctx, metaRead.ErrorResponse!, ct).ConfigureAwait(false);
                    return;
                }
                if (metaRead.Status == CosmosOpsShared.TableMetadataReadStatus.NotFound)
                {
                    await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ResourceNotFoundException",
                        $"Table not found: {tableName}").ConfigureAwait(false);
                    return;
                }
                meta = metaRead.Metadata!;
                tableMeta[tableName] = meta;
            }

            foreach (var k in meta.KeySchema)
            {
                if (!keyEl.TryGetProperty(k.Name, out var attr))
                {
                    await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                        $"TransactItems[{i}].Get.Key is missing required attribute '{k.Name}'.").ConfigureAwait(false);
                    return;
                }
                if (!ItemKeyFormatter.ValidateKeyAttributeType(attr, meta, k.Name, out var typeError))
                {
                    await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException", typeError).ConfigureAwait(false);
                    return;
                }
            }
            if (!ItemKeyFormatter.TryBuild(keyEl, meta, out var pk, out var id, out var keyError))
            {
                await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException", keyError).ConfigureAwait(false);
                return;
            }

            IReadOnlyList<string>? projection = null;
            if (entry.Get.TryGetProperty("ProjectionExpression", out var peEl) && peEl.ValueKind == JsonValueKind.String)
            {
                IReadOnlyDictionary<string, string>? names = null;
                if (entry.Get.TryGetProperty("ExpressionAttributeNames", out var eanEl))
                {
                    if (eanEl.ValueKind != JsonValueKind.Object)
                    {
                        await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                            $"TransactItems[{i}].Get.ExpressionAttributeNames must be a JSON object.").ConfigureAwait(false);
                        return;
                    }
                    var d = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var p in eanEl.EnumerateObject())
                    {
                        if (p.Value.ValueKind != JsonValueKind.String)
                        {
                            await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                                $"TransactItems[{i}].Get.ExpressionAttributeNames['{p.Name}'] must be a string.").ConfigureAwait(false);
                            return;
                        }
                        d[p.Name] = p.Value.GetString()!;
                    }
                    names = d;
                }
                try
                {
                    projection = ProjectionExpressionParser.Parse(peEl.GetString()!, names);
                }
                catch (ExpressionSyntaxException ex)
                {
                    await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                        $"Invalid ProjectionExpression (offset {ex.Position}): {ex.Message}").ConfigureAwait(false);
                    return;
                }
            }

            work[i] = new WorkUnit(tableName, pk, id, projection);
        }

        var results = new PerItemResult[work.Length];
        using (var sem = new SemaphoreSlim(MaxParallelism))
        {
            var tasks = new Task[work.Length];
            for (int i = 0; i < work.Length; i++)
            {
                var idx = i;
                tasks[i] = ExecuteOneAsync(cosmos, work[i], sem, results, idx, ct);
            }
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        bool anyHardError = false;
        for (int i = 0; i < results.Length; i++)
        {
            if (results[i].HardError is not null) { anyHardError = true; break; }
        }
        if (anyHardError)
        {
            await WriteTransactionCanceledAsync(ctx, results).ConfigureAwait(false);
            return;
        }

        var responses = new List<TransactGetItemResponse>(work.Length);
        for (int i = 0; i < work.Length; i++)
        {
            var item = results[i].Item;
            if (item is null)
            {
                responses.Add(new TransactGetItemResponse());
                continue;
            }
            if (work[i].Projection is { } projection)
            {
                item = Project(item, projection);
            }
            responses.Add(new TransactGetItemResponse { Item = item });
        }

        var resp = new TransactGetItemsResponse { Responses = responses };
        await CosmosOpsShared.WriteJsonAsync(ctx, 200, resp,
            TransactGetItemsJsonContext.Default.TransactGetItemsResponse).ConfigureAwait(false);
    }

    private static async Task ExecuteOneAsync(
        CosmosClient cosmos, WorkUnit unit,
        SemaphoreSlim sem, PerItemResult[] results, int idx, CancellationToken ct)
    {
        await sem.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var docLink = "dbs/" + cosmos.DatabaseName + "/colls/" + unit.Table + "/docs/" + unit.Id;
            var pkHeader = CosmosOpsShared.BuildPartitionKeyHeader(unit.Pk);
            var headers = new[]
            {
                new KeyValuePair<string, string>("x-ms-documentdb-partitionkey", pkHeader),
                new KeyValuePair<string, string>("x-ms-consistency-level", "Strong"),
            };
            using var resp = await cosmos.SendAsync(
                HttpMethod.Get, "docs", docLink, "/" + docLink,
                content: null, headers, ct).ConfigureAwait(false);

            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                if (CosmosOpsShared.Is404ContainerMissing(resp))
                {
                    results[idx] = new PerItemResult
                    {
                        HardError = new HardError("ResourceNotFoundException",
                            $"Table not found: {unit.Table}"),
                    };
                    return;
                }
                results[idx] = new PerItemResult();
                return;
            }
            if (!resp.IsSuccessStatusCode)
            {
                string bodyText = string.Empty;
                try { bodyText = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false); }
                catch { }
                var status = (int)resp.StatusCode;
                var code = status switch
                {
                    429 => "ProvisionedThroughputExceededException",
                    401 or 403 => "AccessDeniedException",
                    _ when status >= 500 => "InternalServerError",
                    _ => "ValidationException",
                };
                results[idx] = new PerItemResult
                {
                    HardError = new HardError(code,
                        string.IsNullOrEmpty(bodyText) ? (resp.ReasonPhrase ?? "Cosmos request failed.") : bodyText),
                };
                return;
            }

            var item = await CosmosOpsShared.ReadAndExtractItemAsync(
                resp.Content, DynamoDbMetrics.OpTransactGet, ct).ConfigureAwait(false);
            results[idx] = new PerItemResult { Item = item };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            results[idx] = new PerItemResult
            {
                HardError = new HardError("InternalServerError", ex.Message),
            };
        }
        finally
        {
            sem.Release();
        }
    }

    private static Dictionary<string, JsonElement> Project(
        Dictionary<string, JsonElement> item, IReadOnlyList<string> attrs)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        for (int i = 0; i < attrs.Count; i++)
        {
            if (item.TryGetValue(attrs[i], out var v))
            {
                result[attrs[i]] = v;
            }
        }
        return result;
    }

    private static async Task WriteTransactionCanceledAsync(HttpContext ctx, PerItemResult[] results)
    {
        // TransactionCanceledException — preserve positional CancellationReasons
        // so the SDK can surface per-item failure codes. Hand-rolled writer keeps
        // the response AOT-friendly without needing a Dictionary<string, object?>
        // JsonSerializerContext.
        string? firstMessage = null;
        foreach (var r in results)
        {
            if (r.HardError is { } he) { firstMessage = he.Message; break; }
        }

        using var ms = new MemoryStream();
        await using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("__type", "com.amazonaws.dynamodb.v20120810#TransactionCanceledException");
            w.WriteString("Message", firstMessage ?? "Transaction cancelled.");
            w.WritePropertyName("CancellationReasons");
            w.WriteStartArray();
            foreach (var r in results)
            {
                w.WriteStartObject();
                if (r.HardError is { } he)
                {
                    w.WriteString("Code", he.Code);
                    if (!string.IsNullOrEmpty(he.Message))
                    {
                        w.WriteString("Message", he.Message);
                    }
                }
                else
                {
                    w.WriteString("Code", "None");
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }

        ctx.Response.StatusCode = 400;
        ctx.Response.ContentType = "application/x-amz-json-1.0";
        var bytes = ms.ToArray();
        await ctx.Response.Body.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
    }

    private readonly record struct WorkUnit(string Table, string Pk, string Id, IReadOnlyList<string>? Projection);

    private struct PerItemResult
    {
        public Dictionary<string, JsonElement>? Item;
        public HardError? HardError;
    }

    private readonly record struct HardError(string Code, string Message);
}
