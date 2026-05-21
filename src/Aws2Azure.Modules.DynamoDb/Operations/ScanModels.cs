using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aws2Azure.Modules.DynamoDb.Operations;

/// <summary>
/// Wire DTO for the <c>Scan</c> operation. Mirrors the public
/// DynamoDB shape; legacy v1 fields (<c>ScanFilter</c>,
/// <c>ConditionalOperator</c>, <c>AttributesToGet</c>) are accepted
/// only so the handler can reject them loudly.
/// </summary>
internal sealed class ScanRequest
{
    [JsonPropertyName("TableName")] public string? TableName { get; set; }
    [JsonPropertyName("FilterExpression")] public string? FilterExpression { get; set; }
    [JsonPropertyName("ProjectionExpression")] public string? ProjectionExpression { get; set; }
    [JsonPropertyName("ExpressionAttributeNames")] public JsonElement? ExpressionAttributeNames { get; set; }
    [JsonPropertyName("ExpressionAttributeValues")] public JsonElement? ExpressionAttributeValues { get; set; }
    [JsonPropertyName("Select")] public string? Select { get; set; }
    [JsonPropertyName("Limit")] public int? Limit { get; set; }
    [JsonPropertyName("ExclusiveStartKey")] public JsonElement? ExclusiveStartKey { get; set; }
    [JsonPropertyName("ConsistentRead")] public bool? ConsistentRead { get; set; }
    [JsonPropertyName("IndexName")] public string? IndexName { get; set; }

    // Parallel-scan parameters: rejected in this slice.
    [JsonPropertyName("Segment")] public int? Segment { get; set; }
    [JsonPropertyName("TotalSegments")] public int? TotalSegments { get; set; }

    // Legacy parameters: rejected.
    [JsonPropertyName("ScanFilter")] public JsonElement? ScanFilter { get; set; }
    [JsonPropertyName("ConditionalOperator")] public string? ConditionalOperator { get; set; }
    [JsonPropertyName("AttributesToGet")] public JsonElement? AttributesToGet { get; set; }
}

internal sealed class ScanResponse
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

[JsonSerializable(typeof(ScanRequest))]
[JsonSerializable(typeof(ScanResponse))]
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    AllowTrailingCommas = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class ScanJsonContext : JsonSerializerContext
{
}
