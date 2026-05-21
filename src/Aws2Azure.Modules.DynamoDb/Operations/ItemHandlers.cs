using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Aws2Azure.Modules.DynamoDb.Internal;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.DynamoDb.Operations;

/// <summary>
/// Handlers for the DynamoDB item-level operations
/// (<c>PutItem</c>, <c>GetItem</c>, <c>DeleteItem</c>). Each handler:
///
/// <list type="number">
/// <item>Parses the JSON 1.0 request into the matching DTO.</item>
/// <item>Reads the table's sidecar metadata to learn the HASH/RANGE
///   attribute names + scalar types.</item>
/// <item>Routes the doc by formatting the key into Cosmos
///   <c>pk</c> / <c>id</c> scalars (HASH → <c>pk</c>, RANGE → <c>id</c>
///   when composite, else HASH → both).</item>
/// <item>Stores the full DynamoDB Item map verbatim under <c>item</c> so
///   reads round-trip without any type erosion.</item>
/// </list>
///
/// <para>Conditional writes, projection expressions, and ReturnValues
/// modes other than <c>NONE</c> are deferred to the expression-parser
/// slices and rejected here with <c>ValidationException</c> so callers
/// surface the gap explicitly.</para>
/// </summary>
internal static class ItemHandlers
{
    // Sentinel property names inside the Cosmos doc. Item attributes
    // live under "item" to keep them separate from the routing fields
    // (id / pk) and the future indexing fields (_a2a / _meta).
    internal const string ItemEnvelopeProperty = "item";
    internal const string DiscriminatorProperty = "_a2a";
    internal const string DiscriminatorValueItem = "item";

    public static Task HandlePutItemAsync(HttpContext ctx, byte[] body, CosmosClient cosmos, CancellationToken ct)
    {
        PutItemRequest? req;
        try
        {
            req = JsonSerializer.Deserialize(body, ItemJsonContext.Default.PutItemRequest);
        }
        catch (JsonException ex)
        {
            return CosmosOpsShared.WriteErrorAsync(ctx, 400, "SerializationException", ex.Message);
        }

        if (req is null || !DynamoDbNames.IsValidTableName(req.TableName))
        {
            return CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                "TableName is required and must match [a-zA-Z0-9_.-]{3,255}.");
        }

