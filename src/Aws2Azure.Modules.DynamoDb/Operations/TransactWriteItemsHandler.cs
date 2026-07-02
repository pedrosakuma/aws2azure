using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Aws2Azure.Core.Buffers;
using Aws2Azure.Modules.DynamoDb.Expressions;
using Aws2Azure.Modules.DynamoDb.Internal;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.DynamoDb.Operations;

/// <summary>
/// DynamoDB <c>TransactWriteItems</c> → a single Azure Cosmos DB stored-procedure
/// transaction. Cosmos sprocs run all of their reads/writes inside one
/// server-side ACID transaction scoped to a single logical partition of a
/// single container, which is exactly the atomicity TransactWriteItems needs —
/// provided every operation targets the same table and the same partition-key
/// value.
///
/// <para>Scope of this implementation (documented in
/// <c>docs/gaps/dynamodb/TransactWriteItems.yaml</c>):</para>
/// <list type="bullet">
///   <item><c>Put</c>, <c>Delete</c>, and <c>ConditionCheck</c> are supported
///   and commit atomically (all-or-nothing).</item>
///   <item><c>Update</c> is rejected with <c>ValidationException</c> — atomic
///   in-transaction Update is a known gap.</item>
///   <item>All operations must target one table and one partition-key value;
///   cross-table / cross-partition transactions are rejected with
///   <c>ValidationException</c> (Cosmos cannot make them atomic).</item>
///   <item><c>ClientRequestToken</c> is accepted but not honoured (no
///   idempotency store).</item>
///   <item>Stored procedures must be enabled; with them disabled there is no
///   honest non-atomic fallback, so the request is rejected.</item>
/// </list>
/// </summary>
internal static class TransactWriteItemsHandler
{
    private const int MaxItemsPerCall = 100;

    // The per-action envelopes are deserialized as JsonRange (no DOM) and
    // re-parsed on demand from the request buffer. The source-gen context that
    // captured the ranges (TransactWriteItemsJsonContext) allows trailing
    // commas, so the transient re-parse must accept the same grammar or a body
    // that deserialized fine could throw on re-parse. Comments stay at the
    // serializer default (Disallow).
    private static readonly JsonDocumentOptions TransactItemParseOptions = new()
    {
        AllowTrailingCommas = true,
    };

    internal enum OpKind
    {
        Put,
        Delete,
        Check,
    }

    internal readonly record struct PreparedOp(OpKind Kind, string Id, byte[]? DocBytes, string? ConditionJson);

