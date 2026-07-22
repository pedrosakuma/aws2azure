using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Aws2Azure.Modules.DynamoDb.Internal;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.DynamoDb.Operations;

internal static class TaggingHandlers
{
    private const int MaxTagsPerTable = 50;
    private const int MaxTagKeyLength = 128;
    private const int MaxTagValueLength = 256;
    private const int MaxAggregateTagLength = 10 * 1024;
    private const int MaxConditionalWriteAttempts = 3;

    public static async Task HandleTagResourceAsync(
        HttpContext ctx, byte[] body, CosmosClient cosmos, CancellationToken ct)
    {
        TagResourceRequest? req;
        try
        {
            req = JsonSerializer.Deserialize(body, TaggingJsonContext.Default.TagResourceRequest);
        }
        catch (JsonException ex)
        {
            await CosmosOpsShared.WriteErrorAsync(ctx, 400, "SerializationException",
                "Malformed JSON: " + ex.Message).ConfigureAwait(false);
            return;
        }
        if (req is null || string.IsNullOrEmpty(req.ResourceArn))
        {
            await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                "ResourceArn is required.").ConfigureAwait(false);
            return;
        }
        if (req.Tags is null || req.Tags.Count == 0)
        {
            await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                "Tags is required and must contain at least one entry.").ConfigureAwait(false);
            return;
        }

        if (!TryParseTableName(req.ResourceArn, out var tableName, out var arnError))
        {
            await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException", arnError).ConfigureAwait(false);
            return;
        }
        if (!ValidateTags(req.Tags, out var tagError))
        {
            await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException", tagError).ConfigureAwait(false);
            return;
        }

        for (int attempt = 0; attempt < MaxConditionalWriteAttempts; attempt++)
        {
            var load = await LoadMetadataForMutationAsync(ctx, cosmos, tableName, ct).ConfigureAwait(false);
            if (load is null) return;

            var merged = load.Metadata.Tags is null
                ? new List<TableTag>(req.Tags.Count)
                : new List<TableTag>(load.Metadata.Tags);
            foreach (var tag in req.Tags)
            {
                int existing = FindTagIndex(merged, tag.Key!);
                if (existing >= 0)
                {
                    merged[existing].Value = tag.Value ?? string.Empty;
                }
                else
                {
                    merged.Add(new TableTag { Key = tag.Key!, Value = tag.Value ?? string.Empty });
                }
            }
            if (merged.Count > MaxTagsPerTable)
            {
                await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                    "A table can have up to 50 tags.").ConfigureAwait(false);
                return;
            }
            if (!ValidatePersistedTags(merged, out var mergedError))
            {
                await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException", mergedError).ConfigureAwait(false);
                return;
            }

            load.Metadata.Tags = merged;
            var write = await WriteMetadataAsync(ctx, cosmos, tableName, load.Metadata, load.ETag, ct).ConfigureAwait(false);
            if (write == MetadataWriteStatus.Success) break;
            if (write == MetadataWriteStatus.PreconditionFailed)
            {
                if (attempt + 1 < MaxConditionalWriteAttempts) continue;
                await WriteConflictExhaustedAsync(ctx).ConfigureAwait(false);
            }
            return;
        }

        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "application/x-amz-json-1.0";
    }

    public static async Task HandleUntagResourceAsync(
        HttpContext ctx, byte[] body, CosmosClient cosmos, CancellationToken ct)
    {
        UntagResourceRequest? req;
        try
        {
            req = JsonSerializer.Deserialize(body, TaggingJsonContext.Default.UntagResourceRequest);
        }
        catch (JsonException ex)
        {
            await CosmosOpsShared.WriteErrorAsync(ctx, 400, "SerializationException",
                "Malformed JSON: " + ex.Message).ConfigureAwait(false);
            return;
        }
        if (req is null || string.IsNullOrEmpty(req.ResourceArn))
        {
            await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                "ResourceArn is required.").ConfigureAwait(false);
            return;
        }
        if (req.TagKeys is null || req.TagKeys.Count == 0)
        {
            await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                "TagKeys is required and must contain at least one entry.").ConfigureAwait(false);
            return;
        }

        if (!TryParseTableName(req.ResourceArn, out var tableName, out var arnError))
        {
            await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException", arnError).ConfigureAwait(false);
            return;
        }
        if (!ValidateTagKeys(req.TagKeys, out var tagError))
        {
            await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException", tagError).ConfigureAwait(false);
            return;
        }

        for (int attempt = 0; attempt < MaxConditionalWriteAttempts; attempt++)
        {
            var load = await LoadMetadataForMutationAsync(ctx, cosmos, tableName, ct).ConfigureAwait(false);
            if (load is null) return;
            if (load.Metadata.Tags is not null && load.Metadata.Tags.Count > 0)
            {
                foreach (var key in req.TagKeys)
                {
                    int existing = FindTagIndex(load.Metadata.Tags, key);
                    if (existing >= 0) load.Metadata.Tags.RemoveAt(existing);
                }
                if (load.Metadata.Tags.Count == 0) load.Metadata.Tags = null;
                var write = await WriteMetadataAsync(ctx, cosmos, tableName, load.Metadata, load.ETag, ct).ConfigureAwait(false);
                if (write == MetadataWriteStatus.Success) break;
                if (write == MetadataWriteStatus.PreconditionFailed)
                {
                    if (attempt + 1 < MaxConditionalWriteAttempts) continue;
                    await WriteConflictExhaustedAsync(ctx).ConfigureAwait(false);
                }
                return;
            }
            break;
        }

        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "application/x-amz-json-1.0";
    }

    public static async Task HandleListTagsOfResourceAsync(
        HttpContext ctx, byte[] body, CosmosClient cosmos, CancellationToken ct)
    {
        ListTagsOfResourceRequest? req;
        try
        {
            req = JsonSerializer.Deserialize(body, TaggingJsonContext.Default.ListTagsOfResourceRequest);
        }
        catch (JsonException ex)
        {
            await CosmosOpsShared.WriteErrorAsync(ctx, 400, "SerializationException",
                "Malformed JSON: " + ex.Message).ConfigureAwait(false);
            return;
        }
        if (req is null || string.IsNullOrEmpty(req.ResourceArn))
        {
            await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                "ResourceArn is required.").ConfigureAwait(false);
            return;
        }
        if (!string.IsNullOrEmpty(req.NextToken))
        {
            await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                "NextToken is not supported for DynamoDB tag listing.").ConfigureAwait(false);
            return;
        }
        if (!TryParseTableName(req.ResourceArn, out var tableName, out var arnError))
        {
            await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException", arnError).ConfigureAwait(false);
            return;
        }

        var meta = await LoadMetadataForTaggingAsync(ctx, cosmos, tableName, ct).ConfigureAwait(false);
        if (meta is null) return;

        var tags = new List<DynamoDbTag>(meta.Tags?.Count ?? 0);
        if (meta.Tags is not null)
        {
            foreach (var tag in meta.Tags)
                tags.Add(new DynamoDbTag { Key = tag.Key, Value = tag.Value });
        }

        var resp = new ListTagsOfResourceResponse { Tags = tags };
        await CosmosOpsShared.WriteJsonAsync(ctx, 200, resp,
            TaggingJsonContext.Default.ListTagsOfResourceResponse).ConfigureAwait(false);
    }

    internal static bool TryParseTableName(string? resourceArn, out string tableName, out string error)
    {
        tableName = string.Empty;
        error = string.Empty;
        if (string.IsNullOrEmpty(resourceArn))
        {
            error = "ResourceArn is required.";
            return false;
        }

        if (!resourceArn.StartsWith("arn:", System.StringComparison.Ordinal))
        {
            if (DynamoDbNames.IsValidTableName(resourceArn))
            {
                tableName = resourceArn;
                return true;
            }
            error = "ResourceArn must be a DynamoDB table ARN or valid table name.";
            return false;
        }

        var parts = resourceArn.Split(':', 6);
        if (parts.Length != 6
            || parts[0] != "arn"
            || parts[2] != "dynamodb"
            || !parts[5].StartsWith("table/", System.StringComparison.Ordinal)
            || parts[5].Length == "table/".Length
            || parts[5].IndexOf('/', "table/".Length) >= 0)
        {
            error = "ResourceArn must have the form arn:aws:dynamodb:<region>:<account>:table/<TableName>.";
            return false;
        }

        tableName = parts[5]["table/".Length..];
        if (!DynamoDbNames.IsValidTableName(tableName))
        {
            tableName = string.Empty;
            error = "ResourceArn table name must match [a-zA-Z0-9_.-]{3,255}.";
            return false;
        }
        return true;
    }

    private static bool ValidateTags(List<DynamoDbTag> tags, out string error)
    {
        error = string.Empty;
        if (tags.Count > MaxTagsPerTable)
        {
            error = "Tags cannot contain more than 50 entries.";
            return false;
        }
        for (int i = 0; i < tags.Count; i++)
        {
            var tag = tags[i];
            if (!IsValidTagKey(tag.Key))
            {
                error = "Tag keys must be 1 to 128 characters.";
                return false;
            }
            if (tag.Value is not null && tag.Value.Length > MaxTagValueLength)
            {
                error = "Tag values must be at most 256 characters.";
                return false;
            }
            if (!HasValidTagCharacters(tag.Key!) || (tag.Value is not null && !HasValidTagCharacters(tag.Value)))
            {
                error = "Tag keys and values may contain Unicode letters, digits, spaces, and _ . : / = + - @.";
                return false;
            }
        }
        return true;
    }

    private static bool ValidateTagKeys(List<string> tagKeys, out string error)
    {
        error = string.Empty;
        if (tagKeys.Count > MaxTagsPerTable)
        {
            error = "TagKeys cannot contain more than 50 entries.";
            return false;
        }
        foreach (var key in tagKeys)
        {
            if (!IsValidTagKey(key))
            {
                error = "Tag keys must be 1 to 128 characters.";
                return false;
            }
            if (!HasValidTagCharacters(key))
            {
                error = "Tag keys may contain Unicode letters, digits, spaces, and _ . : / = + - @.";
                return false;
            }
        }
        return true;
    }

    private static bool IsValidTagKey(string? key)
        => !string.IsNullOrEmpty(key)
            && key.Length <= MaxTagKeyLength
            && !key.StartsWith("aws:", System.StringComparison.OrdinalIgnoreCase);

    private static bool ValidatePersistedTags(List<TableTag> tags, out string error)
    {
        error = string.Empty;
        int total = 0;
        foreach (var tag in tags)
        {
            total += tag.Key.Length + tag.Value.Length;
            if (total > MaxAggregateTagLength)
            {
                error = "The total size of all tag keys and values cannot exceed 10 KB.";
                return false;
            }
        }
        return true;
    }

    private static bool HasValidTagCharacters(string value)
    {
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch)) continue;
            if (ch is '_' or '.' or ':' or '/' or '=' or '+' or '-' or '@') continue;
            var category = char.GetUnicodeCategory(ch);
            if (category is UnicodeCategory.SpaceSeparator
                or UnicodeCategory.LineSeparator
                or UnicodeCategory.ParagraphSeparator)
                continue;
            return false;
        }
        return true;
    }

    private static int FindTagIndex(List<TableTag> tags, string key)
    {
        for (int i = 0; i < tags.Count; i++)
        {
            if (string.Equals(tags[i].Key, key, System.StringComparison.Ordinal))
                return i;
        }
        return -1;
    }

    private static async Task<TableMetadata?> LoadMetadataForTaggingAsync(
        HttpContext ctx, CosmosClient cosmos, string tableName, CancellationToken ct)
    {
        using var result = await CosmosOpsShared.TryReadTableMetadataAsync(cosmos, tableName, ct).ConfigureAwait(false);
        if (result.Status == CosmosOpsShared.TableMetadataReadStatus.Found)
            return CloneMetadata(result.Metadata!);
        if (result.Status == CosmosOpsShared.TableMetadataReadStatus.CosmosError)
        {
            await CosmosOpsShared.WriteCosmosErrorAsync(ctx, result.ErrorResponse!, ct).ConfigureAwait(false);
            return null;
        }

        var collLink = "dbs/" + cosmos.DatabaseName + "/colls/" + tableName;
        using var collResp = await cosmos.SendAsync(
            HttpMethod.Get, "colls", collLink, "/" + collLink,
            content: null, extraHeaders: null, ct).ConfigureAwait(false);
        if (collResp.StatusCode == HttpStatusCode.NotFound)
        {
            await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ResourceNotFoundException",
                $"Cannot do operations on a non-existent table: {tableName}").ConfigureAwait(false);
            return null;
        }
        if (!collResp.IsSuccessStatusCode)
        {
            await CosmosOpsShared.WriteCosmosErrorAsync(ctx, collResp, ct).ConfigureAwait(false);
            return null;
        }

        return new TableMetadata { TableName = tableName };
    }

    private static async Task<LoadedMetadata?> LoadMetadataForMutationAsync(
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
        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            var seeded = await LoadMetadataForTaggingAsync(ctx, cosmos, tableName, ct).ConfigureAwait(false);
            return seeded is null ? null : new LoadedMetadata(seeded, ETag: null);
        }
        if (!resp.IsSuccessStatusCode)
        {
            await CosmosOpsShared.WriteCosmosErrorAsync(ctx, resp, ct).ConfigureAwait(false);
            return null;
        }

        try
        {
            using var body = await CosmosOpsShared.ReadCosmosJsonBodyAsync(resp.Content, ct).ConfigureAwait(false);
            var meta = JsonSerializer.Deserialize(
                body.WrittenMemory.Span,
                TableMetadataJsonContext.Default.TableMetadata);
            if (meta is null)
            {
                await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ResourceNotFoundException",
                    $"Cannot do operations on a non-existent table: {tableName}").ConfigureAwait(false);
                return null;
            }
            meta.RemoveCosmosSystemExtensionData();
            string? etag = null;
            if (resp.Headers.ETag is not null)
            {
                etag = resp.Headers.ETag.Tag;
            }
            else if (resp.Headers.TryGetValues("ETag", out var etags))
            {
                foreach (var value in etags) { etag = value; break; }
            }
            return new LoadedMetadata(meta, etag);
        }
        catch (JsonException ex)
        {
            await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                "Malformed table metadata: " + ex.Message).ConfigureAwait(false);
            return null;
        }
    }

    private static TableMetadata CloneMetadata(TableMetadata source)
    {
        var json = JsonSerializer.Serialize(source, TableMetadataJsonContext.Default.TableMetadata);
        return JsonSerializer.Deserialize(json, TableMetadataJsonContext.Default.TableMetadata)
            ?? throw new InvalidOperationException("Could not clone table metadata.");
    }

    private static async Task<MetadataWriteStatus> WriteMetadataAsync(
        HttpContext ctx, CosmosClient cosmos, string tableName, TableMetadata meta, string? etag, CancellationToken ct)
    {
        if (etag is null)
            return await CreateMetadataAsync(ctx, cosmos, tableName, meta, ct).ConfigureAwait(false);

        var docLink = "dbs/" + cosmos.DatabaseName + "/colls/" + tableName + "/docs/" + TableMetadata.DocId;
        var metaJson = JsonSerializer.Serialize(meta, TableMetadataJsonContext.Default.TableMetadata);
        using var content = new StringContent(metaJson, Encoding.UTF8, "application/json");
        var pkHeader = CosmosOpsShared.BuildPartitionKeyHeader(TableMetadata.DocId);
        var headers = new[]
        {
            new KeyValuePair<string, string>("x-ms-documentdb-partitionkey", pkHeader),
            new KeyValuePair<string, string>("If-Match", etag),
        };
        using var resp = await cosmos.SendAsync(
            HttpMethod.Put, "docs", docLink, "/" + docLink,
            content, headers, ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.PreconditionFailed)
            return MetadataWriteStatus.PreconditionFailed;
        if (!resp.IsSuccessStatusCode)
        {
            await CosmosOpsShared.WriteCosmosErrorAsync(ctx, resp, ct).ConfigureAwait(false);
            return MetadataWriteStatus.Failed;
        }

        CosmosOpsShared.MetadataCache.Invalidate(cosmos.AccountEndpoint, cosmos.DatabaseName, tableName);
        return MetadataWriteStatus.Success;
    }

    private static async Task<MetadataWriteStatus> CreateMetadataAsync(
        HttpContext ctx, CosmosClient cosmos, string tableName, TableMetadata meta, CancellationToken ct)
    {
        var collLink = "dbs/" + cosmos.DatabaseName + "/colls/" + tableName;
        var metaJson = JsonSerializer.Serialize(meta, TableMetadataJsonContext.Default.TableMetadata);
        using var content = new StringContent(metaJson, Encoding.UTF8, "application/json");
        var pkHeader = CosmosOpsShared.BuildPartitionKeyHeader(TableMetadata.DocId);
        var headers = new[]
        {
            new KeyValuePair<string, string>("x-ms-documentdb-partitionkey", pkHeader),
        };
        using var resp = await cosmos.SendAsync(
            HttpMethod.Post, "docs", collLink, "/" + collLink + "/docs",
            content, headers, ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.Conflict)
            return MetadataWriteStatus.PreconditionFailed;
        if (!resp.IsSuccessStatusCode)
        {
            await CosmosOpsShared.WriteCosmosErrorAsync(ctx, resp, ct).ConfigureAwait(false);
            return MetadataWriteStatus.Failed;
        }

        CosmosOpsShared.MetadataCache.Invalidate(cosmos.AccountEndpoint, cosmos.DatabaseName, tableName);
        return MetadataWriteStatus.Success;
    }

    private static Task WriteConflictExhaustedAsync(HttpContext ctx)
        => CosmosOpsShared.WriteErrorAsync(ctx, 400, "ResourceInUseException",
            "Could not update table tags because the table metadata changed concurrently. Retry the request.");

    private enum MetadataWriteStatus { Success, PreconditionFailed, Failed }

    private sealed record LoadedMetadata(TableMetadata Metadata, string? ETag);
}

