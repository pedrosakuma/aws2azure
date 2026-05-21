using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aws2Azure.Modules.DynamoDb.Operations;

/// <summary>
/// Wire DTO for the <c>Query</c> operation. Only the subset the
/// proxy currently supports is materialised; unknown / ignored
/// fields are silently dropped by <c>System.Text.Json</c>.
/// </summary>
internal sealed class QueryRequest
{
    [JsonPropertyName("TableName")] public string? TableName { get; set; }
    [JsonPropertyName("KeyConditionExpression")] public string? KeyConditionExpression { get; set; }
    [JsonPropertyName("FilterExpression")] public string? FilterExpression { get; set; }
    [JsonPropertyName("ProjectionExpression")] public string? ProjectionExpression { get; set; }
    [JsonPropertyName("ExpressionAttributeNames")] public JsonElement? ExpressionAttributeNames { get; set; }
    [JsonPropertyName("ExpressionAttributeValues")] public JsonElement? ExpressionAttributeValues { get; set; }
    [JsonPropertyName("Select")] public string? Select { get; set; }
    [JsonPropertyName("Limit")] public int? Limit { get; set; }
    [JsonPropertyName("ExclusiveStartKey")] public JsonElement? ExclusiveStartKey { get; set; }
    [JsonPropertyName("ScanIndexForward")] public bool? ScanIndexForward { get; set; }
    [JsonPropertyName("ConsistentRead")] public bool? ConsistentRead { get; set; }
    [JsonPropertyName("IndexName")] public string? IndexName { get; set; }

    // Legacy parameters explicitly rejected (loud failure).
    [JsonPropertyName("KeyConditions")] public JsonElement? KeyConditions { get; set; }
    [JsonPropertyName("QueryFilter")] public JsonElement? QueryFilter { get; set; }
    [JsonPropertyName("ConditionalOperator")] public string? ConditionalOperator { get; set; }
}

internal sealed class QueryResponse
{
    [JsonPropertyName("Items")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<Dictionary<string, JsonElement>>? Items { get; set; }

    [JsonPropertyName("Count")] public int Count { get; set; }
    [JsonPropertyName("ScannedCount")] public int ScannedCount { get; set; }

    [JsonPropertyName("LastEvaluatedKey")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, JsonElement>? LastEvaluatedKey { get; set; }
}

[JsonSerializable(typeof(QueryRequest))]
[JsonSerializable(typeof(QueryResponse))]
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    AllowTrailingCommas = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class QueryJsonContext : JsonSerializerContext
{
}