        if (req.Item.ValueKind != JsonValueKind.Object)
        {
            return CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                "Item is required and must be a JSON object.");
        }

        if (HasContent(req.ConditionExpression) || HasContent(req.Expected)
            || HasContent(req.ConditionalOperator)
            || HasContent(req.ExpressionAttributeNames) || HasContent(req.ExpressionAttributeValues))
        {
            return CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                "Conditional writes and expression attributes are not supported in this slice.");
        }

        if (!IsAllowedReturnValuesForWrite(req.ReturnValues, out var rvError))
        {
            return CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException", rvError);
        }

        return PutItemCoreAsync(ctx, req, cosmos, ct);
    }

    private static async Task PutItemCoreAsync(
        HttpContext ctx, PutItemRequest req, CosmosClient cosmos, CancellationToken ct)
    {
        var meta = await CosmosOpsShared.TryReadTableMetadataAsync(cosmos, req.TableName!, ct).ConfigureAwait(false);
        if (meta is null)
        {
            await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ResourceNotFoundException",
                $"Table not found: {req.TableName}").ConfigureAwait(false);
            return;
        }

        // Validate that key attribute type tags inside the Item match the
        // table's AttributeDefinitions before we touch Cosmos.
        if (!ValidateKeyAttributesInItem(req.Item, meta, out var validationError))
        {
            await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException", validationError).ConfigureAwait(false);
            return;
        }

        if (!ValidateItemShape(req.Item, out var shapeError))
        {
            await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException", shapeError).ConfigureAwait(false);
            return;
        }

        if (!ItemKeyFormatter.TryBuildFromItem(req.Item, meta, out var pk, out var id, out var keyError))
        {
            await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException", keyError).ConfigureAwait(false);
            return;
        }

        var docJson = BuildItemDocument(id, pk, req.Item);
        using var content = new StringContent(docJson, Encoding.UTF8, "application/json");
        var pkHeader = CosmosOpsShared.BuildPartitionKeyHeader(pk);
        var headers = new[]
        {
            new KeyValuePair<string, string>("x-ms-documentdb-partitionkey", pkHeader),
            new KeyValuePair<string, string>("x-ms-documentdb-is-upsert", "true"),
        };
        var collLink = "dbs/" + cosmos.DatabaseName + "/colls/" + req.TableName;
        using var resp = await cosmos.SendAsync(
            HttpMethod.Post, "docs", collLink, "/" + collLink + "/docs",
            content, headers, ct).ConfigureAwait(false);

        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            // Container exists at metadata-read time but vanished mid-op,
            // or the database link is wrong. Surface as table-not-found.
            await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ResourceNotFoundException",
                $"Table not found: {req.TableName}").ConfigureAwait(false);
            return;
        }

        if (!resp.IsSuccessStatusCode)
        {
            await CosmosOpsShared.WriteCosmosErrorAsync(ctx, resp, ct).ConfigureAwait(false);
            return;
        }

        // ReturnValues = NONE → DynamoDB returns an empty object.
        await CosmosOpsShared.WriteJsonAsync(ctx, 200, new PutItemResponse(),
            ItemJsonContext.Default.PutItemResponse).ConfigureAwait(false);
    }

    public static Task HandleGetItemAsync(HttpContext ctx, byte[] body, CosmosClient cosmos, CancellationToken ct)
    {
        GetItemRequest? req;
        try
        {
            req = JsonSerializer.Deserialize(body, ItemJsonContext.Default.GetItemRequest);
        }
        catch (JsonException ex)
        {
            return CosmosOpsShared.WriteErrorAsync(ctx, 400, "SerializationException", ex.Message);
        }

        if (req is null || !DynamoDbNames.IsValidTableName(req.TableName))
        {
            return CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                "TableName is required and must match [a-zA-Z0-9_.-]{3,255}.");
        }

        if (HasContent(req.AttributesToGet)
            || HasContent(req.ProjectionExpression)
            || HasContent(req.ExpressionAttributeNames))
        {
            return CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                "Projection / AttributesToGet are not supported in this slice.");
        }

        return GetItemCoreAsync(ctx, req, cosmos, ct);
    }

    private static async Task GetItemCoreAsync(
        HttpContext ctx, GetItemRequest req, CosmosClient cosmos, CancellationToken ct)
    {
        var meta = await CosmosOpsShared.TryReadTableMetadataAsync(cosmos, req.TableName!, ct).ConfigureAwait(false);
        if (meta is null)
        {
            await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ResourceNotFoundException",
                $"Table not found: {req.TableName}").ConfigureAwait(false);
            return;
        }

        if (!ValidateKeyAttributesInKey(req.Key, meta, out var validationError))
        {
            await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException", validationError).ConfigureAwait(false);
            return;
        }

        if (!ItemKeyFormatter.TryBuild(req.Key, meta, out var pk, out var id, out var keyError))
        {
            await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException", keyError).ConfigureAwait(false);
            return;
        }

        var docLink = "dbs/" + cosmos.DatabaseName + "/colls/" + req.TableName + "/docs/" + id;
        var pkHeader = CosmosOpsShared.BuildPartitionKeyHeader(pk);
        var headers = new List<KeyValuePair<string, string>>
        {
            new("x-ms-documentdb-partitionkey", pkHeader),
        };
        if (req.ConsistentRead == true)
        {
            // Cosmos has no perfect mapping for DynamoDB strong consistency,
            // but session-bound Strong-equivalent is selected via the
            // x-ms-consistency-level header for accounts that allow it.
            // Configured accounts at "Session" or below will simply ignore.
            headers.Add(new("x-ms-consistency-level", "Strong"));
        }

        using var resp = await cosmos.SendAsync(
            HttpMethod.Get, "docs", docLink, "/" + docLink,
            content: null, headers, ct).ConfigureAwait(false);

        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            // DynamoDB GetItem returns 200 with an empty body when the
            // item does not exist; consumers must not depend on a 404.
            await CosmosOpsShared.WriteJsonAsync(ctx, 200, new GetItemResponse(),
                ItemJsonContext.Default.GetItemResponse).ConfigureAwait(false);
            return;
        }

        if (!resp.IsSuccessStatusCode)
        {
            await CosmosOpsShared.WriteCosmosErrorAsync(ctx, resp, ct).ConfigureAwait(false);
            return;
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var item = ExtractItemFromCosmosDoc(stream);
        var response = new GetItemResponse { Item = item };
        await CosmosOpsShared.WriteJsonAsync(ctx, 200, response,
            ItemJsonContext.Default.GetItemResponse).ConfigureAwait(false);
    }

    public static Task HandleDeleteItemAsync(HttpContext ctx, byte[] body, CosmosClient cosmos, CancellationToken ct)
    {
        DeleteItemRequest? req;
        try
        {
            req = JsonSerializer.Deserialize(body, ItemJsonContext.Default.DeleteItemRequest);
        }
        catch (JsonException ex)
        {
            return CosmosOpsShared.WriteErrorAsync(ctx, 400, "SerializationException", ex.Message);
        }

        if (req is null || !DynamoDbNames.IsValidTableName(req.TableName))
        {
            return CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                "TableName is required and must match [a-zA-Z0-9_.-]{3,255}.");
        }

        if (HasContent(req.ConditionExpression) || HasContent(req.Expected)
            || HasContent(req.ConditionalOperator)
            || HasContent(req.ExpressionAttributeNames) || HasContent(req.ExpressionAttributeValues))
        {
            return CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                "Conditional deletes and expression attributes are not supported in this slice.");
        }

        if (!IsAllowedReturnValuesForWrite(req.ReturnValues, out var rvError))
        {
            return CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException", rvError);
        }

        return DeleteItemCoreAsync(ctx, req, cosmos, ct);
    }

    private static async Task DeleteItemCoreAsync(
        HttpContext ctx, DeleteItemRequest req, CosmosClient cosmos, CancellationToken ct)
    {
        var meta = await CosmosOpsShared.TryReadTableMetadataAsync(cosmos, req.TableName!, ct).ConfigureAwait(false);
        if (meta is null)
        {
            await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ResourceNotFoundException",
                $"Table not found: {req.TableName}").ConfigureAwait(false);
            return;
        }

        if (!ValidateKeyAttributesInKey(req.Key, meta, out var validationError))
        {
            await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException", validationError).ConfigureAwait(false);
            return;
        }

        if (!ItemKeyFormatter.TryBuild(req.Key, meta, out var pk, out var id, out var keyError))
        {
            await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException", keyError).ConfigureAwait(false);
            return;
        }

        var docLink = "dbs/" + cosmos.DatabaseName + "/colls/" + req.TableName + "/docs/" + id;
        var pkHeader = CosmosOpsShared.BuildPartitionKeyHeader(pk);
        var headers = new[]
        {
            new KeyValuePair<string, string>("x-ms-documentdb-partitionkey", pkHeader),
        };
        using var resp = await cosmos.SendAsync(
            HttpMethod.Delete, "docs", docLink, "/" + docLink,
            content: null, headers, ct).ConfigureAwait(false);

        // DynamoDB DeleteItem is idempotent: a missing item is a success.
        if (resp.StatusCode == HttpStatusCode.NotFound || resp.IsSuccessStatusCode)
        {
            await CosmosOpsShared.WriteJsonAsync(ctx, 200, new DeleteItemResponse(),
                ItemJsonContext.Default.DeleteItemResponse).ConfigureAwait(false);
            return;
        }

        await CosmosOpsShared.WriteCosmosErrorAsync(ctx, resp, ct).ConfigureAwait(false);
    }

    // ----- helpers ---------------------------------------------------

    /// <summary>
    /// Validates every attribute in the Item is a single-property typed
    /// value (per the DynamoDB JSON wire format). Catches malformed
    /// inputs early so a write can't poison the partition with a doc
    /// that GetItem cannot parse.
    /// </summary>
    private static bool ValidateItemShape(JsonElement item, out string error)
    {
        foreach (var prop in item.EnumerateObject())
        {
            if (!ParsedAttributeValue.TryParse(prop.Value, out _))
            {
                error = $"Attribute '{prop.Name}' must be a single-property typed attribute value.";
                return false;
            }
        }
        error = string.Empty;
        return true;
    }

    private static bool ValidateKeyAttributesInItem(JsonElement item, TableMetadata meta, out string error)
    {
        foreach (var k in meta.KeySchema)
        {
            if (!item.TryGetProperty(k.Name, out var attr))
            {
                error = $"Item is missing required key attribute '{k.Name}'.";
                return false;
            }
            if (!ItemKeyFormatter.ValidateKeyAttributeType(attr, meta, k.Name, out var typeError))
            {
                error = typeError;
                return false;
            }
        }
        error = string.Empty;
        return true;
    }

    private static bool ValidateKeyAttributesInKey(JsonElement key, TableMetadata meta, out string error)
    {
        if (key.ValueKind != JsonValueKind.Object)
        {
            error = "Key must be a JSON object.";
            return false;
        }
        foreach (var k in meta.KeySchema)
        {
            if (!key.TryGetProperty(k.Name, out var attr))
            {
                error = $"Key is missing required attribute '{k.Name}'.";
                return false;
            }
            if (!ItemKeyFormatter.ValidateKeyAttributeType(attr, meta, k.Name, out var typeError))
            {
                error = typeError;
                return false;
            }
        }
        error = string.Empty;
        return true;
    }

    /// <summary>
    /// Composes the Cosmos doc shape:
    /// <c>{"id":"&lt;id&gt;","pk":"&lt;pk&gt;","_a2a":"item","item":&lt;raw map&gt;}</c>.
    /// The raw item is written via <see cref="JsonElement.GetRawText"/>
    /// so the on-wire bytes are preserved (number precision intact).
    /// </summary>
    internal static string BuildItemDocument(string id, string pk, JsonElement item)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            writer.WriteString("id", id);
            writer.WriteString("pk", pk);
            writer.WriteString(DiscriminatorProperty, DiscriminatorValueItem);
            writer.WritePropertyName(ItemEnvelopeProperty);
            item.WriteTo(writer);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>
    /// Extracts the <c>item</c> envelope from a Cosmos doc, projecting
    /// each attribute as a <see cref="JsonElement"/> clone so the
    /// returned dictionary outlives the source document.
    /// </summary>
    internal static Dictionary<string, JsonElement>? ExtractItemFromCosmosDoc(Stream cosmosDocBody)
    {
        using var doc = JsonDocument.Parse(cosmosDocBody);
        if (!doc.RootElement.TryGetProperty(ItemEnvelopeProperty, out var envelope)
            || envelope.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var prop in envelope.EnumerateObject())
        {
            // Clone so the returned element is detached from the
            // JsonDocument we're about to dispose.
            result[prop.Name] = prop.Value.Clone();
        }
        return result;
    }

    private static bool HasContent(string? s) => !string.IsNullOrEmpty(s);
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

    private static bool IsAllowedReturnValuesForWrite(string? rv, out string error)
    {
        if (string.IsNullOrEmpty(rv) || rv == "NONE")
        {
            error = string.Empty;
            return true;
        }
        error = $"ReturnValues='{rv}' is not supported in this slice (only NONE).";
        return false;
    }
}
