using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Aws2Azure.Modules.DynamoDb.Operations;

/// <summary>
/// DynamoDB CreateTable request body. Secondary index schemas are modeled
/// as typed DTOs so the handler can validate and persist them; throughput
/// and SSE fields are still accepted-and-ignored per the gap doc.
/// </summary>
internal sealed class CreateTableRequest
{
    [JsonPropertyName("TableName")] public string? TableName { get; set; }
    [JsonPropertyName("AttributeDefinitions")] public List<AttributeDefinitionDto>? AttributeDefinitions { get; set; }
    [JsonPropertyName("KeySchema")] public List<KeySchemaElementDto>? KeySchema { get; set; }
    [JsonPropertyName("BillingMode")] public string? BillingMode { get; set; }
    [JsonPropertyName("GlobalSecondaryIndexes")] public List<SecondaryIndexDto>? GlobalSecondaryIndexes { get; set; }
    [JsonPropertyName("LocalSecondaryIndexes")] public List<SecondaryIndexDto>? LocalSecondaryIndexes { get; set; }
}

/// <summary>
/// CreateTable representation of a GSI or LSI. The two share an identical
/// request shape (IndexName + KeySchema + Projection); GSI-only fields like
/// ProvisionedThroughput are accepted-and-ignored.
/// </summary>
internal sealed class SecondaryIndexDto
{
    [JsonPropertyName("IndexName")] public string? IndexName { get; set; }
    [JsonPropertyName("KeySchema")] public List<KeySchemaElementDto>? KeySchema { get; set; }
    [JsonPropertyName("Projection")] public ProjectionDto? Projection { get; set; }
}

internal sealed class ProjectionDto
{
    [JsonPropertyName("ProjectionType")] public string? ProjectionType { get; set; }
    [JsonPropertyName("NonKeyAttributes")] public List<string>? NonKeyAttributes { get; set; }
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
    [JsonPropertyName("GlobalSecondaryIndexes")] public List<SecondaryIndexDescriptionDto>? GlobalSecondaryIndexes { get; set; }
    [JsonPropertyName("LocalSecondaryIndexes")] public List<SecondaryIndexDescriptionDto>? LocalSecondaryIndexes { get; set; }
}

/// <summary>
/// DescribeTable representation of a GSI or LSI. <see cref="IndexStatus"/>
/// is GSI-only (LSIs have no lifecycle status); it is omitted for LSIs via
/// the context's WhenWritingNull policy.
/// </summary>
internal sealed class SecondaryIndexDescriptionDto
{
    [JsonPropertyName("IndexName")] public string? IndexName { get; set; }
    [JsonPropertyName("KeySchema")] public List<KeySchemaElementDto>? KeySchema { get; set; }
    [JsonPropertyName("Projection")] public ProjectionDto? Projection { get; set; }
    [JsonPropertyName("IndexStatus")] public string? IndexStatus { get; set; }
    [JsonPropertyName("IndexArn")] public string? IndexArn { get; set; }
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
