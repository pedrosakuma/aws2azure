using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

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

        using var request = new HttpRequestMessage(HttpMethod.Put, client.BuildVaultUri(KeyVaultSecretClient.BuildSecretPath(name)));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(KeyVaultSecretClient.BuildJsonBody(
            SecretsManagerOperationSupport.ReadString(document, "SecretString"),
            SecretsManagerOperationSupport.ReadString(document, "SecretBinary"),
            SecretsManagerOperationSupport.ReadString(document, "Description"),
            KeyVaultSecretClient.WithVersionStages(KeyVaultSecretClient.GetTags(document.RootElement), ["AWSCURRENT"])), Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            await SecretsManagerOperationSupport.WriteAwsErrorAsync(context, SecretsManagerOperationSupport.MapStatusCode(response.StatusCode), SecretsManagerOperationSupport.MapErrorCode(response.StatusCode), "Key Vault request failed.").ConfigureAwait(false);
            return;
        }

        using var secretDocument = await SecretsManagerOperationSupport.ReadJsonDocumentAsync(response.Content, cancellationToken).ConfigureAwait(false);
        var id = secretDocument.RootElement.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String
            ? idElement.GetString() ?? string.Empty
            : string.Empty;
        var createdDate = KeyVaultSecretClient.GetCreatedDate(secretDocument.RootElement);
        var newVersionId = KeyVaultSecretClient.GetVersionId(id);

        // Demote any prior AWSCURRENT version so only the new version is current.
        // Without this, Key Vault list-by-stage sees two AWSCURRENT versions and,
        // because `created` has 1s granularity, a same-second update can resolve
        // AWSCURRENT to the stale version (read-your-write regression, #484).
        await DemotePriorCurrentAsync(client, token, name, newVersionId, cancellationToken).ConfigureAwait(false);

        var payload = new UpdateSecretResponse(
            Arn: KeyVaultSecretClient.BuildArn(name),
            Name: name,
            VersionId: newVersionId,
            VersionStages: ["AWSCURRENT"],
            CreatedDate: createdDate);

        await SecretsManagerOperationSupport.WriteJsonAsync(context, payload, SecretsManagerJsonContext.Default.UpdateSecretResponse, cancellationToken).ConfigureAwait(false);
    }

    private static async Task DemotePriorCurrentAsync(KeyVaultSecretClient client, string token, string name, string newVersionId, CancellationToken cancellationToken)
    {
        using var listRequest = new HttpRequestMessage(HttpMethod.Get, client.BuildVaultUri(KeyVaultSecretClient.BuildSecretVersionsPath(name)));
        listRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var listResponse = await client.SendAsync(listRequest, cancellationToken).ConfigureAwait(false);
        if (!listResponse.IsSuccessStatusCode)
        {
            return;
        }

        using var versions = await SecretsManagerOperationSupport.ReadJsonDocumentAsync(listResponse.Content, cancellationToken).ConfigureAwait(false);
        if (!versions.RootElement.TryGetProperty("value", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var version in array.EnumerateArray())
        {
            var vid = version.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                ? KeyVaultSecretClient.GetVersionId(idEl.GetString() ?? string.Empty)
                : string.Empty;
            if (string.IsNullOrEmpty(vid) || string.Equals(vid, newVersionId, StringComparison.Ordinal))
            {
                continue;
            }

            var demoted = KeyVaultSecretClient.WithVersionStages(KeyVaultSecretClient.GetRawTags(version), ["AWSPREVIOUS"]);
            using var patch = new HttpRequestMessage(HttpMethod.Patch, client.BuildVaultUri(KeyVaultSecretClient.BuildSecretVersionPath(name, vid)));
            patch.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            patch.Content = new StringContent(KeyVaultSecretClient.BuildTagsJsonBody(demoted), Encoding.UTF8, "application/json");
            using var _ = await client.SendAsync(patch, cancellationToken).ConfigureAwait(false);
        }
    }
}