    public static async Task HandleTransactWriteItemsAsync(
        HttpContext ctx, byte[] body, CosmosClient cosmos, SprocContext? sprocCtx, CancellationToken ct)
    {
        TransactWriteItemsRequest? req;
        try
        {
            req = JsonSerializer.Deserialize(body, TransactWriteItemsJsonContext.Default.TransactWriteItemsRequest);
        }
        catch (JsonException ex)
        {
            await CosmosOpsShared.WriteErrorAsync(ctx, 400, "SerializationException",
                "Malformed JSON: " + ex.Message).ConfigureAwait(false);
            return;
        }

        if (req?.TransactItems is null || req.TransactItems.Count == 0)
        {
            await Reject(ctx, "TransactItems is required and must contain at least one entry.").ConfigureAwait(false);
            return;
        }
        if (req.TransactItems.Count > MaxItemsPerCall)
        {
            await Reject(ctx, $"TransactWriteItems supports at most {MaxItemsPerCall} items per request.").ConfigureAwait(false);
            return;
        }

        // A transaction must be atomic; aws2azure implements atomicity with a
        // Cosmos stored procedure (single logical-partition ACID). With sprocs
        // disabled there is no honest non-atomic fallback, so reject up front.
        if (sprocCtx is not { IsSprocEnabled: true } || sprocCtx.Manager is null)
        {
            await Reject(ctx,
                "TransactWriteItems requires stored procedures, which are disabled in this deployment. Set the DynamoDB stored-procedure mode to Preferred or Required to enable atomic transactions.").ConfigureAwait(false);
            return;
        }

        var tableMeta = new Dictionary<string, TableMetadata>(StringComparer.Ordinal);
        var prepared = new PreparedOp[req.TransactItems.Count];
        string? table = null;
        string? partitionKey = null;
        var seenTargets = new HashSet<string>(StringComparer.Ordinal);

        for (int i = 0; i < req.TransactItems.Count; i++)
        {
            var item = req.TransactItems[i];
            if (item is null)
            {
                await Reject(ctx, $"TransactItems[{i}] is required.").ConfigureAwait(false);
                return;
            }

            bool hasPut = IsPresentObject(body, item.Put);
            bool hasDelete = IsPresentObject(body, item.Delete);
            bool hasCheck = IsPresentObject(body, item.ConditionCheck);
            bool hasUpdate = IsPresentObject(body, item.Update);

            if (hasUpdate)
            {
                await Reject(ctx,
                    $"TransactItems[{i}].Update is not yet supported by aws2azure: atomic Update within a transaction is a documented gap. Use Put to overwrite the full item, or perform the update outside the transaction. See docs/gaps/dynamodb/TransactWriteItems.yaml.").ConfigureAwait(false);
                return;
            }

            int present = (hasPut ? 1 : 0) + (hasDelete ? 1 : 0) + (hasCheck ? 1 : 0);
            if (present != 1)
            {
                await Reject(ctx,
                    $"TransactItems[{i}] must contain exactly one of Put, Delete, or ConditionCheck.").ConfigureAwait(false);
                return;
            }

            var opRange = hasPut ? item.Put : hasDelete ? item.Delete : item.ConditionCheck;
            string opName = hasPut ? "Put" : hasDelete ? "Delete" : "ConditionCheck";

            // Re-materialize only the single present envelope into a short-lived
            // pooled JsonDocument; the validators / key-extraction / condition
            // parser below traverse it and it is disposed at the end of this
            // iteration. Nothing downstream retains a JsonElement: the work unit
            // keeps only the encoded doc bytes, the condition JSON, and the
            // computed id/pk strings.
            using var opDoc = JsonDocument.Parse(body.AsMemory(opRange.Start, opRange.Length), TransactItemParseOptions);
            var op = opDoc.RootElement;

            if (!op.TryGetProperty("TableName", out var tEl) || tEl.ValueKind != JsonValueKind.String)
            {
                await Reject(ctx, $"TransactItems[{i}].{opName}.TableName is required.").ConfigureAwait(false);
                return;
            }
            var tableName = tEl.GetString()!;
            if (!DynamoDbNames.IsValidTableName(tableName))
            {
                await Reject(ctx, $"Invalid TableName '{tableName}'.").ConfigureAwait(false);
                return;
            }

            // Single-table constraint: a Cosmos sproc transaction is scoped to
            // one container.
            if (table is null)
            {
                table = tableName;
            }
            else if (!string.Equals(table, tableName, StringComparison.Ordinal))
            {
                await Reject(ctx,
                    "TransactWriteItems via aws2azure requires all operations to target the same table (Azure Cosmos DB stored-procedure transactions are scoped to a single container). See docs/gaps/dynamodb/TransactWriteItems.yaml.").ConfigureAwait(false);
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

            // The key-bearing element: Put carries a full Item, Delete/Check a Key.
            JsonElement keyBearer;
            if (hasPut)
            {
                if (!op.TryGetProperty("Item", out keyBearer) || keyBearer.ValueKind != JsonValueKind.Object)
                {
                    await Reject(ctx, $"TransactItems[{i}].Put.Item is required and must be an object.").ConfigureAwait(false);
                    return;
                }
            }
            else
            {
                if (!op.TryGetProperty("Key", out keyBearer) || keyBearer.ValueKind != JsonValueKind.Object)
                {
                    await Reject(ctx, $"TransactItems[{i}].{opName}.Key is required and must be an object.").ConfigureAwait(false);
                    return;
                }
            }

            foreach (var k in meta.KeySchema)
            {
                if (!keyBearer.TryGetProperty(k.Name, out var attr))
                {
                    await Reject(ctx, $"TransactItems[{i}].{opName} is missing required key attribute '{k.Name}'.").ConfigureAwait(false);
                    return;
                }
                if (!ItemKeyFormatter.ValidateKeyAttributeType(attr, meta, k.Name, out var typeError))
                {
                    await Reject(ctx, typeError).ConfigureAwait(false);
                    return;
                }
            }

            string pk, id, keyError;
            bool keyOk = hasPut
                ? ItemKeyFormatter.TryBuildFromItem(keyBearer, meta, out pk, out id, out keyError)
                : ItemKeyFormatter.TryBuild(keyBearer, meta, out pk, out id, out keyError);
            if (!keyOk)
            {
                await Reject(ctx, keyError).ConfigureAwait(false);
                return;
            }

            // Single-partition constraint: the sproc transaction can only span
            // one logical partition key value.
            if (partitionKey is null)
            {
                partitionKey = pk;
            }
            else if (!string.Equals(partitionKey, pk, StringComparison.Ordinal))
            {
                await Reject(ctx,
                    "TransactWriteItems via aws2azure requires all operations to share the same partition-key value (Azure Cosmos DB stored-procedure transactions are scoped to a single logical partition). See docs/gaps/dynamodb/TransactWriteItems.yaml.").ConfigureAwait(false);
                return;
            }

            if (!seenTargets.Add(id))
            {
                await Reject(ctx, "Transaction request cannot include multiple operations on one item.").ConfigureAwait(false);
                return;
            }

            ConditionNode? condition;
            try
            {
                condition = ParseCondition(op, out var condError);
                if (condError is not null)
                {
                    await Reject(ctx, condError).ConfigureAwait(false);
                    return;
                }
            }
            catch (ExpressionSyntaxException ex)
            {
                await Reject(ctx, $"Invalid ConditionExpression (offset {ex.Position}): {ex.Message}").ConfigureAwait(false);
                return;
            }
            catch (ConditionParseConflictException ex)
            {
                await Reject(ctx, ex.Message).ConfigureAwait(false);
                return;
            }

            if (hasCheck && condition is null)
            {
                await Reject(ctx, $"TransactItems[{i}].ConditionCheck.ConditionExpression is required.").ConfigureAwait(false);
                return;
            }

            // The transaction sproc evaluates condition paths against the RAW
            // Cosmos document, where shadow-encoded / injected reserved names
            // (id, ttl, _a2a*) do not hold the user's value under that key. With
            // no in-process fallback (unlike single-item conditional writes),
            // such a condition would silently evaluate against the wrong field,
            // so reject it. See docs/gaps/dynamodb/TransactWriteItems.yaml.
            if (SprocEligibility.FindReservedConditionRoot(condition) is { } reservedRoot)
            {
                await Reject(ctx,
                    $"TransactItems[{i}] ConditionExpression references attribute '{reservedRoot}', " +
                    "which this proxy cannot evaluate inside a transaction because it collides with a " +
                    "reserved Cosmos document field. Use a different attribute name.").ConfigureAwait(false);
                return;
            }

            string? conditionJson = SprocAstSerializer.SerializeCondition(condition);

            byte[]? docBytes = null;
            if (hasPut)
            {
                if (!ItemHandlers.ValidateItemShape(keyBearer, out var shapeError))
                {
                    await Reject(ctx, shapeError).ConfigureAwait(false);
                    return;
                }
                try
                {
                    // Embedded into the sproc params JSON via WriteRawValue
                    // (bytes overload) — no string round-trip.
                    int? ttlSeconds = TtlTranslation.ComputeItemTtlSeconds(
                        keyBearer, meta.TimeToLive, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                    var orderKeys = SecondaryIndexOrderKeys.Compute(meta, keyBearer);
                    docBytes = ItemHandlers.BuildItemDocumentBytes(id, pk, keyBearer, binary: false, ttlSeconds, orderKeys);
                }
                catch (ArgumentException ex)
                {
                    await Reject(ctx, ex.Message).ConfigureAwait(false);
                    return;
                }
            }

            prepared[i] = new PreparedOp(
                hasPut ? OpKind.Put : hasDelete ? OpKind.Delete : OpKind.Check,
                id, docBytes, conditionJson);
        }

        PooledByteBufferWriter paramsBuf;
        try
        {
            paramsBuf = BuildTransactParamsBody(prepared);
        }
        catch (JsonException)
        {
            // A condition/value AST that serialized to malformed JSON (e.g. an
            // ExpressionAttributeValues entry like {"N":"not-a-number"}) only
            // surfaces here, when WriteRawValue re-validates the embedded token.
            // Map it to a ValidationException rather than a 500.
            await Reject(ctx, "One or more ExpressionAttributeValues are not valid DynamoDB attribute values.").ConfigureAwait(false);
            return;
        }

        SprocTransactResult result;
        using (paramsBuf)
        {
            var ready = await sprocCtx.Manager.EnsureTransactSprocAsync(cosmos, table!, ct).ConfigureAwait(false);
            if (!ready)
            {
                await CosmosOpsShared.WriteErrorAsync(ctx, 500, "InternalServerError",
                    "TransactWriteItems stored procedure could not be provisioned.").ConfigureAwait(false);
                return;
            }

            result = await sprocCtx.Manager.ExecuteTransactAsync(
                cosmos, table!, partitionKey!, paramsBuf.WrittenMemory, ct).ConfigureAwait(false);
        }

        if (result.Success)
        {
            await CosmosOpsShared.WriteJsonAsync(ctx, 200, new TransactWriteItemsResponse(),
                TransactWriteItemsJsonContext.Default.TransactWriteItemsResponse).ConfigureAwait(false);
            return;
        }
        if (result.ConditionFailed)
        {
            await WriteTransactionCanceledAsync(ctx, result.ResponseBody, prepared.Length).ConfigureAwait(false);
            return;
        }

        var status = result.StatusCode;
        var code = status switch
        {
            429 => "ProvisionedThroughputExceededException",
            401 or 403 => "AccessDeniedException",
            _ => "InternalServerError",
        };
        var httpStatus = status >= 500 || status == 0 ? 500 : 400;
        if (status == 429 || status == 401 || status == 403)
        {
            httpStatus = 400;
        }
        await CosmosOpsShared.WriteErrorAsync(ctx, httpStatus, code,
            string.IsNullOrEmpty(result.ErrorBody)
                ? "TransactWriteItems failed; the transaction was rolled back."
                : result.ErrorBody!).ConfigureAwait(false);
    }

    // True when the captured range is present and its value is a JSON object.
    // The JsonRange converter records reader.TokenStartIndex, which STJ points
    // at the first non-whitespace byte of the value token, so the value is an
    // object iff that byte is '{'. This reproduces the previous
    // `JsonElement.ValueKind == JsonValueKind.Object` gate (a present-but-null
    // or present-but-scalar envelope is treated as absent) without materializing
    // a DOM just to classify it.
    private static bool IsPresentObject(byte[] body, JsonRange range)
        => range.IsPresent && body[range.Start] == (byte)'{';

    private static ConditionNode? ParseCondition(JsonElement op, out string? error)
    {
        error = null;

        string? expr = null;
        if (op.TryGetProperty("ConditionExpression", out var ceEl) && ceEl.ValueKind == JsonValueKind.String)
        {
            expr = ceEl.GetString();
        }

        IReadOnlyDictionary<string, string>? names = null;
        if (op.TryGetProperty("ExpressionAttributeNames", out var eanEl))
        {
            if (eanEl.ValueKind != JsonValueKind.Object)
            {
                error = "ExpressionAttributeNames must be a JSON object.";
                return null;
            }
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var p in eanEl.EnumerateObject())
            {
                if (p.Value.ValueKind != JsonValueKind.String)
                {
                    error = $"ExpressionAttributeNames['{p.Name}'] must be a string.";
                    return null;
                }
                d[p.Name] = p.Value.GetString()!;
            }
            names = d;
        }

        IReadOnlyDictionary<string, JsonElement>? values = null;
        if (op.TryGetProperty("ExpressionAttributeValues", out var eavEl))
        {
            if (eavEl.ValueKind != JsonValueKind.Object)
            {
                error = "ExpressionAttributeValues must be a JSON object.";
                return null;
            }
            var d = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var p in eavEl.EnumerateObject())
            {
                d[p.Name] = p.Value.Clone();
            }
            values = d;
        }

        if (string.IsNullOrWhiteSpace(expr))
        {
            if (names is { Count: > 0 } || values is { Count: > 0 })
            {
                error = "ExpressionAttributeNames/Values were supplied but no ConditionExpression references them.";
                return null;
            }
            return null;
        }

        return ConditionGate.TryParse(expr, expected: null, conditionalOperator: null, names, values);
    }

