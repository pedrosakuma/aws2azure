using System.Collections.Generic;
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
        var hasSecretValue = SecretsManagerOperationSupport.HasSecretValue(secretString, secretBinary);

        if (!hasSecretValue)
        {
            if (string.IsNullOrEmpty(description))
            {
                throw new ArgumentException("UpdateSecret requires Description, SecretString, or SecretBinary.");
            }

            var exists = await SecretsManagerOperationSupport.SecretExistsAsync(
                context,
                client,
                token,
                name,
                cancellationToken).ConfigureAwait(false);
            if (exists is null)
            {
                return;
            }

            if (!exists.Value)
            {
                await SecretsManagerOperationSupport.WriteAwsErrorAsync(context, StatusCodes.Status404NotFound, "ResourceNotFoundException", $"Secrets Manager can't find the specified secret '{name}'.").ConfigureAwait(false);
                return;
            }

            await SecretsManagerOperationSupport.WriteAwsErrorAsync(
                context,
                StatusCodes.Status501NotImplemented,
                "NotImplementedException",
                "Metadata-only UpdateSecret requests are not supported by aws2azure. Azure Key Vault's update contract does not expose AWS Secrets Manager's description-only metadata path, so publish a new secret value instead or manage secret metadata directly in Azure.").ConfigureAwait(false);
            return;
        }

        await using var secretLock = await SecretVersionCoordinator.AcquireLockAsync(name, cancellationToken).ConfigureAwait(false);
        IReadOnlyDictionary<string, string> currentUserTags;
        IReadOnlyList<SecretVersionCoordinator.SecretVersionMetadata>? existingVersions = null;
        if (!string.IsNullOrWhiteSpace(clientRequestToken))
        {
            var inventory = await SecretVersionCoordinator.ReadInventoryAsync(
                context,
                client,
                token,
                name,
                cancellationToken).ConfigureAwait(false);
            if (inventory is null)
            {
                return;
            }

            if (!inventory.Value.Exists)
            {
                await SecretsManagerOperationSupport.WriteAwsErrorAsync(context, StatusCodes.Status404NotFound, "ResourceNotFoundException", $"Secrets Manager can't find the specified secret '{name}'.").ConfigureAwait(false);
                return;
            }

            existingVersions = inventory.Value.Versions;
            currentUserTags = SecretVersionCoordinator.GetCurrentUserTags(existingVersions);
        }
        else
        {
            var currentSecret = await SecretsManagerOperationSupport.ReadCurrentSecretLookupAsync(
                context,
                client,
                token,
                name,
                cancellationToken).ConfigureAwait(false);
            if (currentSecret is null)
            {
                return;
            }

            if (!currentSecret.Value.Exists)
            {
                await SecretsManagerOperationSupport.WriteAwsErrorAsync(context, StatusCodes.Status404NotFound, "ResourceNotFoundException", $"Secrets Manager can't find the specified secret '{name}'.").ConfigureAwait(false);
                return;
            }

            currentUserTags = currentSecret.Value.UserTags;
        }

        var written = await PutSecretValueHandler.CreateVersionAsync(
            context, client, token, name, secretString, secretBinary, description,
            clientRequestToken, payloadSha256, ["AWSCURRENT"], versionStagesSpecified: false, currentUserTags, existingVersions, cancellationToken).ConfigureAwait(false);
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
