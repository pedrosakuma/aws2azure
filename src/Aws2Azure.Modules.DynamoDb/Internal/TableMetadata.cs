using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Aws2Azure.Modules.DynamoDb.Internal;

/// <summary>
/// DynamoDB table-level schema preserved in a sidecar document inside
/// the Cosmos container that backs the table. Cosmos itself only
/// records a single partition-key path (<c>/pk</c> in this proxy); the
/// original DynamoDB attribute names + types, the secondary index
/// schemas, and the table's billing/streaming flags are kept here so
/// <c>DescribeTable</c> can round-trip them.
///
/// <para>The document lives at <c>id = "__aws2azure_table_meta__"</c>
/// and <c>pk = "__aws2azure_table_meta__"</c> so it never collides
/// with user items. Item handlers (later slices) skip it on Query/Scan
/// using its <c>_meta</c> discriminator field.</para>
/// </summary>
internal sealed class TableMetadata
{
    public const string DocId = "__aws2azure_table_meta__";

    [JsonPropertyName("id")]
    public string Id { get; set; } = DocId;

    [JsonPropertyName("pk")]
    public string PartitionKey { get; set; } = DocId;

    /// <summary>Discriminator so item-scan code can skip sidecar docs.</summary>
    [JsonPropertyName("_meta")]
    public string Meta { get; set; } = "table";

    [JsonPropertyName("tableName")]
    public string TableName { get; set; } = string.Empty;

    /// <summary>UTC creation timestamp (Unix seconds).</summary>
    [JsonPropertyName("creationDateTime")]
    public long CreationDateTime { get; set; }

    [JsonPropertyName("attributeDefinitions")]
    public List<TableAttributeDefinition> AttributeDefinitions { get; set; } = new();

    [JsonPropertyName("keySchema")]
    public List<TableKeySchemaElement> KeySchema { get; set; } = new();

    [JsonPropertyName("billingMode")]
    public string? BillingMode { get; set; }
}

internal sealed class TableAttributeDefinition
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;

    /// <summary>DynamoDB scalar type: <c>S</c>, <c>N</c>, or <c>B</c>.</summary>
    [JsonPropertyName("type")] public string Type { get; set; } = string.Empty;
}

internal sealed class TableKeySchemaElement
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;

    /// <summary>Key role: <c>HASH</c> or <c>RANGE</c>.</summary>
    [JsonPropertyName("keyType")] public string KeyType { get; set; } = string.Empty;
}

[JsonSerializable(typeof(TableMetadata))]
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    AllowTrailingCommas = true)]
internal sealed partial class TableMetadataJsonContext : JsonSerializerContext
{
}
