using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.SecretsManager.Operations;

internal static class GetSecretValueHandler
{
    public static async Task HandleAsync(
        HttpContext context,
        KeyVaultSecretClient client,
        JsonDocument document,
        CancellationToken cancellationToken)
    {
        var secretId = SecretsManagerOperationSupport.ReadString(document, "SecretId") ?? string.Empty;
        KeyVaultSecretClient.NormalizeSecretName(secretId);
        var token = await client.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        var result = await RetrieveAsync(
            client,
            token,
            secretId,
            SecretsManagerOperationSupport.ReadString(document, "VersionId"),
            SecretsManagerOperationSupport.ReadString(document, "VersionStage"),
            cancellationToken).ConfigureAwait(false);

        if (result.Value is { } value)
        {
            await SecretsManagerOperationSupport.WriteJsonAsync(
                context,
                value,
                SecretsManagerJsonContext.Default.GetSecretValueResponse,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var error = result.Error!;
        await SecretsManagerOperationSupport.WriteAwsErrorAsync(
            context,
            error.StatusCode,
            error.Code,
            error.Message).ConfigureAwait(false);
    }

    internal static async Task<GetSecretValueResult> RetrieveAsync(
        KeyVaultSecretClient client,
        string token,
        string secretId,
        string? requestedVersionId,
        string? requestedVersionStage,
        CancellationToken cancellationToken)
    {
        var normalizedSecretId = KeyVaultSecretClient.NormalizeSecretName(secretId);

        // When no explicit VersionId is requested, the resolved version is almost always the
        // Key Vault "current" (unversioned) version. Speculatively fetch it concurrently with the
        // mandatory version listing so the two Key Vault round trips overlap instead of serializing;
        // if it turns out to match the version resolved from the (still authoritative) full list
        // below, its response is reused directly and the second, version-specific GET is skipped.
        using var speculativeCts = string.IsNullOrWhiteSpace(requestedVersionId)
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : null;
        var speculativeLatestTask = speculativeCts is not null
            ? FetchLatestAsync(client, token, normalizedSecretId, speculativeCts.Token)
            : null;

        try
        {
            var versionList = await SecretVersionCoordinator.ListVersionsCoreAsync(
                client,
                token,
                normalizedSecretId,
                cancellationToken).ConfigureAwait(false);
            if (versionList.StatusCode is { } versionListStatus)
            {
                return BackendFailure(versionListStatus);
            }

            var versions = versionList.Versions!;
            SecretVersionCoordinator.SecretVersionMetadata? selected;
            if (!string.IsNullOrWhiteSpace(requestedVersionId))
            {
                selected = FindVersion(versions, requestedVersionId);
                if (selected is null)
                {
                    using var directRequest = new HttpRequestMessage(
                        HttpMethod.Get,
                        client.BuildVaultUri(KeyVaultSecretClient.BuildSecretVersionPath(normalizedSecretId, requestedVersionId)));
                    directRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    using var directResponse = await client.SendAsync(directRequest, cancellationToken).ConfigureAwait(false);
                    if (directResponse.IsSuccessStatusCode)
                    {
                        using var directDocument = await SecretsManagerOperationSupport.ReadJsonDocumentAsync(
                            directResponse.Content,
                            cancellationToken).ConfigureAwait(false);
                        selected = SecretVersionCoordinator.ReadMetadata(directDocument.RootElement);
                    }
                    else if (directResponse.StatusCode == HttpStatusCode.NotFound)
                    {
                        var tokenResolution = SecretVersionCoordinator.ResolveToken(
                            versions,
                            requestedVersionId,
                            expectedPayloadSha256: null);
                        if (tokenResolution.Conflict)
                        {
                            return Failure(
                                StatusCodes.Status400BadRequest,
                                "ResourceExistsException",
                                "ClientRequestToken is associated with conflicting Key Vault versions.");
                        }

                        selected = tokenResolution.Version;
                    }
                    else
                    {
                        if (await SecretsManagerOperationSupport.IsDisabledSecretVersionAsync(
                            directResponse,
                            cancellationToken).ConfigureAwait(false))
                        {
                            return Failure(
                                StatusCodes.Status404NotFound,
                                "ResourceNotFoundException",
                                "Secrets Manager can't find the specified secret value because the requested Key Vault secret version is disabled.");
                        }

                        return BackendFailure(directResponse.StatusCode);
                    }
                }
            }
            else
            {
                requestedVersionStage ??= "AWSCURRENT";
                var stageResolution = SecretVersionCoordinator.ResolveStage(versions, requestedVersionStage);
                if (stageResolution.Conflict)
                {
                    return Failure(
                        StatusCodes.Status400BadRequest,
                        "ResourceExistsException",
                        $"Multiple Key Vault versions hold staging label '{requestedVersionStage}'.");
                }

                selected = stageResolution.Version;
            }

            if (selected is null)
            {
                var detail = requestedVersionStage is null
                    ? $"version '{requestedVersionId}'"
                    : $"staging label '{requestedVersionStage}'";
                return Failure(
                    StatusCodes.Status404NotFound,
                    "ResourceNotFoundException",
                    $"Secrets Manager can't find the specified secret value for {detail}.");
            }

            HttpResponseMessage? response = null;
            JsonDocument? secretDocument = null;
            if (speculativeLatestTask is not null)
            {
                var speculativeResponse = await speculativeLatestTask.ConfigureAwait(false);
                speculativeLatestTask = null;
                if (speculativeResponse.IsSuccessStatusCode)
                {
                    var speculativeDocument = await SecretsManagerOperationSupport.ReadJsonDocumentAsync(
                        speculativeResponse.Content,
                        cancellationToken).ConfigureAwait(false);
                    var speculativeMetadata = SecretVersionCoordinator.ReadMetadata(speculativeDocument.RootElement);
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
                using var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    client.BuildVaultUri(KeyVaultSecretClient.BuildSecretVersionPath(normalizedSecretId, selected.VersionId)));
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    if (await SecretsManagerOperationSupport.IsDisabledSecretVersionAsync(
                        response,
                        cancellationToken).ConfigureAwait(false))
                    {
                        response.Dispose();
                        return Failure(
                            StatusCodes.Status404NotFound,
                            "ResourceNotFoundException",
                            "Secrets Manager can't find the specified secret value because the requested Key Vault secret version is disabled.");
                    }

                    var result = BackendFailure(response.StatusCode);
                    response.Dispose();
                    return result;
                }

                secretDocument = await SecretsManagerOperationSupport.ReadJsonDocumentAsync(
                    response.Content,
                    cancellationToken).ConfigureAwait(false);
            }

            using var ownedResponse = response;
            using var ownedSecretDocument = secretDocument;
            var secret = secretDocument.RootElement;
            var freshMetadata = SecretVersionCoordinator.ReadMetadata(secret);
            if (!string.IsNullOrWhiteSpace(requestedVersionStage)
                && !ContainsStage(freshMetadata.VersionStages, requestedVersionStage))
            {
                return Failure(
                    StatusCodes.Status400BadRequest,
                    "InvalidRequestException",
                    "VersionId and VersionStage must reference the same secret version.");
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
            return new GetSecretValueResult(
                new GetSecretValueResponse(
                    Arn: KeyVaultSecretClient.BuildArn(normalizedSecretId),
                    Name: normalizedSecretId,
                    VersionId: responseVersionId,
                    SecretString: binary ? null : value,
                    SecretBinary: binary ? KeyVaultSecretClient.EncodeSecretBinary(KeyVaultSecretClient.DecodeSecretBinary(value)) : null,
                    VersionStages: versionStages,
                    CreatedDate: KeyVaultSecretClient.GetCreatedDate(secret)),
                null);
        }
        finally
        {
            speculativeCts?.Cancel();
            await DiscardAsync(speculativeLatestTask).ConfigureAwait(false);
        }
    }

    private static GetSecretValueResult BackendFailure(HttpStatusCode statusCode)
        => Failure(
            SecretsManagerOperationSupport.MapStatusCode(statusCode),
            SecretsManagerOperationSupport.MapErrorCode(statusCode),
            "Key Vault request failed.",
            IsRequestLevelFailure(statusCode));

    private static GetSecretValueResult Failure(
        int statusCode,
        string code,
        string message,
        bool requestLevel = false)
        => new(null, new GetSecretValueError(statusCode, code, message, requestLevel));

    private static bool IsRequestLevelFailure(HttpStatusCode statusCode)
        => statusCode is HttpStatusCode.Unauthorized
            or HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests
            || statusCode >= HttpStatusCode.InternalServerError;

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
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            client.BuildVaultUri(KeyVaultSecretClient.BuildSecretPath(secretId)));
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
        catch (OperationCanceledException)
        {
            // The owning request completed before the speculative fetch.
        }
        catch (HttpRequestException)
        {
            // The authoritative request path has already produced the caller-visible result.
        }
    }

    internal sealed record GetSecretValueError(
        int StatusCode,
        string Code,
        string Message,
        bool RequestLevel);

    internal readonly record struct GetSecretValueResult(
        GetSecretValueResponse? Value,
        GetSecretValueError? Error);
}
