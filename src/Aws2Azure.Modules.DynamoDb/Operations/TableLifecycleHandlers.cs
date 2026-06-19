using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using Aws2Azure.Modules.DynamoDb.Errors;
using Aws2Azure.Modules.DynamoDb.Internal;
using Aws2Azure.Modules.DynamoDb.Persistence;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.DynamoDb.Operations;

/// <summary>
/// Handlers for the DynamoDB table-lifecycle operations
/// (<c>CreateTable</c>, <c>DeleteTable</c>, <c>DescribeTable</c>,
/// <c>ListTables</c>). Each handler:
///
/// <list type="number">
/// <item>Parses the JSON 1.0 request body into the matching DTO.</item>
/// <item>Validates DynamoDB-shaped invariants (table name format,
///   key schema arity, attribute references) and returns
///   <c>ValidationException</c> on failure.</item>
/// <item>Maps the operation to one or more Cosmos REST calls.</item>
/// <item>Renders the response in DynamoDB JSON 1.0 shape.</item>
/// </list>
///
/// <para>The sidecar <see cref="TableMetadata"/> document inside each
/// Cosmos container preserves the DynamoDB attribute names + types and
/// the original key schema so <c>DescribeTable</c> round-trips losslessly
/// even though Cosmos itself only stores a single <c>/pk</c> path.</para>
/// </summary>
internal static class TableLifecycleHandlers
{
    internal const string ProvisionedBillingMode = "PROVISIONED";
    internal const string PayPerRequestBillingMode = "PAY_PER_REQUEST";

    public static Task HandleCreateTableAsync(HttpContext ctx, byte[] body, CosmosClient cosmos, CancellationToken ct)
    {
        CreateTableRequest? req;
        try
        {
            req = JsonSerializer.Deserialize(body, TableLifecycleJsonContext.Default.CreateTableRequest);
        }
        catch (JsonException ex)
        {
            return WriteErrorAsync(ctx, 400, "SerializationException", ex.Message);
        }

        if (req is null || !DynamoDbNames.IsValidTableName(req.TableName))
        {
            return WriteErrorAsync(ctx, 400, "ValidationException",
                "TableName must match [a-zA-Z0-9_.-]{3,255}.");
        }

        if (HasNonEmptyArray(req.GlobalSecondaryIndexes))
        {
            return WriteErrorAsync(ctx, 400, "ValidationException",
                "GlobalSecondaryIndexes are not supported by the aws2azure proxy.");
        }
        if (HasNonEmptyArray(req.LocalSecondaryIndexes))
        {
            return WriteErrorAsync(ctx, 400, "ValidationException",
                "LocalSecondaryIndexes are not supported by the aws2azure proxy.");
        }

        if (!string.IsNullOrEmpty(req.BillingMode)
            && req.BillingMode != ProvisionedBillingMode
            && req.BillingMode != PayPerRequestBillingMode)
        {
            return WriteErrorAsync(ctx, 400, "ValidationException",
                $"BillingMode must be {ProvisionedBillingMode} or {PayPerRequestBillingMode}.");
        }

        if (req.KeySchema is null || req.KeySchema.Count is < 1 or > 2)
        {
            return WriteErrorAsync(ctx, 400, "ValidationException",
                "KeySchema must contain 1 (HASH only) or 2 (HASH + RANGE) elements.");
        }

        if (req.AttributeDefinitions is null || req.AttributeDefinitions.Count == 0)
        {
            return WriteErrorAsync(ctx, 400, "ValidationException",
                "AttributeDefinitions is required.");
        }

        if (!ValidateAttributeDefinitions(req.AttributeDefinitions, out var attrError))
        {
            return WriteErrorAsync(ctx, 400, "ValidationException", attrError);
        }

        if (!ValidateKeyConsistency(req.KeySchema, req.AttributeDefinitions, out var keyError))
        {
            return WriteErrorAsync(ctx, 400, "ValidationException", keyError);
        }

        return CreateTableCoreAsync(ctx, req, cosmos, ct);
    }