    /// <summary>
    /// Assembles the full <c>atomicTransactWrite</c> sproc parameter list
    /// <c>[ [ &lt;ops&gt; ] ]</c> in a single <see cref="Utf8JsonWriter"/> pass
    /// straight into a pooled UTF-8 buffer. The sproc takes one parameter (the
    /// operations array), so the outer array wraps it. Each Put's pre-encoded
    /// document and each serialized condition AST are spliced as raw JSON via
    /// <see cref="Utf8JsonWriter.WriteRawValue(System.ReadOnlySpan{byte})"/> /
    /// the string overload — no <c>byte[] → string → byte[]</c> round-trip and
    /// no <c>StringContent</c> re-encode. Output is byte-identical to
    /// <c>"[" + <see cref="BuildOperationsJson"/> + "]"</c> (verified by tests).
    /// </summary>
    internal static PooledByteBufferWriter BuildTransactParamsBody(PreparedOp[] ops)
    {
        var buf = new PooledByteBufferWriter(512);
        try
        {
            using var w = new Utf8JsonWriter(buf);
            w.WriteStartArray();      // sproc parameter list
            WriteOperationsArray(w, ops);
            w.WriteEndArray();
            w.Flush();
        }
        catch
        {
            buf.Dispose();
            throw;
        }
        return buf;
    }

