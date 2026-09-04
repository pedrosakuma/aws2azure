using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.SecretsManager.Operations;

internal static class GetSecretValueHandler
{
    public static async Task HandleAsync(HttpContext context, KeyVaultSecretClient client, JsonDocument document, CancellationToken cancellationToken)
    {
        var secretId = KeyVaultSecretClient.NormalizeSecretName(SecretsManagerOperationSupport.ReadString(document, "SecretId") ?? string.Empty);
        var requestedVersionId = SecretsManagerOperationSupport.ReadString(document, "VersionId");
        var requestedVersionStage = SecretsManagerOperationSupport.ReadString(document, "VersionStage");
        var token = await client.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);

        // When no explicit VersionId is requested, the resolved version is almost always the
        // Key Vault "current" (unversioned) version. Speculatively fetch it concurrently with the
        // mandatory version listing so the two Key Vault round trips overlap instead of serializing;
        // if it turns out to match the version resolved from the (still authoritative) full list
        // below, its response is reused directly and the second, version-specific GET is skipped.
        var speculativeLatestTask = string.IsNullOrWhiteSpace(requestedVersionId)
            ? FetchLatestAsync(client, token, secretId, cancellationToken)
            : null;

        var versions = await SecretVersionCoordinator.ListVersionsAsync(context, client, token, secretId, cancellationToken).ConfigureAwait(false);
        if (versions is null)
        {
            await DiscardAsync(speculativeLatestTask).ConfigureAwait(false);
            return;
        }

        SecretVersionCoordinator.SecretVersionMetadata? selected;
        if (!string.IsNullOrWhiteSpace(requestedVersionId))
        {
            selected = FindVersion(versions, requestedVersionId);
            if (selected is null)
            {
                using var directRequest = new HttpRequestMessage(HttpMethod.Get, client.BuildVaultUri(KeyVaultSecretClient.BuildSecretVersionPath(secretId, requestedVersionId)));
                directRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var directResponse = await client.SendAsync(directRequest, cancellationToken).ConfigureAwait(false);
                if (directResponse.IsSuccessStatusCode)
                {
                    using var directDocument = await SecretsManagerOperationSupport.ReadJsonDocumentAsync(directResponse.Content, cancellationToken).ConfigureAwait(false);
                    selected = SecretVersionCoordinator.ReadMetadata(directDocument.RootElement);
                }
                else if (directResponse.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    var tokenResolution = SecretVersionCoordinator.ResolveToken(versions, requestedVersionId, expectedPayloadSha256: null);
                    if (tokenResolution.Conflict)
                    {
                        await SecretVersionCoordinator.WriteConflictAsync(context, "ClientRequestToken is associated with conflicting Key Vault versions.").ConfigureAwait(false);
                        return;
                    }

                    selected = tokenResolution.Version;
                }
                else
                {
                    if (await SecretsManagerOperationSupport.TryWriteDisabledSecretVersionAsNotFoundAsync(context, directResponse, cancellationToken).ConfigureAwait(false))
                    {
                        return;
                    }

                    await SecretsManagerOperationSupport.WriteAwsErrorAsync(context, SecretsManagerOperationSupport.MapStatusCode(directResponse.StatusCode), SecretsManagerOperationSupport.MapErrorCode(directResponse.StatusCode), "Key Vault request failed.").ConfigureAwait(false);
                    return;
                }
            }
        }
        else
        {
            requestedVersionStage ??= "AWSCURRENT";
            var stageResolution = SecretVersionCoordinator.ResolveStage(versions, requestedVersionStage);
            if (stageResolution.Conflict)
            {
                await DiscardAsync(speculativeLatestTask).ConfigureAwait(false);
                await SecretVersionCoordinator.WriteConflictAsync(context, $"Multiple Key Vault versions hold staging label '{requestedVersionStage}'.").ConfigureAwait(false);
                return;
            }

            selected = stageResolution.Version;
        }

        if (selected is null)
        {
            await DiscardAsync(speculativeLatestTask).ConfigureAwait(false);
            var detail = requestedVersionStage is null
                ? $"version '{requestedVersionId}'"
                : $"staging label '{requestedVersionStage}'";
            await SecretsManagerOperationSupport.WriteAwsErrorAsync(context, StatusCodes.Status404NotFound, "ResourceNotFoundException", $"Secrets Manager can't find the specified secret value for {detail}.").ConfigureAwait(false);
            return;
        }

