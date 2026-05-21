using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aws2Azure.Modules.DynamoDb.Operations;

/// <summary>
/// Wire shape for <c>BatchGetItem</c>. <see cref="RequestItems"/> is a
/// table-name → per-table-request map; the values are intentionally
/// modelled as <see cref="JsonElement"/> so we can validate each
/// table's body once per table without paying for nested AOT-friendly
/// DTO generation across the dynamic map.
/// </summary>
internal sealed class BatchGetItemRequest
{
    [JsonPropertyName("RequestItems")]
    public Dictionary<string, JsonElement>? RequestItems { get; set; }

    [JsonPropertyName("ReturnConsumedCapacity")]
    public string? ReturnConsumedCapacity { get; set; }
}

internal sealed class BatchGetItemResponse
{
    [JsonPropertyName("Responses")]
    public Dictionary<string, List<Dictionary<string, JsonElement>>>? Responses { get; set; }

    [JsonPropertyName("UnprocessedKeys")]
    public Dictionary<string, BatchGetUnprocessedTable>? UnprocessedKeys { get; set; }
}

internal sealed class BatchGetUnprocessedTable
{
    [JsonPropertyName("Keys")]
    public List<Dictionary<string, JsonElement>>? Keys { get; set; }

    [JsonPropertyName("ProjectionExpression")]
    public string? ProjectionExpression { get; set; }

    [JsonPropertyName("ExpressionAttributeNames")]
    public Dictionary<string, string>? ExpressionAttributeNames { get; set; }

    [JsonPropertyName("ConsistentRead")]
    public bool? ConsistentRead { get; set; }
}

[JsonSerializable(typeof(BatchGetItemRequest))]
[JsonSerializable(typeof(BatchGetItemResponse))]
[JsonSerializable(typeof(BatchGetUnprocessedTable))]
[JsonSerializable(typeof(Dictionary<string, JsonElement>))]
[JsonSerializable(typeof(List<Dictionary<string, JsonElement>>))]
internal sealed partial class BatchGetItemJsonContext : JsonSerializerContext
{
}