    private static void WriteOperationsArray(Utf8JsonWriter w, PreparedOp[] ops)
    {
        w.WriteStartArray();
        foreach (var op in ops)
        {
            w.WriteStartObject();
            w.WriteString("type", op.Kind switch
            {
                OpKind.Put => "PUT",
                OpKind.Delete => "DELETE",
                _ => "CHECK",
            });
            w.WriteString("id", op.Id);
            if (op.DocBytes is not null)
            {
                w.WritePropertyName("doc");
                w.WriteRawValue(op.DocBytes);
            }
            w.WritePropertyName("condition");
            if (op.ConditionJson is not null)
            {
                w.WriteRawValue(op.ConditionJson);
            }
            else
            {
                w.WriteNullValue();
            }
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    // Legacy string encoder for just the operations array, retained as the
    // byte-identity reference for <see cref="BuildTransactParamsBody"/> tests.
    // Not used on the request path (which is now single-pass / zero-copy).
    internal static string BuildOperationsJson(PreparedOp[] ops)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            WriteOperationsArray(w, ops);
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static async Task WriteTransactionCanceledAsync(HttpContext ctx, string? sprocBody, int opCount)
    {
        var codes = new string[opCount];
        for (int i = 0; i < opCount; i++)
        {
            codes[i] = "None";
        }

        if (!string.IsNullOrEmpty(sprocBody))
        {
            try
            {
                using var doc = JsonDocument.Parse(sprocBody);
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("reasons", out var reasons)
                    && reasons.ValueKind == JsonValueKind.Array)
                {
                    int i = 0;
                    foreach (var r in reasons.EnumerateArray())
                    {
                        if (i >= opCount) break;
                        if (r.ValueKind == JsonValueKind.Object
                            && r.TryGetProperty("code", out var cEl)
                            && cEl.ValueKind == JsonValueKind.String)
                        {
                            codes[i] = cEl.GetString() ?? "None";
                        }
                        i++;
                    }
                }
            }
            catch (JsonException)
            {
                // Fall back to all-None codes.
            }
        }

        using var ms = new MemoryStream();
        await using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("__type", "com.amazonaws.dynamodb.v20120810#TransactionCanceledException");
            w.WriteString("Message",
                "Transaction cancelled, please refer cancellation reasons for specific reasons [" + string.Join(", ", codes) + "].");
            w.WritePropertyName("CancellationReasons");
            w.WriteStartArray();
            foreach (var code in codes)
            {
                w.WriteStartObject();
                w.WriteString("Code", code);
                if (string.Equals(code, "ConditionalCheckFailed", StringComparison.Ordinal))
                {
                    w.WriteString("Message", "The conditional request failed");
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

    private static Task Reject(HttpContext ctx, string message)
        => CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException", message);
}