        HttpResponseMessage? response = null;
        JsonDocument? secretDocument = null;
        if (speculativeLatestTask is not null)
        {
            var speculativeResponse = await speculativeLatestTask.ConfigureAwait(false);
            if (speculativeResponse.IsSuccessStatusCode)
            {
                var speculativeDocument = await SecretsManagerOperationSupport.ReadJsonDocumentAsync(speculativeResponse.Content, cancellationToken).ConfigureAwait(false);
                if (string.Equals(SecretVersionCoordinator.ReadMetadata(speculativeDocument.RootElement).VersionId, selected.VersionId, StringComparison.Ordinal))
                {
                    response = speculativeResponse;
                    secretDocument = speculativeDocument;
                }
                else
                {
                    speculativeDocument.Dispose();
                    speculativeResponse.Dispose();
                }
            }
            else
            {
                speculativeResponse.Dispose();
            }
        }

        if (secretDocument is null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, client.BuildVaultUri(KeyVaultSecretClient.BuildSecretVersionPath(secretId, selected.VersionId)));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                if (await SecretsManagerOperationSupport.TryWriteDisabledSecretVersionAsNotFoundAsync(context, response, cancellationToken).ConfigureAwait(false))
                {
                    response.Dispose();
                    return;
                }

                await SecretsManagerOperationSupport.WriteAwsErrorAsync(context, SecretsManagerOperationSupport.MapStatusCode(response.StatusCode), SecretsManagerOperationSupport.MapErrorCode(response.StatusCode), "Key Vault request failed.").ConfigureAwait(false);
                response.Dispose();
                return;
            }

            secretDocument = await SecretsManagerOperationSupport.ReadJsonDocumentAsync(response.Content, cancellationToken).ConfigureAwait(false);
        }

        using var ownedResponse = response;
        using var ownedSecretDocument = secretDocument;
        var secret = secretDocument.RootElement;
        var freshMetadata = SecretVersionCoordinator.ReadMetadata(secret);
        if (!string.IsNullOrWhiteSpace(requestedVersionStage)
            && !ContainsStage(freshMetadata.VersionStages, requestedVersionStage))
        {
            await SecretsManagerOperationSupport.WriteAwsErrorAsync(context, StatusCodes.Status400BadRequest, "InvalidRequestException", "VersionId and VersionStage must reference the same secret version.").ConfigureAwait(false);
            return;
        }

        var value = secret.TryGetProperty("value", out var valueElement) && valueElement.ValueKind == JsonValueKind.String
            ? valueElement.GetString() ?? string.Empty
            : string.Empty;
        var contentType = secret.TryGetProperty("contentType", out var contentTypeElement) && contentTypeElement.ValueKind == JsonValueKind.String
            ? contentTypeElement.GetString()
            : null;
        var responseVersionId = freshMetadata.Tags.TryGetValue(KeyVaultSecretClient.ClientRequestTokenTag, out var clientRequestToken)
            ? clientRequestToken
            : freshMetadata.VersionId;
        var versionStages = freshMetadata.HasStoredStages ? freshMetadata.VersionStages : ["AWSCURRENT"];
        var binary = string.Equals(contentType, "application/octet-stream", StringComparison.OrdinalIgnoreCase);
        var payload = new GetSecretValueResponse(
            Arn: KeyVaultSecretClient.BuildArn(secretId),
            Name: secretId,
            VersionId: responseVersionId,
            SecretString: binary ? null : value,
            SecretBinary: binary ? KeyVaultSecretClient.EncodeSecretBinary(KeyVaultSecretClient.DecodeSecretBinary(value)) : null,
            VersionStages: versionStages,
            CreatedDate: KeyVaultSecretClient.GetCreatedDate(secret));
        await SecretsManagerOperationSupport.WriteJsonAsync(context, payload, SecretsManagerJsonContext.Default.GetSecretValueResponse, cancellationToken).ConfigureAwait(false);
    }

    private static SecretVersionCoordinator.SecretVersionMetadata? FindVersion(
        IReadOnlyList<SecretVersionCoordinator.SecretVersionMetadata> versions,
        string versionId)
    {
        foreach (var version in versions)
        {
            if (string.Equals(version.VersionId, versionId, StringComparison.Ordinal))
            {
                return version;
            }
        }

        return null;
    }

    private static bool ContainsStage(IReadOnlyList<string> stages, string stage)
    {
        foreach (var candidate in stages)
        {
            if (string.Equals(candidate, stage, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<HttpResponseMessage> FetchLatestAsync(
        KeyVaultSecretClient client,
        string token,
        string secretId,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, client.BuildVaultUri(KeyVaultSecretClient.BuildSecretPath(secretId)));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static async Task DiscardAsync(Task<HttpResponseMessage>? task)
    {
        if (task is null)
        {
            return;
        }

        try
        {
            (await task.ConfigureAwait(false)).Dispose();
        }
        catch
        {
            // Best-effort cleanup of a speculative prefetch whose result is no longer needed;
            // any failure here is irrelevant to the response already written for this request.
        }
    }
}
