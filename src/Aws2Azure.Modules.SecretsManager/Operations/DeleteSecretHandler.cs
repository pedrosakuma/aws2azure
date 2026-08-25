using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.SecretsManager.Operations;

internal static class DeleteSecretHandler
{
    public static async Task HandleAsync(HttpContext context, KeyVaultSecretClient client, JsonDocument document, CancellationToken cancellationToken)
    {
        var name = KeyVaultSecretClient.NormalizeSecretName(SecretsManagerOperationSupport.ReadString(document, "SecretId") ?? string.Empty);
        var recoveryWindowInDays = SecretsManagerOperationSupport.ReadInt(document, "RecoveryWindowInDays");
        var forceDeleteWithoutRecovery = SecretsManagerOperationSupport.ReadBool(document, "ForceDeleteWithoutRecovery") ?? false;
        if (recoveryWindowInDays is not null && forceDeleteWithoutRecovery)
        {
            await SecretsManagerOperationSupport.WriteAwsErrorAsync(
                context,
                StatusCodes.Status400BadRequest,
                "InvalidParameterException",
                "RecoveryWindowInDays and ForceDeleteWithoutRecovery are mutually exclusive.").ConfigureAwait(false);
            return;
        }

        if (recoveryWindowInDays is not null || forceDeleteWithoutRecovery)
        {
            await SecretsManagerOperationSupport.WriteAwsErrorAsync(
                context,
                StatusCodes.Status501NotImplemented,
                "NotImplementedException",
                "DeleteSecret recovery-window and force-delete options are not supported by aws2azure because Azure Key Vault retention and purge behavior are governed by vault-level soft-delete settings, not per-request AWS parameters.").ConfigureAwait(false);
            return;
        }

        var token = await client.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Delete, client.BuildVaultUri(KeyVaultSecretClient.BuildSecretPath(name)));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            await SecretsManagerOperationSupport.WriteAwsErrorAsync(context, SecretsManagerOperationSupport.MapStatusCode(response.StatusCode), SecretsManagerOperationSupport.MapErrorCode(response.StatusCode), "Key Vault request failed.").ConfigureAwait(false);
            return;
        }

        using var deletedSecretDocument = await SecretsManagerOperationSupport.ReadJsonDocumentAsync(response.Content, cancellationToken).ConfigureAwait(false);
        var deletionDate = TryReadUnixTime(deletedSecretDocument.RootElement, "scheduledPurgeDate")
            ?? TryReadUnixTime(deletedSecretDocument.RootElement, "deletedDate")
            ?? DateTimeOffset.UtcNow;
        var payload = new DeleteSecretResponse(
            Arn: KeyVaultSecretClient.BuildArn(name),
            Name: name,
            DeletionDate: deletionDate,
            DeletedDate: null,
            VersionId: null);

        await SecretsManagerOperationSupport.WriteJsonAsync(context, payload, SecretsManagerJsonContext.Default.DeleteSecretResponse, cancellationToken).ConfigureAwait(false);
    }

    private static DateTimeOffset? TryReadUnixTime(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.Number
            ? DateTimeOffset.FromUnixTimeSeconds(property.GetInt64())
            : null;
}
