using System.Text.Json.Serialization;

namespace Aws2Azure.Modules.SecretsManager;

internal sealed record GetSecretValueResponse(
    [property: JsonPropertyName("ARN")] string Arn,
    [property: JsonPropertyName("Name")] string Name,
    [property: JsonPropertyName("VersionId")] string VersionId,
    [property: JsonPropertyName("SecretString")] string? SecretString,
    [property: JsonPropertyName("SecretBinary")] string? SecretBinary,
    [property: JsonPropertyName("VersionStages")] IReadOnlyList<string>? VersionStages,
    [property: JsonPropertyName("CreatedDate")][property: JsonConverter(typeof(EpochDateTimeOffsetConverter))] DateTimeOffset? CreatedDate);

internal sealed record CreateSecretResponse(
    [property: JsonPropertyName("ARN")] string Arn,
    [property: JsonPropertyName("Name")] string Name,
    [property: JsonPropertyName("VersionId")] string VersionId,
    [property: JsonPropertyName("VersionStages")] IReadOnlyList<string> VersionStages,
    [property: JsonPropertyName("CreatedDate")][property: JsonConverter(typeof(EpochDateTimeOffsetConverter))] DateTimeOffset CreatedDate);

internal sealed record DeleteSecretResponse(
    [property: JsonPropertyName("ARN")] string Arn,
    [property: JsonPropertyName("Name")] string Name,
    [property: JsonPropertyName("DeletionDate")][property: JsonConverter(typeof(EpochDateTimeOffsetConverter))] DateTimeOffset DeletionDate,
    [property: JsonPropertyName("DeletedDate")][property: JsonConverter(typeof(EpochDateTimeOffsetConverter))] DateTimeOffset? DeletedDate,
    [property: JsonPropertyName("VersionId")] string? VersionId);

internal sealed record DescribeSecretResponse(
    [property: JsonPropertyName("ARN")] string Arn,
    [property: JsonPropertyName("Name")] string Name,
    [property: JsonPropertyName("Description")] string? Description,
    [property: JsonPropertyName("CreatedDate")][property: JsonConverter(typeof(EpochDateTimeOffsetConverter))] DateTimeOffset CreatedDate,
    [property: JsonPropertyName("LastChangedDate")][property: JsonConverter(typeof(EpochDateTimeOffsetConverter))] DateTimeOffset? LastChangedDate,
    [property: JsonPropertyName("Tags")] IReadOnlyList<SecretsManagerTag>? Tags,
    [property: JsonPropertyName("VersionIdsToStages")] IReadOnlyDictionary<string, IReadOnlyList<string>>? VersionIdsToStages,
    [property: JsonPropertyName("RotationEnabled")] bool? RotationEnabled,
    [property: JsonPropertyName("DeletedDate")][property: JsonConverter(typeof(EpochDateTimeOffsetConverter))] DateTimeOffset? DeletedDate);

internal sealed record ListSecretsResponse(
    [property: JsonPropertyName("SecretList")] IReadOnlyList<ListSecretsItem> SecretList,
    [property: JsonPropertyName("NextToken")] string? NextToken);

internal sealed record ListSecretsItem(
    [property: JsonPropertyName("ARN")] string Arn,
    [property: JsonPropertyName("Name")] string Name,
    [property: JsonPropertyName("Description")] string? Description,
    [property: JsonPropertyName("CreatedDate")][property: JsonConverter(typeof(EpochDateTimeOffsetConverter))] DateTimeOffset CreatedDate,
    [property: JsonPropertyName("LastChangedDate")][property: JsonConverter(typeof(EpochDateTimeOffsetConverter))] DateTimeOffset? LastChangedDate,
    [property: JsonPropertyName("Tags")] IReadOnlyList<SecretsManagerTag>? Tags,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [property: JsonPropertyName("VersionIdsToStages")] IReadOnlyDictionary<string, IReadOnlyList<string>>? VersionIdsToStages);

/// <summary>
/// AWS Secrets Manager tag shape. Secrets Manager serializes tags as a JSON
/// array of <c>{ "Key": ..., "Value": ... }</c> objects, not as a JSON map.
/// Emitting a map makes the AWS SDK's tag list-unmarshaller desynchronize and
/// spin the enclosing SecretList loop, so this array shape is required.
/// </summary>
internal sealed record SecretsManagerTag(
    [property: JsonPropertyName("Key")] string Key,
    [property: JsonPropertyName("Value")] string Value);

internal sealed record UpdateSecretResponse(
    [property: JsonPropertyName("ARN")] string Arn,
    [property: JsonPropertyName("Name")] string Name,
    [property: JsonPropertyName("VersionId")] string VersionId,
    [property: JsonPropertyName("VersionStages")] IReadOnlyList<string> VersionStages,
    [property: JsonPropertyName("CreatedDate")][property: JsonConverter(typeof(EpochDateTimeOffsetConverter))] DateTimeOffset CreatedDate);

internal sealed record PutSecretValueResponse(
    [property: JsonPropertyName("ARN")] string Arn,
    [property: JsonPropertyName("Name")] string Name,
    [property: JsonPropertyName("VersionId")] string VersionId,
    [property: JsonPropertyName("VersionStages")] IReadOnlyList<string> VersionStages);

internal sealed record KeyVaultSecretAttributes(
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("exp")] long? Exp,
    [property: JsonPropertyName("nbf")] long? Nbf,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [property: JsonPropertyName("created")] long? Created);

internal sealed record KeyVaultSecretRequest(
    [property: JsonPropertyName("value")] string? Value,
    [property: JsonPropertyName("contentType")] string? ContentType,
    [property: JsonPropertyName("tags")] IReadOnlyDictionary<string, string>? Tags,
    [property: JsonPropertyName("attributes")] KeyVaultSecretAttributes? Attributes,
    [property: JsonPropertyName("description")] string? Description);

internal sealed record KeyVaultSecretTagsRequest(
    [property: JsonPropertyName("tags")] IReadOnlyDictionary<string, string>? Tags);

[JsonSerializable(typeof(GetSecretValueResponse))]
[JsonSerializable(typeof(CreateSecretResponse))]
[JsonSerializable(typeof(DeleteSecretResponse))]
[JsonSerializable(typeof(DescribeSecretResponse))]
[JsonSerializable(typeof(ListSecretsResponse))]
[JsonSerializable(typeof(ListSecretsItem))]
[JsonSerializable(typeof(SecretsManagerTag))]
[JsonSerializable(typeof(UpdateSecretResponse))]
[JsonSerializable(typeof(PutSecretValueResponse))]
[JsonSerializable(typeof(KeyVaultSecretRequest))]
[JsonSerializable(typeof(KeyVaultSecretTagsRequest))]
[JsonSerializable(typeof(KeyVaultSecretAttributes))]
internal sealed partial class SecretsManagerJsonContext : JsonSerializerContext
{
}
