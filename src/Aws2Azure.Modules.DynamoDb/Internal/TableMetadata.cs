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

    [JsonPropertyName("_a2a_pk")]
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

    [JsonPropertyName("tags")]
    public List<TableTag>? Tags { get; set; }

    /// <summary>
    /// Global secondary index schemas. Persisted so index Query/Scan
    /// (later slices) and <c>DescribeTable</c> can resolve the index
    /// HASH/RANGE attributes. Null when the table declares no GSIs.
    /// </summary>
    [JsonPropertyName("globalSecondaryIndexes")]
    public List<TableIndexDefinition>? GlobalSecondaryIndexes { get; set; }

    /// <summary>
    /// Local secondary index schemas. An LSI shares the table HASH key
    /// and adds an alternate RANGE key. Null when the table declares no
    /// LSIs.
    /// </summary>
    [JsonPropertyName("localSecondaryIndexes")]
    public List<TableIndexDefinition>? LocalSecondaryIndexes { get; set; }
}

/// <summary>
/// A persisted secondary index schema (GSI or LSI). The <see cref="KeySchema"/>
/// holds the index HASH (and optional RANGE) attribute names; the projection
/// describes which attributes a query against the index returns.
/// </summary>
internal sealed class TableIndexDefinition
{
    [JsonPropertyName("indexName")] public string IndexName { get; set; } = string.Empty;

    [JsonPropertyName("keySchema")]
    public List<TableKeySchemaElement> KeySchema { get; set; } = new();

    /// <summary>Projection type: <c>ALL</c>, <c>KEYS_ONLY</c>, or <c>INCLUDE</c>.</summary>
    [JsonPropertyName("projectionType")] public string ProjectionType { get; set; } = "ALL";

    /// <summary>Extra attributes returned when <see cref="ProjectionType"/> is <c>INCLUDE</c>.</summary>
    [JsonPropertyName("nonKeyAttributes")] public List<string>? NonKeyAttributes { get; set; }
}

internal sealed class TableTag
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("value")]
    public string Value { get; set; } = string.Empty;
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
