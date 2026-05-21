using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Aws2Azure.Modules.DynamoDb.Internal;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.DynamoDb.Operations;

/// <summary>
/// Writes the AWS-compatible response for a failed conditional write:
/// HTTP 400 with type <c>ConditionalCheckFailedException</c>, plus the
/// optional <c>Item</c> property when the caller passed
/// <c>ReturnValuesOnConditionCheckFailure=ALL_OLD</c>.
/// </summary>
internal static class ConditionFailureResponder
{
    public static Task WriteAsync(
        HttpContext ctx,
        IReadOnlyDictionary<string, JsonElement>? existingItem,
        string returnValuesOnConditionCheckFailure)
    {
        var payload = new ConditionFailurePayload
        {
            Type = "com.amazonaws.dynamodb.v20120810#ConditionalCheckFailedException",
            Message = "The conditional request failed",
        };
        if (returnValuesOnConditionCheckFailure == "ALL_OLD" && existingItem is { Count: > 0 })
        {
            payload.Item = new Dictionary<string, JsonElement>(existingItem);
        }
        ctx.Response.StatusCode = 400;
        ctx.Response.ContentType = "application/x-amz-json-1.0";
        return JsonSerializer.SerializeAsync(
            ctx.Response.Body, payload, ItemJsonContext.Default.ConditionFailurePayload);
    }
}

internal sealed class ConditionFailurePayload
{
    [JsonPropertyName("__type")] public string? Type { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("Item")] public Dictionary<string, JsonElement>? Item { get; set; }
}
