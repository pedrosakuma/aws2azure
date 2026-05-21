using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aws2Azure.Modules.DynamoDb.Operations;

/// <summary>
/// JSON shapes for <c>TransactGetItems</c>. Per-table envelope is kept
/// as <see cref="JsonElement"/> so unknown DDB attributes (e.g.
/// <c>ExpressionAttributeNames</c>) survive round-trip.
/// </summary>
internal sealed class TransactGetItemsRequest
{
    [JsonPropertyName("TransactItems")]
    public List<TransactGetItem>? TransactItems { get; set; }

    [JsonPropertyName("ReturnConsumedCapacity")]
    public string? ReturnConsumedCapacity { get; set; }
}

internal sealed class TransactGetItem
{
    [JsonPropertyName("Get")]
    public JsonElement Get { get; set; }
}

internal sealed class TransactGetItemsResponse
{
    [JsonPropertyName("Responses")]
    public List<TransactGetItemResponse>? Responses { get; set; }
}

internal sealed class TransactGetItemResponse
{
    [JsonPropertyName("Item")]
    public Dictionary<string, JsonElement>? Item { get; set; }
}

[JsonSerializable(typeof(TransactGetItemsRequest))]
[JsonSerializable(typeof(TransactGetItemsResponse))]
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    AllowTrailingCommas = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class TransactGetItemsJsonContext : JsonSerializerContext
{
}
