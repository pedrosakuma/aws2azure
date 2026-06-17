using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.SecretsManager.Operations;

internal static class GetSecretValueHandler
{
    public static async Task HandleAsync(HttpContext context, KeyVaultSecretClient client, JsonDocument document, CancellationToken cancellationToken)
    {
        var secretId = KeyVaultSecretClient.NormalizeSecretName(SecretsManagerOperationSupport.ReadString(document, "SecretId") ?? string.Empty);
        var versionId = SecretsManagerOperationSupport.ReadString(document, "VersionId");
        var token = await client.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        var path = versionId is null
            ? KeyVaultSecretClient.BuildSecretPath(secretId)
            : KeyVaultSecretClient.BuildSecretVersionPath(secretId, versionId);
        using var request = new HttpRequestMessage(HttpMethod.Get, client.BuildVaultUri(path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            await SecretsManagerOperationSupport.WriteAwsErrorAsync(context, SecretsManagerOperationSupport.MapStatusCode(response.StatusCode), SecretsManagerOperationSupport.MapErrorCode(response.StatusCode), "Key Vault request failed.").ConfigureAwait(false);
            return;
        }

        using var secretDocument = await SecretsManagerOperationSupport.ReadJsonDocumentAsync(response.Content, cancellationToken).ConfigureAwait(false);
        var value = secretDocument.RootElement.TryGetProperty("value", out var valueElement) && valueElement.ValueKind == JsonValueKind.String
            ? valueElement.GetString() ?? string.Empty
            : string.Empty;
        var id = secretDocument.RootElement.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String
            ? idElement.GetString() ?? string.Empty
            : string.Empty;
        var contentType = secretDocument.RootElement.TryGetProperty("contentType", out var contentTypeElement) && contentTypeElement.ValueKind == JsonValueKind.String
            ? contentTypeElement.GetString()
            : null;
        var createdDate = KeyVaultSecretClient.GetCreatedDate(secretDocument.RootElement);

        var secretBinary = string.Equals(contentType, "application/octet-stream", StringComparison.OrdinalIgnoreCase)
            ? KeyVaultSecretClient.EncodeSecretBinary(KeyVaultSecretClient.DecodeSecretBinary(value))
            : null;

        var secretString = string.Equals(contentType, "application/octet-stream", StringComparison.OrdinalIgnoreCase)
            ? null
            : value;

        var payload = new GetSecretValueResponse(
            Arn: KeyVaultSecretClient.BuildArn(secretId),
            Name: secretId,
            VersionId: KeyVaultSecretClient.GetVersionId(id),
            SecretString: secretString,
            SecretBinary: secretBinary,
            VersionStages: ["AWSCURRENT"],
            CreatedDate: createdDate);

        await SecretsManagerOperationSupport.WriteJsonAsync(context, payload, SecretsManagerJsonContext.Default.GetSecretValueResponse, cancellationToken).ConfigureAwait(false);
    }
}
