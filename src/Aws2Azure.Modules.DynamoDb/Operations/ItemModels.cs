using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aws2Azure.Modules.DynamoDb.Internal;

namespace Aws2Azure.Modules.DynamoDb.Operations;

/// <summary>
/// DynamoDB item-op request/response DTOs. The proxy parses these via
/// source-gen contexts. Attribute maps stay as raw <see cref="JsonElement"/>
/// so the original wire form (type-tagged single-property objects)
/// round-trips through Cosmos without re-encoding.
/// </summary>
internal sealed class PutItemRequest
{
    [JsonPropertyName("TableName")] public string? TableName { get; set; }

    // Captured as a byte range into the request buffer rather than a
    // materialized JsonElement: the deserializer skips the value (no retained
    // per-request JsonDocument DOM), and the handler recovers the raw item
    // bytes for the single-pass encoder + a short-lived pooled parse for the
    // shape/key validators. Cuts PutItem write-path allocation ~84-96%.
    [JsonPropertyName("Item")]
    [JsonConverter(typeof(JsonRangeConverter))]
    public JsonRange Item { get; set; }

    [JsonPropertyName("ReturnValues")] public string? ReturnValues { get; set; }

    // Modeled but accepted silently: the proxy never returns
    // ConsumedCapacity / ItemCollectionMetrics in this slice. Surfaced
    // explicitly in gap docs.
    [JsonPropertyName("ReturnConsumedCapacity")] public string? ReturnConsumedCapacity { get; set; }
    [JsonPropertyName("ReturnItemCollectionMetrics")] public string? ReturnItemCollectionMetrics { get; set; }

    // Fields the proxy explicitly rejects until later slices wire in the
    // expression parser. Modeled here so the handler can detect their
    // presence instead of silently dropping them.
    [JsonPropertyName("ConditionExpression")] public string? ConditionExpression { get; set; }
    [JsonPropertyName("Expected")] public JsonElement? Expected { get; set; }
    [JsonPropertyName("ConditionalOperator")] public string? ConditionalOperator { get; set; }
    [JsonPropertyName("ReturnValuesOnConditionCheckFailure")] public string? ReturnValuesOnConditionCheckFailure { get; set; }
    [JsonPropertyName("ExpressionAttributeNames")] public JsonElement? ExpressionAttributeNames { get; set; }
    [JsonPropertyName("ExpressionAttributeValues")] public JsonElement? ExpressionAttributeValues { get; set; }
}

internal sealed class GetItemRequest
{
    [JsonPropertyName("TableName")] public string? TableName { get; set; }
    [JsonPropertyName("Key")] public JsonElement Key { get; set; }
    [JsonPropertyName("ConsistentRead")] public bool? ConsistentRead { get; set; }
    [JsonPropertyName("ReturnConsumedCapacity")] public string? ReturnConsumedCapacity { get; set; }

    [JsonPropertyName("AttributesToGet")] public JsonElement? AttributesToGet { get; set; }
    [JsonPropertyName("ProjectionExpression")] public string? ProjectionExpression { get; set; }
    [JsonPropertyName("ExpressionAttributeNames")] public JsonElement? ExpressionAttributeNames { get; set; }
}

internal sealed class DeleteItemRequest
{
    [JsonPropertyName("TableName")] public string? TableName { get; set; }
    [JsonPropertyName("Key")] public JsonElement Key { get; set; }
    [JsonPropertyName("ReturnValues")] public string? ReturnValues { get; set; }
    [JsonPropertyName("ReturnConsumedCapacity")] public string? ReturnConsumedCapacity { get; set; }
    [JsonPropertyName("ReturnItemCollectionMetrics")] public string? ReturnItemCollectionMetrics { get; set; }

