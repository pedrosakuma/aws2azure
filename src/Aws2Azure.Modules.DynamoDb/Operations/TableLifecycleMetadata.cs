using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Aws2Azure.Modules.DynamoDb.Internal;
using Aws2Azure.Modules.DynamoDb.Persistence;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.DynamoDb.Operations;

internal static partial class TableLifecycleHandlers
{
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

    private static List<TableIndexDefinition>? MapSecondaryIndexes(List<SecondaryIndexDto>? src)
    {
        if (src is null || src.Count == 0) return null;
        var dst = new List<TableIndexDefinition>(src.Count);
        foreach (var idx in src)
        {
            dst.Add(new TableIndexDefinition
            {
                IndexName = idx.IndexName ?? string.Empty,
                KeySchema = MapKeySchema(idx.KeySchema),
                ProjectionType = string.IsNullOrEmpty(idx.Projection?.ProjectionType)
                    ? "ALL"
                    : idx.Projection!.ProjectionType!,
                NonKeyAttributes = idx.Projection?.NonKeyAttributes is { Count: > 0 } nk
                    ? new List<string>(nk)
                    : null,
            });
        }
        return dst;
    }

    private static async Task<bool> PersistCreateTableMetadataAsync(
        HttpContext ctx, CosmosClient cosmos, string tableName, TableMetadata meta, CancellationToken ct)
    {
        using var createResp = await CreateMetadataAsync(cosmos, tableName, meta, ct).ConfigureAwait(false);
        if (createResp.IsSuccessStatusCode)
        {
            CosmosOpsShared.MetadataCache.Invalidate(cosmos.AccountEndpoint, cosmos.DatabaseName, tableName);
            return true;
        }
        if (createResp.StatusCode != HttpStatusCode.Conflict)
        {
            await WriteCosmosErrorAsync(ctx, createResp, ct).ConfigureAwait(false);
            return false;
        }

        const int maxConditionalWriteAttempts = 3;
        for (int attempt = 0; attempt < maxConditionalWriteAttempts; attempt++)
        {
            var existing = await ReadMetadataForReplaceAsync(ctx, cosmos, tableName, ct).ConfigureAwait(false);
            if (existing is null) return false;

            meta.Tags = CloneTags(existing.Metadata.Tags);
            var replaceStatus = await ReplaceMetadataAsync(cosmos, tableName, meta, existing.ETag, ct).ConfigureAwait(false);
            if (replaceStatus.StatusCode == HttpStatusCode.PreconditionFailed)
            {
                replaceStatus.Dispose();
                if (attempt + 1 < maxConditionalWriteAttempts) continue;
                await WriteErrorAsync(ctx, 400, "ResourceInUseException",
                    "Could not persist table metadata because table tags changed concurrently. Retry the request.")
                    .ConfigureAwait(false);
                return false;
            }
            using (replaceStatus)
            {
                if (!replaceStatus.IsSuccessStatusCode)
                {
                    await WriteCosmosErrorAsync(ctx, replaceStatus, ct).ConfigureAwait(false);
                    return false;
                }
            }

            CosmosOpsShared.MetadataCache.Invalidate(cosmos.AccountEndpoint, cosmos.DatabaseName, tableName);
            return true;
        }

        return false;
    }

    private static async Task<HttpResponseMessage> CreateMetadataAsync(
        CosmosClient cosmos, string tableName, TableMetadata meta, CancellationToken ct)
    {
        var collLink = "dbs/" + cosmos.DatabaseName + "/colls/" + tableName;
        var metaJson = JsonSerializer.Serialize(meta, TableMetadataJsonContext.Default.TableMetadata);
        using var metaContent = new StringContent(metaJson, Encoding.UTF8, "application/json");
        var pkHeader = CosmosOpsShared.BuildPartitionKeyHeader(TableMetadata.DocId);
        var metaHeaders = new[]
        {
            new KeyValuePair<string, string>("x-ms-documentdb-partitionkey", pkHeader),
        };
        return await cosmos.SendAsync(
            HttpMethod.Post, "docs", collLink, "/" + collLink + "/docs",
            metaContent, metaHeaders, ct).ConfigureAwait(false);
    }

    private static async Task<LoadedMetadataForReplace?> ReadMetadataForReplaceAsync(
        HttpContext ctx, CosmosClient cosmos, string tableName, CancellationToken ct)
    {
        var docLink = "dbs/" + cosmos.DatabaseName + "/colls/" + tableName + "/docs/" + TableMetadata.DocId;
        var pkHeader = CosmosOpsShared.BuildPartitionKeyHeader(TableMetadata.DocId);
        var headers = new[]
        {
            new KeyValuePair<string, string>("x-ms-documentdb-partitionkey", pkHeader),
        };
        using var resp = await cosmos.SendAsync(
            HttpMethod.Get, "docs", docLink, "/" + docLink,
            content: null, headers, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            await WriteCosmosErrorAsync(ctx, resp, ct).ConfigureAwait(false);
            return null;
        }

        string? etag = null;
        if (resp.Headers.ETag is not null)
        {
            etag = resp.Headers.ETag.Tag;
        }
        else if (resp.Headers.TryGetValues("ETag", out var etags))
        {
            foreach (var value in etags) { etag = value; break; }
        }
        if (string.IsNullOrEmpty(etag))
        {
            await WriteErrorAsync(ctx, 500, "InternalServerError",
                "Cosmos metadata document did not include an ETag.").ConfigureAwait(false);
            return null;
        }

        try
        {
            using var body = await CosmosOpsShared.ReadCosmosJsonBodyAsync(resp.Content, ct).ConfigureAwait(false);
            var existing = JsonSerializer.Deserialize(
                body.WrittenMemory.Span,
                TableMetadataJsonContext.Default.TableMetadata);
            if (existing is null)
            {
                await WriteErrorAsync(ctx, 500, "InternalServerError",
                    "Cosmos metadata document was empty.").ConfigureAwait(false);
                return null;
            }
            return new LoadedMetadataForReplace(existing, etag);
        }
        catch (JsonException ex)
        {
            await WriteErrorAsync(ctx, 500, "InternalServerError",
                "Malformed table metadata: " + ex.Message).ConfigureAwait(false);
            return null;
        }
    }

    private static async Task<HttpResponseMessage> ReplaceMetadataAsync(
        CosmosClient cosmos, string tableName, TableMetadata meta, string etag, CancellationToken ct)
    {
        var docLink = "dbs/" + cosmos.DatabaseName + "/colls/" + tableName + "/docs/" + TableMetadata.DocId;
        var metaJson = JsonSerializer.Serialize(meta, TableMetadataJsonContext.Default.TableMetadata);
        using var metaContent = new StringContent(metaJson, Encoding.UTF8, "application/json");
        var pkHeader = CosmosOpsShared.BuildPartitionKeyHeader(TableMetadata.DocId);
        var headers = new[]
        {
            new KeyValuePair<string, string>("x-ms-documentdb-partitionkey", pkHeader),
            new KeyValuePair<string, string>("If-Match", etag),
        };
        return await cosmos.SendAsync(
            HttpMethod.Put, "docs", docLink, "/" + docLink,
            metaContent, headers, ct).ConfigureAwait(false);
    }

    private static List<TableTag>? CloneTags(List<TableTag>? source)
    {
        if (source is null) return null;
        var clone = new List<TableTag>(source.Count);
        foreach (var tag in source)
        {
            clone.Add(new TableTag { Key = tag.Key, Value = tag.Value });
        }
        return clone;
    }

    private sealed record LoadedMetadataForReplace(TableMetadata Metadata, string ETag);
}
