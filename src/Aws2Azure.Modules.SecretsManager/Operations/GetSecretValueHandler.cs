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
        // The fetch is linked to a cancellable token: if the mandatory list call fails or any other
        // early return happens before the speculative response is actually consumed, the `finally`
        // below cancels it, bounding wasted Key Vault load during backend outages/throttling instead
        // of always waiting the fetch out to completion.
        using var speculativeCts = string.IsNullOrWhiteSpace(requestedVersionId)
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : null;
        var speculativeLatestTask = speculativeCts is not null
            ? FetchLatestAsync(client, token, secretId, speculativeCts.Token)
            : null;

        try
        {
            var versions = await SecretVersionCoordinator.ListVersionsAsync(context, client, token, secretId, cancellationToken).ConfigureAwait(false);
            if (versions is null)
            {
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
                    await SecretVersionCoordinator.WriteConflictAsync(context, $"Multiple Key Vault versions hold staging label '{requestedVersionStage}'.").ConfigureAwait(false);
                    return;
                }

                selected = stageResolution.Version;
            }

            if (selected is null)
            {
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
                // The task is now fully awaited; clear it so the outer `finally` never touches it
                // again, regardless of what happens later in this method (including exceptions).
                speculativeLatestTask = null;
                if (speculativeResponse.IsSuccessStatusCode)
                {
                    var speculativeDocument = await SecretsManagerOperationSupport.ReadJsonDocumentAsync(speculativeResponse.Content, cancellationToken).ConfigureAwait(false);
                    var speculativeMetadata = SecretVersionCoordinator.ReadMetadata(speculativeDocument.RootElement);
                    // Matching on VersionId alone is not sufficient: PutSecretValue publishes a new
                    // version, then PATCHes its staging-label tags onto that *same* VersionId across
                    // one or more later round trips (SecretVersionCoordinator.PublishVersionAsync). The
                    // speculative GET can snapshot the version before those tags land while the
                    // (still authoritative) list call observes them afterward, so the tags themselves
                    // must also agree before the speculative response can be trusted.
                    if (string.Equals(speculativeMetadata.VersionId, selected.VersionId, StringComparison.Ordinal)
                        && TagsEqual(speculativeMetadata.Tags, selected.Tags))
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
        finally
        {
            // If the speculative task is still non-null here, it was never consumed (an early
            // return or an exception happened first): cancel it so an in-flight speculative fetch
            // doesn't keep loading Key Vault after the request it was prefetching for has already
            // concluded, then guarantee it is always drained/disposed exactly once.
            speculativeCts?.Cancel();
            await DiscardAsync(speculativeLatestTask).ConfigureAwait(false);
        }
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

    private static bool TagsEqual(IReadOnlyDictionary<string, string> left, IReadOnlyDictionary<string, string> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        foreach (var pair in left)
        {
            if (!right.TryGetValue(pair.Key, out var value) || !string.Equals(value, pair.Value, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
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
