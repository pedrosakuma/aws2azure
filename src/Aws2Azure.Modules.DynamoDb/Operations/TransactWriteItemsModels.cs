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
/// parsed and executed atomically alongside <c>Put</c>/<c>Delete</c>/
/// <c>ConditionCheck</c> via the <c>atomicTransactWrite_v6</c> stored
/// procedure, restricted to the SET/REMOVE-only, top-level-attribute,
/// native-JSON-value subset validated by
/// <see cref="Internal.SprocEligibility.TryValidateTransactionUpdate"/> (see
/// <c>docs/gaps/dynamodb/TransactWriteItems.yaml</c> for the exact scope and
/// verification status).
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

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
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
