using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aws2Azure.Modules.DynamoDb.Internal;

namespace Aws2Azure.Modules.DynamoDb.Operations;

/// <summary>
/// JSON shapes for <c>TransactWriteItems</c>. Each per-operation envelope
/// (<c>Put</c> / <c>Delete</c> / <c>ConditionCheck</c> / <c>Update</c>) is
/// captured as a <see cref="JsonRange"/> byte range (not a materialized
/// <see cref="JsonElement"/> DOM): the deserializer skips the value, so the
/// request retains no per-action DOM (up to 100 actions/call), and the handler
/// opens a short-lived pooled <see cref="JsonDocument"/> over the present
/// envelope to validate/extract the inner DynamoDB fields. <c>Update</c> is
/// captured only to emit a clear <c>ValidationException</c> — atomic
/// <c>Update</c> within a transaction is a documented gap (see
/// <c>docs/gaps/dynamodb/TransactWriteItems.yaml</c>).
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
    public JsonRange Put { get; set; }

    [JsonPropertyName("Update")]
    public JsonRange Update { get; set; }

    [JsonPropertyName("Delete")]
    public JsonRange Delete { get; set; }

    [JsonPropertyName("ConditionCheck")]
    public JsonRange ConditionCheck { get; set; }
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