internal sealed class DynamoDbTag
{
    [JsonPropertyName("Key")] public string? Key { get; set; }
    [JsonPropertyName("Value")] public string? Value { get; set; }
}

internal sealed class TagResourceRequest
{
    [JsonPropertyName("ResourceArn")] public string? ResourceArn { get; set; }
    [JsonPropertyName("Tags")] public List<DynamoDbTag>? Tags { get; set; }
}

internal sealed class UntagResourceRequest
{
    [JsonPropertyName("ResourceArn")] public string? ResourceArn { get; set; }
    [JsonPropertyName("TagKeys")] public List<string>? TagKeys { get; set; }
}

internal sealed class ListTagsOfResourceRequest
{
    [JsonPropertyName("ResourceArn")] public string? ResourceArn { get; set; }
    [JsonPropertyName("NextToken")] public string? NextToken { get; set; }
}

internal sealed class ListTagsOfResourceResponse
{
    [JsonPropertyName("Tags")] public List<DynamoDbTag>? Tags { get; set; }
    [JsonPropertyName("NextToken")] public string? NextToken { get; set; }
}

[JsonSerializable(typeof(TagResourceRequest))]
[JsonSerializable(typeof(UntagResourceRequest))]
[JsonSerializable(typeof(ListTagsOfResourceRequest))]
[JsonSerializable(typeof(ListTagsOfResourceResponse))]
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    AllowTrailingCommas = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class TaggingJsonContext : JsonSerializerContext
{
}
