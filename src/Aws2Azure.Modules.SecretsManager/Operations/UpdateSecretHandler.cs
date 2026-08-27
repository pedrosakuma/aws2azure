using Microsoft.AspNetCore.Http;
using System.Text.Json;

namespace Aws2Azure.Modules.SecretsManager.Operations;

internal static class UpdateSecretHandler
{
    public static async Task HandleAsync(HttpContext context, KeyVaultSecretClient client, JsonDocument document, CancellationToken cancellationToken)
    {
        var name = KeyVaultSecretClient.NormalizeSecretName(SecretsManagerOperationSupport.ReadString(document, "SecretId") ?? SecretsManagerOperationSupport.ReadString(document, "Name") ?? string.Empty);
        var secretString = SecretsManagerOperationSupport.ReadString(document, "SecretString");
        var secretBinary = SecretsManagerOperationSupport.ReadString(document, "SecretBinary");
        var description = SecretsManagerOperationSupport.ReadString(document, "Description");
        SecretsManagerOperationSupport.ValidateAtMostOneSecretValue(secretString, secretBinary);
        var clientRequestToken = SecretsManagerOperationSupport.ReadString(document, "ClientRequestToken");
        var contentType = string.IsNullOrEmpty(secretBinary) ? null : "application/octet-stream";
        var storedValue = string.IsNullOrEmpty(secretBinary)
            ? secretString
            : KeyVaultSecretClient.EncodeSecretBinary(KeyVaultSecretClient.DecodeSecretBinary(secretBinary));
        var payloadSha256 = KeyVaultSecretClient.GetPayloadSha256(storedValue, contentType);
        var token = await client.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);

        var exists = await SecretsManagerOperationSupport.SecretExistsAsync(context, client, token, name, cancellationToken).ConfigureAwait(false);
        if (exists is null)
        {
            return;
        }

        if (!exists.Value)
        {
            await SecretsManagerOperationSupport.WriteAwsErrorAsync(context, StatusCodes.Status404NotFound, "ResourceNotFoundException", $"Secrets Manager can't find the specified secret '{name}'.").ConfigureAwait(false);
            return;
        }

        if (!SecretsManagerOperationSupport.HasSecretValue(secretString, secretBinary))
        {
            if (string.IsNullOrEmpty(description))
            {
                throw new ArgumentException("UpdateSecret requires Description, SecretString, or SecretBinary.");
            }

            await SecretsManagerOperationSupport.WriteAwsErrorAsync(
                context,
                StatusCodes.Status501NotImplemented,
                "NotImplementedException",
                "Metadata-only UpdateSecret requests are not supported by aws2azure. Azure Key Vault's update contract does not expose AWS Secrets Manager's description-only metadata path, so publish a new secret value instead or manage secret metadata directly in Azure.").ConfigureAwait(false);
            return;
        }

        await using var secretLock = await SecretVersionCoordinator.AcquireLockAsync(name, cancellationToken).ConfigureAwait(false);
        var currentUserTags = await PutSecretValueHandler.ReadCurrentUserTagsAsync(context, client, token, name, cancellationToken).ConfigureAwait(false);
        if (currentUserTags is null)
        {
            return;
        }

        var written = await PutSecretValueHandler.CreateVersionAsync(
            context, client, token, name, secretString, secretBinary, description,
            clientRequestToken, payloadSha256, ["AWSCURRENT"], versionStagesSpecified: false, currentUserTags, cancellationToken).ConfigureAwait(false);
        if (written is null)
        {
            return;
        }

        var payload = new UpdateSecretResponse(
            Arn: KeyVaultSecretClient.BuildArn(name),
            Name: name,
            VersionId: string.IsNullOrWhiteSpace(clientRequestToken) ? written.Value.VersionId : clientRequestToken);

        await SecretsManagerOperationSupport.WriteJsonAsync(context, payload, SecretsManagerJsonContext.Default.UpdateSecretResponse, cancellationToken).ConfigureAwait(false);
    }
}