    [JsonPropertyName("ConditionExpression")] public string? ConditionExpression { get; set; }
    [JsonPropertyName("Expected")] public JsonElement? Expected { get; set; }
    [JsonPropertyName("ConditionalOperator")] public string? ConditionalOperator { get; set; }
    [JsonPropertyName("ReturnValuesOnConditionCheckFailure")] public string? ReturnValuesOnConditionCheckFailure { get; set; }
    [JsonPropertyName("ExpressionAttributeNames")] public JsonElement? ExpressionAttributeNames { get; set; }
    [JsonPropertyName("ExpressionAttributeValues")] public JsonElement? ExpressionAttributeValues { get; set; }
}

/// <summary>
/// UpdateItem request. Two ways callers express the mutation:
/// <list type="bullet">
/// <item><c>UpdateExpression</c> (modern, e.g. <c>SET a = :v REMOVE b</c>)
///   with <c>ExpressionAttributeValues</c> + optional
///   <c>ExpressionAttributeNames</c>.</item>
/// <item><c>AttributeUpdates</c> (legacy map of
///   <c>{name: {Action: PUT|DELETE, Value: {...}}}</c>).</item>
/// </list>
/// Exactly one form must be present. Conditional knobs and any
/// arithmetic / list / function-call grammar are deferred to the
/// expression-parser slice.
/// </summary>
internal sealed class UpdateItemRequest
{
    [JsonPropertyName("TableName")] public string? TableName { get; set; }
    [JsonPropertyName("Key")] public JsonElement Key { get; set; }
    [JsonPropertyName("UpdateExpression")] public string? UpdateExpression { get; set; }
    [JsonPropertyName("AttributeUpdates")] public JsonElement? AttributeUpdates { get; set; }
    [JsonPropertyName("ExpressionAttributeNames")] public JsonElement? ExpressionAttributeNames { get; set; }
    [JsonPropertyName("ExpressionAttributeValues")] public JsonElement? ExpressionAttributeValues { get; set; }
    [JsonPropertyName("ReturnValues")] public string? ReturnValues { get; set; }
    [JsonPropertyName("ReturnConsumedCapacity")] public string? ReturnConsumedCapacity { get; set; }
    [JsonPropertyName("ReturnItemCollectionMetrics")] public string? ReturnItemCollectionMetrics { get; set; }

    // Rejected pending the expression-parser slice.
    [JsonPropertyName("ConditionExpression")] public string? ConditionExpression { get; set; }
    [JsonPropertyName("Expected")] public JsonElement? Expected { get; set; }
    [JsonPropertyName("ConditionalOperator")] public string? ConditionalOperator { get; set; }
    [JsonPropertyName("ReturnValuesOnConditionCheckFailure")] public string? ReturnValuesOnConditionCheckFailure { get; set; }
}

/// <summary>
/// PutItem response. DynamoDB normally returns an empty object except
/// when <c>ReturnValues = ALL_OLD</c>. The proxy only supports NONE in
/// this slice; the <c>Attributes</c> property is reserved for the
/// future ReturnValues plumbing.
/// </summary>
internal sealed class PutItemResponse
{
    [JsonPropertyName("Attributes")] public Dictionary<string, JsonElement>? Attributes { get; set; }
}

internal sealed class GetItemResponse
{
    [JsonPropertyName("Item")] public Dictionary<string, JsonElement>? Item { get; set; }
}

internal sealed class DeleteItemResponse
{
    [JsonPropertyName("Attributes")] public Dictionary<string, JsonElement>? Attributes { get; set; }
}

internal sealed class UpdateItemResponse
{
    [JsonPropertyName("Attributes")] public Dictionary<string, JsonElement>? Attributes { get; set; }
}

[JsonSerializable(typeof(PutItemRequest))]
[JsonSerializable(typeof(GetItemRequest))]
[JsonSerializable(typeof(DeleteItemRequest))]
[JsonSerializable(typeof(UpdateItemRequest))]
[JsonSerializable(typeof(PutItemResponse))]
[JsonSerializable(typeof(GetItemResponse))]
[JsonSerializable(typeof(DeleteItemResponse))]
[JsonSerializable(typeof(UpdateItemResponse))]
[JsonSerializable(typeof(ConditionFailurePayload))]
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    AllowTrailingCommas = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class ItemJsonContext : JsonSerializerContext
{
}
