using Microsoft.AspNetCore.Http;
using System.Text.Json;

namespace Aws2Azure.Modules.SecretsManager.Operations;

internal static class UpdateSecretHandler
{
    public static async Task HandleAsync(HttpContext context, KeyVaultSecretClient client, JsonDocument document, CancellationToken cancellationToken)
    {
        var name = KeyVaultSecretClient.NormalizeSecretName(SecretsManagerOperationSupport.ReadString(document, "SecretId") ?? SecretsManagerOperationSupport.ReadString(document, "Name") ?? string.Empty);
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

        var secretString = SecretsManagerOperationSupport.ReadString(document, "SecretString");
        var secretBinary = SecretsManagerOperationSupport.ReadString(document, "SecretBinary");
        var description = SecretsManagerOperationSupport.ReadString(document, "Description");
        var clientRequestToken = SecretsManagerOperationSupport.ReadString(document, "ClientRequestToken");
        var contentType = string.IsNullOrWhiteSpace(secretBinary) ? null : "application/octet-stream";
        var storedValue = string.IsNullOrWhiteSpace(secretBinary)
            ? secretString
            : KeyVaultSecretClient.EncodeSecretBinary(KeyVaultSecretClient.DecodeSecretBinary(secretBinary));
        var payloadSha256 = KeyVaultSecretClient.GetPayloadSha256(storedValue, contentType);

        await using var secretLock = await SecretVersionCoordinator.AcquireLockAsync(name, cancellationToken).ConfigureAwait(false);
        var written = await PutSecretValueHandler.CreateVersionAsync(
            context, client, token, name, secretString, secretBinary, description,
            clientRequestToken, payloadSha256, ["AWSCURRENT"], versionStagesSpecified: false, cancellationToken).ConfigureAwait(false);
        if (written is null)
        {
            return;
        }

        var payload = new UpdateSecretResponse(
            Arn: KeyVaultSecretClient.BuildArn(name),
            Name: name,
            VersionId: string.IsNullOrWhiteSpace(clientRequestToken) ? written.Value.VersionId : clientRequestToken,
            VersionStages: written.Value.VersionStages,
            CreatedDate: written.Value.CreatedDate);

        await SecretsManagerOperationSupport.WriteJsonAsync(context, payload, SecretsManagerJsonContext.Default.UpdateSecretResponse, cancellationToken).ConfigureAwait(false);
    }
}
