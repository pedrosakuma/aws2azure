using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aws2Azure.Modules.DynamoDb.Operations;

/// <summary>
/// Wire shape for <c>BatchWriteItem</c>. <see cref="RequestItems"/>
/// maps each table name to an ordered list of write actions; each
/// element carries exactly one of <c>PutRequest</c> or
/// <c>DeleteRequest</c>. The values are <see cref="JsonElement"/>
/// because their shape is dynamic and validated per-item.
/// </summary>
internal sealed class BatchWriteItemRequest
{
    [JsonPropertyName("RequestItems")]
    public Dictionary<string, List<JsonElement>>? RequestItems { get; set; }

    [JsonPropertyName("ReturnConsumedCapacity")]
    public string? ReturnConsumedCapacity { get; set; }

    [JsonPropertyName("ReturnItemCollectionMetrics")]
    public string? ReturnItemCollectionMetrics { get; set; }
}

internal sealed class BatchWriteItemResponse
{
    /// <summary>
    /// Per-table list of items that could not be processed (typically
    /// throttled by Cosmos). The wire shape mirrors the request: each
    /// entry is the original PutRequest / DeleteRequest envelope so
    /// AWS SDKs can retry the batch verbatim.
    /// </summary>
    [JsonPropertyName("UnprocessedItems")]
    public Dictionary<string, List<JsonElement>>? UnprocessedItems { get; set; }
}

[JsonSerializable(typeof(BatchWriteItemRequest))]
[JsonSerializable(typeof(BatchWriteItemResponse))]
[JsonSerializable(typeof(Dictionary<string, List<JsonElement>>))]
[JsonSerializable(typeof(List<JsonElement>))]
internal sealed partial class BatchWriteItemJsonContext : JsonSerializerContext
{
}