    private static async Task CreateTableCoreAsync(
        HttpContext ctx, CreateTableRequest req, CosmosClient cosmos, CancellationToken ct)
    {
        var dbLink = "dbs/" + cosmos.DatabaseName;
        var containerBody = BuildContainerBody(req.TableName!);
        using var content = new StringContent(containerBody, Encoding.UTF8, "application/json");

        using var createResp = await cosmos.SendAsync(
            HttpMethod.Post, "colls", dbLink, "/" + dbLink + "/colls",
            content, extraHeaders: null, ct).ConfigureAwait(false);

        if (createResp.StatusCode == HttpStatusCode.Conflict)
        {
            await WriteErrorAsync(ctx, 400, "ResourceInUseException",
                $"Table already exists: {req.TableName}").ConfigureAwait(false);
            return;
        }

        if (createResp.StatusCode == HttpStatusCode.NotFound)
        {
            // Database missing — surface a clear server-config error instead of
            // 500 so operators see the misconfiguration immediately.
            await WriteErrorAsync(ctx, 500, "InternalServerError",
                $"Cosmos database '{cosmos.DatabaseName}' does not exist.").ConfigureAwait(false);
            return;
        }

        if (!createResp.IsSuccessStatusCode)
        {
            await WriteCosmosErrorAsync(ctx, createResp, ct).ConfigureAwait(false);
            return;
        }

        // Persist the sidecar metadata so DescribeTable can rebuild the AWS shape.
        var meta = new TableMetadata
        {
            TableName = req.TableName!,
            CreationDateTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            BillingMode = req.BillingMode,
            AttributeDefinitions = MapAttributeDefinitions(req.AttributeDefinitions),
            KeySchema = MapKeySchema(req.KeySchema),
        };
        var metaJson = JsonSerializer.Serialize(meta, TableMetadataJsonContext.Default.TableMetadata);
        var collLink = dbLink + "/colls/" + req.TableName;
        using var metaContent = new StringContent(metaJson, Encoding.UTF8, "application/json");
        var pkHeader = CosmosOpsShared.BuildPartitionKeyHeader(TableMetadata.DocId);
        var metaHeaders = new[]
        {
            new KeyValuePair<string, string>("x-ms-documentdb-partitionkey", pkHeader),
            new KeyValuePair<string, string>("x-ms-documentdb-is-upsert", "true"),
        };
        using var metaResp = await cosmos.SendAsync(
            HttpMethod.Post, "docs", collLink, "/" + collLink + "/docs",
            metaContent, metaHeaders, ct).ConfigureAwait(false);

        if (!metaResp.IsSuccessStatusCode)
        {
            // Container created but metadata persist failed; best-effort
            // rollback to avoid an orphan container the operator can't
            // describe. Surface the underlying Cosmos error.
            using var rollback = await cosmos.SendAsync(
                HttpMethod.Delete, "colls", collLink, "/" + collLink,
                content: null, extraHeaders: null, ct).ConfigureAwait(false);
            await WriteCosmosErrorAsync(ctx, metaResp, ct).ConfigureAwait(false);
            return;
        }

        var resp = new CreateTableResponse
        {
            TableDescription = BuildTableDescription(meta, status: "ACTIVE"),
        };
        await WriteJsonAsync(ctx, 200, resp, TableLifecycleJsonContext.Default.CreateTableResponse).ConfigureAwait(false);
    }

    public static async Task HandleDeleteTableAsync(HttpContext ctx, byte[] body, CosmosClient cosmos, CancellationToken ct)
    {
        DeleteTableRequest? req;
        try
        {
            req = JsonSerializer.Deserialize(body, TableLifecycleJsonContext.Default.DeleteTableRequest);
        }
        catch (JsonException ex)
        {
            await WriteErrorAsync(ctx, 400, "SerializationException", ex.Message).ConfigureAwait(false);
            return;
        }

        if (req is null || !DynamoDbNames.IsValidTableName(req.TableName))
        {
            await WriteErrorAsync(ctx, 400, "ValidationException",
                "TableName must match [a-zA-Z0-9_.-]{3,255}.").ConfigureAwait(false);
            return;
        }

        var collLink = "dbs/" + cosmos.DatabaseName + "/colls/" + req.TableName;
        // Read the sidecar metadata BEFORE deleting so the AWS response can
        // echo the original key schema (DynamoDB DeleteTable returns the
        // TableDescription as it was just before deletion).
        var meta = await TryReadMetadataAsync(cosmos, req.TableName!, ct).ConfigureAwait(false);

        using var resp = await cosmos.SendAsync(
            HttpMethod.Delete, "colls", collLink, "/" + collLink,
            content: null, extraHeaders: null, ct).ConfigureAwait(false);

        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            await WriteErrorAsync(ctx, 400, "ResourceNotFoundException",
                $"Cannot do operations on a non-existent table: {req.TableName}").ConfigureAwait(false);
            return;
        }

        if (!resp.IsSuccessStatusCode)
        {
            await WriteCosmosErrorAsync(ctx, resp, ct).ConfigureAwait(false);
            return;
        }

