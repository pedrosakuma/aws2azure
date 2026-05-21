using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Aws2Azure.Modules.DynamoDb.Internal;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.DynamoDb.Operations;

/// <summary>
/// DynamoDB resource tagging stubs. Tags have no first-class equivalent
/// in Azure Cosmos DB control plane that's reachable through the data
/// plane endpoint we proxy here, and AWS SDK callers routinely tag
/// tables on creation as a bookkeeping side-effect rather than relying
/// on tag-based queries.
///
/// <para>
/// To keep those callers happy without persisting any state, the proxy:
/// <list type="bullet">
///   <item>Accepts <c>TagResource</c> / <c>UntagResource</c> calls with a
///   200 empty response (after validating the ResourceArn shape).</item>
///   <item>Returns an empty <c>{Tags: []}</c> from
///   <c>ListTagsOfResource</c>.</item>
/// </list>
/// All three are flagged as <c>stub</c> in the gap docs so callers know
/// the tags are not persisted nor honoured by Azure billing.
/// </para>
/// </summary>
internal static class TaggingHandlers
{
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
        // Stub: discard tags. Empty 200 body is the documented success shape.
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
        // Stub: nothing to untag because we never stored anything.
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
        var resp = new ListTagsOfResourceResponse { Tags = new List<DynamoDbTag>() };
        await CosmosOpsShared.WriteJsonAsync(ctx, 200, resp,
            TaggingJsonContext.Default.ListTagsOfResourceResponse).ConfigureAwait(false);
    }
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
