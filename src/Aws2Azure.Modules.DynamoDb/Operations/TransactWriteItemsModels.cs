using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aws2Azure.Modules.DynamoDb.Operations;

/// <summary>
/// JSON shapes for <c>TransactWriteItems</c>. Each per-operation envelope
/// (<c>Put</c> / <c>Delete</c> / <c>ConditionCheck</c> / <c>Update</c>) is kept
/// as a raw <see cref="JsonElement"/> so the handler can validate/extract the
/// inner DynamoDB fields without a deep typed model and so unknown sub-fields
/// survive round-trip. <c>Update</c> is parsed only to emit a clear
/// <c>ValidationException</c> — atomic <c>Update</c> within a transaction is a
/// documented gap (see <c>docs/gaps/dynamodb/TransactWriteItems.yaml</c>).
/// </summary>
internal sealed class TransactWriteItemsRequest
{
    [JsonPropertyName("TransactItems")]
    public List<TransactWriteItem>? TransactItems { get; set; }

    [JsonPropertyName("ClientRequestToken")]
    public string? ClientRequestToken { get; set; }

    [JsonPropertyName("ReturnConsumedCapacity")]
    public string? ReturnConsumedCapacity { get; set; }

    [JsonPropertyName("ReturnItemCollectionMetrics")]
    public string? ReturnItemCollectionMetrics { get; set; }
}

internal sealed class TransactWriteItem
{
    [JsonPropertyName("Put")]
    public JsonElement Put { get; set; }

    [JsonPropertyName("Update")]
    public JsonElement Update { get; set; }

    [JsonPropertyName("Delete")]
    public JsonElement Delete { get; set; }

    [JsonPropertyName("ConditionCheck")]
    public JsonElement ConditionCheck { get; set; }
}

/// <summary>
/// <c>TransactWriteItems</c> success envelope. AWS returns an empty body
/// (optionally <c>ConsumedCapacity</c> / <c>ItemCollectionMetrics</c>, which
/// aws2azure does not surface).
/// </summary>
internal sealed class TransactWriteItemsResponse
{
}

[JsonSerializable(typeof(TransactWriteItemsRequest))]
[JsonSerializable(typeof(TransactWriteItemsResponse))]
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    AllowTrailingCommas = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class TransactWriteItemsJsonContext : JsonSerializerContext
{
}
