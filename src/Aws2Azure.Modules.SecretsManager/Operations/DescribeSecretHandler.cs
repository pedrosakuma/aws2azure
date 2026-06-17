using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.SecretsManager.Operations;

internal static class DescribeSecretHandler
{
    public static async Task HandleAsync(HttpContext context, KeyVaultSecretClient client, JsonDocument document, CancellationToken cancellationToken)
    {
        var name = KeyVaultSecretClient.NormalizeSecretName(SecretsManagerOperationSupport.ReadString(document, "SecretId") ?? string.Empty);
        var token = await client.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Get, client.BuildVaultUri(KeyVaultSecretClient.BuildSecretPath(name)));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            await SecretsManagerOperationSupport.WriteAwsErrorAsync(context, SecretsManagerOperationSupport.MapStatusCode(response.StatusCode), SecretsManagerOperationSupport.MapErrorCode(response.StatusCode), "Key Vault request failed.").ConfigureAwait(false);
            return;
        }

        using var secretDocument = await SecretsManagerOperationSupport.ReadJsonDocumentAsync(response.Content, cancellationToken).ConfigureAwait(false);
        var nameValue = secretDocument.RootElement.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String ? nameElement.GetString() ?? string.Empty : name;
        var id = secretDocument.RootElement.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String ? idElement.GetString() ?? string.Empty : string.Empty;
        var created = KeyVaultSecretClient.GetCreatedDate(secretDocument.RootElement);
        var lastChanged = KeyVaultSecretClient.GetLastChangedDate(secretDocument.RootElement);
        var description = KeyVaultSecretClient.GetDescription(secretDocument.RootElement);
        var tags = KeyVaultSecretClient.GetTags(secretDocument.RootElement);
        var versionIdsToStages = string.IsNullOrWhiteSpace(id)
            ? null
            : new Dictionary<string, IReadOnlyList<string>>
            {
                [KeyVaultSecretClient.GetVersionId(id)] = ["AWSCURRENT"],
            };

        var payload = new DescribeSecretResponse(
            Arn: KeyVaultSecretClient.BuildArn(nameValue),
            Name: nameValue,
            Description: description,
            CreatedDate: created,
            LastChangedDate: lastChanged,
            Tags: SecretsManagerOperationSupport.ToTagArray(tags),
            VersionIdsToStages: versionIdsToStages,
            RotationEnabled: null,
            DeletedDate: null);

        await SecretsManagerOperationSupport.WriteJsonAsync(context, payload, SecretsManagerJsonContext.Default.DescribeSecretResponse, cancellationToken).ConfigureAwait(false);
    }
}
