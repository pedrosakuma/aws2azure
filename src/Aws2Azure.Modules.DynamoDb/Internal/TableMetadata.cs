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

    /// <summary>
    /// DynamoDB Time To Live configuration (UpdateTimeToLive). When enabled,
    /// the named attribute carries an absolute epoch-seconds expiry that item
    /// write paths translate into Cosmos' relative per-item <c>ttl</c> field.
    /// Null when TTL has never been configured for the table.
    /// </summary>
    [JsonPropertyName("timeToLive")]
    public TableTimeToLive? TimeToLive { get; set; }

    private IReadOnlyList<NumericIndexSortKey>? _numericGsiSortKeys;

    /// <summary>
    /// Global secondary index RANGE (sort) key attributes declared as Number
    /// (<c>N</c>), each paired with the pre-encoded Cosmos property name
    /// (<c>_a2a$ord$&lt;attr&gt;</c>) under which the write path stores the
    /// order-preserving numeric key so ordered GSI queries sort high-precision
    /// values correctly (#482). Computed once and cached on this (cached)
    /// metadata instance so the write hot path never rescans the index schemas
    /// per request. Empty when no GSI declares an N-typed sort key — the common
    /// case, where the write path adds nothing.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<NumericIndexSortKey> NumericGsiSortKeys
        => _numericGsiSortKeys ??= ComputeNumericGsiSortKeys();

    private IReadOnlyList<NumericIndexSortKey> ComputeNumericGsiSortKeys()
    {
        if (GlobalSecondaryIndexes is not { Count: > 0 } gsis)
        {
            return Array.Empty<NumericIndexSortKey>();
        }

        List<NumericIndexSortKey>? result = null;
        foreach (var gsi in gsis)
        {
            string? sortName = null;
            foreach (var k in gsi.KeySchema)
            {
                if (string.Equals(k.KeyType, "RANGE", StringComparison.Ordinal))
                {
                    sortName = k.Name;
                    break;
                }
            }

            if (sortName is null || !IsDeclaredNumber(sortName))
            {
                continue;
            }

            result ??= new List<NumericIndexSortKey>();
            // Multiple GSIs may share one sort attribute; store it once.
            bool already = false;
            foreach (var existing in result)
            {
                if (string.Equals(existing.AttributeName, sortName, StringComparison.Ordinal))
                {
                    already = true;
                    break;
                }
            }

            if (!already)
            {
                result.Add(new NumericIndexSortKey(sortName));
            }
        }

        return (IReadOnlyList<NumericIndexSortKey>?)result ?? Array.Empty<NumericIndexSortKey>();
    }

    private bool IsDeclaredNumber(string attributeName)
    {
        foreach (var def in AttributeDefinitions)
        {
            if (string.Equals(def.Name, attributeName, StringComparison.Ordinal))
            {
                return string.Equals(def.Type, "N", StringComparison.Ordinal);
            }
        }

        return false;
    }
}

/// <summary>
/// A Number-typed secondary-index sort attribute and the pre-encoded Cosmos
/// property name (<c>_a2a$ord$&lt;attr&gt;</c>) that carries its
/// order-preserving numeric key (#482). The UTF-8 property-name bytes are
/// computed once so the write path emits them without re-encoding per item.
/// </summary>
internal readonly struct NumericIndexSortKey
{
    public NumericIndexSortKey(string attributeName)
    {
        AttributeName = attributeName;
        OrderProperty = Persistence.InferredAttributeStorage.OrderKeyPropertyPrefix + attributeName;
        OrderPropertyUtf8 = System.Text.Encoding.UTF8.GetBytes(OrderProperty);
    }

    /// <summary>The DynamoDB item attribute name (the GSI RANGE key).</summary>
    public string AttributeName { get; }

    /// <summary>The Cosmos property name (<c>_a2a$ord$&lt;attr&gt;</c>).</summary>
    public string OrderProperty { get; }

    /// <summary>UTF-8 bytes of <see cref="OrderProperty"/>, for the write encoders.</summary>
    public byte[] OrderPropertyUtf8 { get; }
}

/// <summary>
/// Persisted DynamoDB TTL configuration for a table. DynamoDB expires items
/// whose <see cref="AttributeName"/> attribute (a Number of epoch seconds) is
/// in the past; the proxy maps this onto Cosmos' container <c>defaultTtl</c>
/// plus a per-item <c>ttl</c> computed at write time.
/// </summary>
internal sealed class TableTimeToLive
{
    /// <summary>Whether TTL expiry is currently enabled for the table.</summary>
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }

    /// <summary>
    /// The DynamoDB item attribute designated as the expiry timestamp. Retained
    /// even after TTL is disabled so <c>DescribeTimeToLive</c> can echo it, and
    /// because DynamoDB requires the name to flip a table back on.
    /// </summary>
    [JsonPropertyName("attributeName")] public string? AttributeName { get; set; }
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
