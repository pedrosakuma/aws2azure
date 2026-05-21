using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aws2Azure.Modules.DynamoDb.Operations;

/// <summary>
/// DynamoDB CreateTable request body. Index/throughput/SSE fields are
/// modeled as raw <see cref="JsonElement"/> so the handler can detect
/// when a caller asks for an unsupported feature and reject the call
/// instead of silently dropping it — see the matching gap doc for the
/// divergence list.
/// </summary>
internal sealed class CreateTableRequest
{
    [JsonPropertyName("TableName")] public string? TableName { get; set; }
    [JsonPropertyName("AttributeDefinitions")] public List<AttributeDefinitionDto>? AttributeDefinitions { get; set; }
    [JsonPropertyName("KeySchema")] public List<KeySchemaElementDto>? KeySchema { get; set; }
    [JsonPropertyName("BillingMode")] public string? BillingMode { get; set; }
    [JsonPropertyName("GlobalSecondaryIndexes")] public JsonElement? GlobalSecondaryIndexes { get; set; }
    [JsonPropertyName("LocalSecondaryIndexes")] public JsonElement? LocalSecondaryIndexes { get; set; }
}

internal sealed class AttributeDefinitionDto
{
    [JsonPropertyName("AttributeName")] public string? AttributeName { get; set; }
    [JsonPropertyName("AttributeType")] public string? AttributeType { get; set; }
}

internal sealed class KeySchemaElementDto
{
    [JsonPropertyName("AttributeName")] public string? AttributeName { get; set; }
    [JsonPropertyName("KeyType")] public string? KeyType { get; set; }
}

internal sealed class DeleteTableRequest
{
    [JsonPropertyName("TableName")] public string? TableName { get; set; }
}

internal sealed class DescribeTableRequest
{
    [JsonPropertyName("TableName")] public string? TableName { get; set; }
}

internal sealed class ListTablesRequest
{
    [JsonPropertyName("ExclusiveStartTableName")] public string? ExclusiveStartTableName { get; set; }
    [JsonPropertyName("Limit")] public int? Limit { get; set; }
}

internal sealed class CreateTableResponse
{
    [JsonPropertyName("TableDescription")] public TableDescription? TableDescription { get; set; }
}

internal sealed class DeleteTableResponse
{
    [JsonPropertyName("TableDescription")] public TableDescription? TableDescription { get; set; }
}

internal sealed class DescribeTableResponse
{
    [JsonPropertyName("Table")] public TableDescription? Table { get; set; }
}

internal sealed class ListTablesResponse
{
    [JsonPropertyName("TableNames")] public List<string> TableNames { get; set; } = new();

    [JsonPropertyName("LastEvaluatedTableName")]
    public string? LastEvaluatedTableName { get; set; }
}

internal sealed class TableDescription
{
    [JsonPropertyName("TableName")] public string? TableName { get; set; }
    [JsonPropertyName("TableStatus")] public string? TableStatus { get; set; }
    [JsonPropertyName("CreationDateTime")] public double? CreationDateTime { get; set; }
    [JsonPropertyName("AttributeDefinitions")] public List<AttributeDefinitionDto>? AttributeDefinitions { get; set; }
    [JsonPropertyName("KeySchema")] public List<KeySchemaElementDto>? KeySchema { get; set; }
    [JsonPropertyName("ItemCount")] public long ItemCount { get; set; }
    [JsonPropertyName("TableSizeBytes")] public long TableSizeBytes { get; set; }
    [JsonPropertyName("TableArn")] public string? TableArn { get; set; }
    [JsonPropertyName("BillingModeSummary")] public BillingModeSummary? BillingModeSummary { get; set; }
}

internal sealed class BillingModeSummary
{
    [JsonPropertyName("BillingMode")] public string? BillingMode { get; set; }
}

[JsonSerializable(typeof(CreateTableRequest))]
[JsonSerializable(typeof(DeleteTableRequest))]
[JsonSerializable(typeof(DescribeTableRequest))]
[JsonSerializable(typeof(ListTablesRequest))]
[JsonSerializable(typeof(CreateTableResponse))]
[JsonSerializable(typeof(DeleteTableResponse))]
[JsonSerializable(typeof(DescribeTableResponse))]
[JsonSerializable(typeof(ListTablesResponse))]
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    AllowTrailingCommas = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class TableLifecycleJsonContext : JsonSerializerContext
{
}
