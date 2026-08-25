using System.Collections.Generic;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.SecretsManager.Operations;

internal static class TagResourceHandler
{
    public static async Task HandleAsync(HttpContext context, KeyVaultSecretClient client, JsonDocument document, CancellationToken cancellationToken)
    {
        var name = KeyVaultSecretClient.NormalizeSecretName(SecretsManagerOperationSupport.ReadString(document, "SecretId") ?? string.Empty);
        var newTags = KeyVaultSecretClient.GetTags(document.RootElement);
        var token = await client.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        await using var secretLock = await SecretVersionCoordinator.AcquireLockAsync(name, cancellationToken).ConfigureAwait(false);

        using var getRequest = new HttpRequestMessage(HttpMethod.Get, client.BuildVaultUri(KeyVaultSecretClient.BuildSecretPath(name)));
        getRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var getResponse = await client.SendAsync(getRequest, cancellationToken).ConfigureAwait(false);
        if (!getResponse.IsSuccessStatusCode)
        {
            await SecretsManagerOperationSupport.WriteAwsErrorAsync(context, SecretsManagerOperationSupport.MapStatusCode(getResponse.StatusCode), SecretsManagerOperationSupport.MapErrorCode(getResponse.StatusCode), "Key Vault request failed.").ConfigureAwait(false);
            return;
        }

        using var currentDocument = await SecretsManagerOperationSupport.ReadJsonDocumentAsync(getResponse.Content, cancellationToken).ConfigureAwait(false);
        var mergedTags = new Dictionary<string, string>(KeyVaultSecretClient.GetRawTags(currentDocument.RootElement), StringComparer.Ordinal);
        foreach (var tag in newTags)
        {
            mergedTags[tag.Key] = tag.Value;
        }
        var versionId = ResolveVersionId(currentDocument.RootElement);

        using var patchRequest = new HttpRequestMessage(HttpMethod.Patch, client.BuildVaultUri(KeyVaultSecretClient.BuildSecretVersionPath(name, versionId)));
        patchRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        patchRequest.Content = new StringContent(KeyVaultSecretClient.BuildTagsJsonBody(mergedTags), Encoding.UTF8, "application/json");

        using var patchResponse = await client.SendAsync(patchRequest, cancellationToken).ConfigureAwait(false);
        if (!patchResponse.IsSuccessStatusCode)
        {
            await SecretsManagerOperationSupport.WriteAwsErrorAsync(context, SecretsManagerOperationSupport.MapStatusCode(patchResponse.StatusCode), SecretsManagerOperationSupport.MapErrorCode(patchResponse.StatusCode), "Key Vault request failed.").ConfigureAwait(false);
            return;
        }

        await SecretsManagerOperationSupport.WriteEmptySuccessAsync(context).ConfigureAwait(false);
    }

    private static string ResolveVersionId(JsonElement root)
    {
        var id = root.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String
            ? idElement.GetString() ?? string.Empty
            : string.Empty;
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new InvalidOperationException("Key Vault secret response did not include a version id.");
        }

        return KeyVaultSecretClient.GetVersionId(id);
    }
}