        // Invalidate metadata cache after successful deletion
        CosmosOpsShared.MetadataCache.Invalidate(cosmos.AccountEndpoint, cosmos.DatabaseName, req.TableName!);

        var description = meta is not null
            ? BuildTableDescription(meta, status: "DELETING")
            : new TableDescription { TableName = req.TableName, TableStatus = "DELETING" };

        var dto = new DeleteTableResponse { TableDescription = description };
        await WriteJsonAsync(ctx, 200, dto, TableLifecycleJsonContext.Default.DeleteTableResponse).ConfigureAwait(false);
    }

    public static async Task HandleDescribeTableAsync(HttpContext ctx, byte[] body, CosmosClient cosmos, CancellationToken ct)
    {
        DescribeTableRequest? req;
        try
        {
            req = JsonSerializer.Deserialize(body, TableLifecycleJsonContext.Default.DescribeTableRequest);
        }
        catch (JsonException ex)
        {
            await WriteErrorAsync(ctx, 400, "SerializationException", ex.Message).ConfigureAwait(false);
            return;
        }

        if (req is null || !DynamoDbNames.IsValidTableName(req.TableName))
        {
            await WriteErrorAsync(ctx, 400, "ValidationException",
                "TableName must match [a-zA-Z0-9_.-]{3,255}.").ConfigureAwait(false);
            return;
        }

        var collLink = "dbs/" + cosmos.DatabaseName + "/colls/" + req.TableName;
        using var collResp = await cosmos.SendAsync(
            HttpMethod.Get, "colls", collLink, "/" + collLink,
            content: null, extraHeaders: null, ct).ConfigureAwait(false);

        if (collResp.StatusCode == HttpStatusCode.NotFound)
        {
            await WriteErrorAsync(ctx, 400, "ResourceNotFoundException",
                $"Cannot do operations on a non-existent table: {req.TableName}").ConfigureAwait(false);
            return;
        }
        if (!collResp.IsSuccessStatusCode)
        {
            await WriteCosmosErrorAsync(ctx, collResp, ct).ConfigureAwait(false);
            return;
        }

        // Fetch metadata sidecar; tolerate its absence (e.g. container created
        // out-of-band by an operator) by synthesising a minimal description.
        var meta = await TryReadMetadataAsync(cosmos, req.TableName!, ct).ConfigureAwait(false);
        meta ??= new TableMetadata { TableName = req.TableName! };

        var resp = new DescribeTableResponse
        {
            Table = BuildTableDescription(meta, status: "ACTIVE"),
        };
        await WriteJsonAsync(ctx, 200, resp, TableLifecycleJsonContext.Default.DescribeTableResponse).ConfigureAwait(false);
    }

    public static async Task HandleListTablesAsync(HttpContext ctx, byte[] body, CosmosClient cosmos, CancellationToken ct)
    {
        ListTablesRequest? req = null;
        if (body.Length > 0)
        {
            try
            {
                req = JsonSerializer.Deserialize(body, TableLifecycleJsonContext.Default.ListTablesRequest);
            }
            catch (JsonException ex)
            {
                await WriteErrorAsync(ctx, 400, "SerializationException", ex.Message).ConfigureAwait(false);
                return;
            }
        }

        var limit = req?.Limit ?? 100;
        if (limit is < 1 or > 100)
        {
            await WriteErrorAsync(ctx, 400, "ValidationException",
                "Limit must be between 1 and 100.").ConfigureAwait(false);
            return;
        }

        var dbLink = "dbs/" + cosmos.DatabaseName;
        var names = new List<string>();
        string? continuation = null;

        // Cosmos read-feed paginates via x-ms-continuation; loop until the
        // feed is exhausted so DynamoDB's ListTables sees a complete view
        // before we apply ExclusiveStartTableName / Limit slicing.
        do
        {
            IReadOnlyList<KeyValuePair<string, string>>? headers = null;
            if (!string.IsNullOrEmpty(continuation))
            {
                headers = new[]
                {
                    new KeyValuePair<string, string>("x-ms-continuation", continuation),
                };
            }

            using var listResp = await cosmos.SendAsync(
                HttpMethod.Get, "colls", dbLink, "/" + dbLink + "/colls",
                content: null, headers, ct).ConfigureAwait(false);

            if (!listResp.IsSuccessStatusCode)
            {
                await WriteCosmosErrorAsync(ctx, listResp, ct).ConfigureAwait(false);
                return;
            }

            var stream = await listResp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            ParseContainerNamesInto(stream, names);

            continuation = null;
            if (listResp.Headers.TryGetValues("x-ms-continuation", out var values))
            {
                foreach (var v in values)
                {
                    if (!string.IsNullOrEmpty(v)) { continuation = v; break; }
                }
            }
        }
        while (!string.IsNullOrEmpty(continuation));

        names.Sort(StringComparer.Ordinal);

        // ExclusiveStartTableName cursor: skip names <= start.
        int startIndex = 0;
        if (!string.IsNullOrEmpty(req?.ExclusiveStartTableName))
        {
            // Binary-search-style: find first name strictly greater than the cursor.
            for (int i = 0; i < names.Count; i++)
            {
                if (StringComparer.Ordinal.Compare(names[i], req!.ExclusiveStartTableName!) > 0)
                {
                    startIndex = i;
                    break;
                }
                if (i == names.Count - 1) startIndex = names.Count;
            }
        }

        var page = new List<string>(Math.Min(limit, names.Count - startIndex));
        for (int i = startIndex; i < names.Count && page.Count < limit; i++)
        {
            page.Add(names[i]);
        }

        string? lastEvaluated = null;
        if (startIndex + page.Count < names.Count && page.Count > 0)
        {
            lastEvaluated = page[^1];
        }

        var dto = new ListTablesResponse
        {
            TableNames = page,
            LastEvaluatedTableName = lastEvaluated,
        };
        await WriteJsonAsync(ctx, 200, dto, TableLifecycleJsonContext.Default.ListTablesResponse).ConfigureAwait(false);
    }

    // ----- helpers ---------------------------------------------------

    private static bool ValidateAttributeDefinitions(
        List<AttributeDefinitionDto> attrs, out string error)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var a in attrs)
        {
            if (string.IsNullOrEmpty(a.AttributeName))
            {
                error = "AttributeDefinitions entries must include AttributeName.";
                return false;
            }
            if (a.AttributeType is not ("S" or "N" or "B"))
            {
                error = $"AttributeDefinitions[{a.AttributeName}].AttributeType must be one of S, N, B.";
                return false;
            }
            if (!seen.Add(a.AttributeName))
            {
                error = $"AttributeDefinitions contains duplicate AttributeName '{a.AttributeName}'.";
                return false;
            }
        }
        error = string.Empty;
        return true;
    }

    private static bool ValidateKeyConsistency(
        List<KeySchemaElementDto> keys,
        List<AttributeDefinitionDto> attrs,
        out string error)
    {
        // Order matters: KeySchema[0] must be HASH, KeySchema[1] (if any) must be RANGE.
        for (int i = 0; i < keys.Count; i++)
        {
            var k = keys[i];
            if (string.IsNullOrEmpty(k.AttributeName) || string.IsNullOrEmpty(k.KeyType))
            {
                error = "KeySchema entries must include AttributeName and KeyType.";
                return false;
            }
            var expected = i == 0 ? "HASH" : "RANGE";
            if (!string.Equals(k.KeyType, expected, StringComparison.Ordinal))
            {
                error = i == 0
                    ? "KeySchema[0].KeyType must be HASH."
                    : "KeySchema[1].KeyType must be RANGE.";
                return false;
            }
        }

        if (keys.Count == 2
            && string.Equals(keys[0].AttributeName, keys[1].AttributeName, StringComparison.Ordinal))
        {
            error = "KeySchema HASH and RANGE attributes must differ.";
            return false;
        }

        // Every key must reference a declared attribute, and — because
        // indexes are not yet supported — every declared attribute must
        // be used by the key schema (no orphan definitions).
        var keyNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var k in keys) keyNames.Add(k.AttributeName!);

        foreach (var k in keys)
        {
            bool found = false;
            foreach (var a in attrs)
            {
                if (string.Equals(a.AttributeName, k.AttributeName, StringComparison.Ordinal))
                {
                    found = true;
                    break;
                }
            }
            if (!found)
            {
                error = $"KeySchema attribute '{k.AttributeName}' has no matching AttributeDefinition.";
                return false;
            }
        }

        foreach (var a in attrs)
        {
            if (!keyNames.Contains(a.AttributeName!))
            {
                error = $"AttributeDefinition '{a.AttributeName}' is not referenced by KeySchema "
                        + "(secondary indexes are not yet supported, so extra definitions are not allowed).";
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    private static bool HasNonEmptyArray(JsonElement? value)
    {
        if (value is not { } v) return false;
        return v.ValueKind == JsonValueKind.Array && v.GetArrayLength() > 0;
    }

    private static string BuildContainerBody(string tableName)
    {
        // Fixed /_a2a_pk partition path: see InferredAttributeStorage
        // for why the routing field is namespaced under "_a2a" — it
        // frees the bare attribute names "pk" / "sk" for user data.
        // tableName is already validated to be ASCII [a-zA-Z0-9_.-]{3,255}
        // so no JSON-escape is required for it specifically.
        return "{\"id\":\"" + tableName
            + "\",\"partitionKey\":{\"paths\":[\"/" + InferredAttributeStorage.PkProperty + "\"],\"kind\":\"Hash\"}}";
    }

    private static List<TableAttributeDefinition> MapAttributeDefinitions(List<AttributeDefinitionDto>? src)
    {
        if (src is null) return new List<TableAttributeDefinition>();
        var dst = new List<TableAttributeDefinition>(src.Count);
        foreach (var a in src)
        {
            dst.Add(new TableAttributeDefinition { Name = a.AttributeName ?? string.Empty, Type = a.AttributeType ?? string.Empty });
        }
        return dst;
    }

    private static List<TableKeySchemaElement> MapKeySchema(List<KeySchemaElementDto>? src)
    {
        if (src is null) return new List<TableKeySchemaElement>();
        var dst = new List<TableKeySchemaElement>(src.Count);
        foreach (var k in src)
        {
            dst.Add(new TableKeySchemaElement { Name = k.AttributeName ?? string.Empty, KeyType = k.KeyType ?? string.Empty });
        }
        return dst;
    }

    private static TableDescription BuildTableDescription(TableMetadata meta, string status)
    {
        var attrs = new List<AttributeDefinitionDto>(meta.AttributeDefinitions.Count);
        foreach (var a in meta.AttributeDefinitions)
            attrs.Add(new AttributeDefinitionDto { AttributeName = a.Name, AttributeType = a.Type });
        var keys = new List<KeySchemaElementDto>(meta.KeySchema.Count);
        foreach (var k in meta.KeySchema)
            keys.Add(new KeySchemaElementDto { AttributeName = k.Name, KeyType = k.KeyType });

        return new TableDescription
        {
            TableName = meta.TableName,
            TableStatus = status,
            CreationDateTime = meta.CreationDateTime > 0 ? meta.CreationDateTime : null,
            AttributeDefinitions = attrs.Count > 0 ? attrs : null,
            KeySchema = keys.Count > 0 ? keys : null,
            TableArn = DynamoDbNames.BuildTableArn(string.Empty, meta.TableName),
            BillingModeSummary = string.IsNullOrEmpty(meta.BillingMode)
                ? null
                : new BillingModeSummary { BillingMode = meta.BillingMode },
        };
    }

    private static async Task<TableMetadata?> TryReadMetadataAsync(CosmosClient cosmos, string tableName, CancellationToken ct)
    {
        using var result = await CosmosOpsShared.TryReadTableMetadataAsync(cosmos, tableName, ct).ConfigureAwait(false);
        // Lifecycle handlers only need the metadata when present; they
        // already issue the authoritative container call separately and
        // surface 429/auth failures from that path. Treat any non-Found
        // outcome (NotFound or CosmosError) as "no sidecar available".
        return result.Status == CosmosOpsShared.TableMetadataReadStatus.Found ? result.Metadata : null;
    }

    internal static List<string> ParseContainerNames(Stream cosmosListBody)
    {
        var names = new List<string>();
        ParseContainerNamesInto(cosmosListBody, names);
        return names;
    }

    internal static void ParseContainerNamesInto(Stream cosmosListBody, List<string> names)
    {
        // Cosmos returns: { "_rid":"...", "DocumentCollections":[ {"id":"name", ...}, ... ], "_count":N }
        using var doc = JsonDocument.Parse(cosmosListBody);
        if (!doc.RootElement.TryGetProperty("DocumentCollections", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return;
        foreach (var item in arr.EnumerateArray())
        {
            if (item.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
            {
                var id = idEl.GetString();
                if (!string.IsNullOrEmpty(id)) names.Add(id);
            }
        }
    }

    private static Task WriteCosmosErrorAsync(HttpContext ctx, HttpResponseMessage cosmosResp, CancellationToken ct)
        => CosmosOpsShared.WriteCosmosErrorAsync(ctx, cosmosResp, ct);

    private static Task WriteErrorAsync(HttpContext ctx, int status, string code, string message)
        => CosmosOpsShared.WriteErrorAsync(ctx, status, code, message);

    private static Task WriteJsonAsync<T>(HttpContext ctx, int status, T payload, JsonTypeInfo<T> typeInfo)
        where T : class
        => CosmosOpsShared.WriteJsonAsync(ctx, status, payload, typeInfo);
}
