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
        var versionStage = SecretsManagerOperationSupport.ReadString(document, "VersionStage");
        var token = await client.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        if (versionId is null)
        {
            versionStage ??= "AWSCURRENT";
            var resolvedVersion = await ResolveVersionIdForStageAsync(context, client, token, secretId, versionStage, cancellationToken).ConfigureAwait(false);
            if (resolvedVersion.ErrorWritten)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(resolvedVersion.VersionId))
            {
                await SecretsManagerOperationSupport.WriteAwsErrorAsync(context, StatusCodes.Status404NotFound, "ResourceNotFoundException", $"Secrets Manager can't find the specified secret value for staging label '{versionStage}'.").ConfigureAwait(false);
                return;
            }

            versionId = resolvedVersion.VersionId;
        }

        var path = KeyVaultSecretClient.BuildSecretVersionPath(secretId, versionId);
        using var request = new HttpRequestMessage(HttpMethod.Get, client.BuildVaultUri(path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            if (document.RootElement.TryGetProperty("VersionId", out _)
                && response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                var resolvedVersion = await ResolveVersionIdForClientRequestTokenAsync(context, client, token, secretId, versionId, cancellationToken).ConfigureAwait(false);
                if (resolvedVersion.ErrorWritten)
                {
                    return;
                }

                if (!string.IsNullOrWhiteSpace(resolvedVersion.VersionId))
                {
                    await WriteSecretValueAsync(context, client, token, secretId, resolvedVersion.VersionId, versionStage, cancellationToken).ConfigureAwait(false);
                    return;
                }
            }

            await SecretsManagerOperationSupport.WriteAwsErrorAsync(context, SecretsManagerOperationSupport.MapStatusCode(response.StatusCode), SecretsManagerOperationSupport.MapErrorCode(response.StatusCode), "Key Vault request failed.").ConfigureAwait(false);
            return;
        }

        using var secretDocument = await SecretsManagerOperationSupport.ReadJsonDocumentAsync(response.Content, cancellationToken).ConfigureAwait(false);
        await WriteSecretValueAsync(context, secretId, secretDocument.RootElement, versionStage, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteSecretValueAsync(
        HttpContext context,
        KeyVaultSecretClient client,
        string token,
        string secretId,
        string versionId,
        string? versionStage,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, client.BuildVaultUri(KeyVaultSecretClient.BuildSecretVersionPath(secretId, versionId)));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            await SecretsManagerOperationSupport.WriteAwsErrorAsync(context, SecretsManagerOperationSupport.MapStatusCode(response.StatusCode), SecretsManagerOperationSupport.MapErrorCode(response.StatusCode), "Key Vault request failed.").ConfigureAwait(false);
            return;
        }

        using var secretDocument = await SecretsManagerOperationSupport.ReadJsonDocumentAsync(response.Content, cancellationToken).ConfigureAwait(false);
        await WriteSecretValueAsync(context, secretId, secretDocument.RootElement, versionStage, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteSecretValueAsync(
        HttpContext context,
        string secretId,
        JsonElement secret,
        string? requestedVersionStage,
        CancellationToken cancellationToken)
    {
        if (!VersionMatchesStage(secret, requestedVersionStage))
        {
            await SecretsManagerOperationSupport.WriteAwsErrorAsync(context, StatusCodes.Status400BadRequest, "InvalidRequestException", "VersionId and VersionStage must reference the same secret version.").ConfigureAwait(false);
            return;
        }

        var value = secret.TryGetProperty("value", out var valueElement) && valueElement.ValueKind == JsonValueKind.String
            ? valueElement.GetString() ?? string.Empty
            : string.Empty;
        var id = secret.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String
            ? idElement.GetString() ?? string.Empty
            : string.Empty;
        var contentType = secret.TryGetProperty("contentType", out var contentTypeElement) && contentTypeElement.ValueKind == JsonValueKind.String
            ? contentTypeElement.GetString()
            : null;
        var createdDate = KeyVaultSecretClient.GetCreatedDate(secret);
        var versionStages = KeyVaultSecretClient.GetVersionStages(secret);
        var responseVersionId = KeyVaultSecretClient.TryGetRawTag(secret, KeyVaultSecretClient.ClientRequestTokenTag, out var clientRequestToken)
            ? clientRequestToken
            : KeyVaultSecretClient.GetVersionId(id);

        var secretBinary = string.Equals(contentType, "application/octet-stream", StringComparison.OrdinalIgnoreCase)
            ? KeyVaultSecretClient.EncodeSecretBinary(KeyVaultSecretClient.DecodeSecretBinary(value))
            : null;

        var secretString = string.Equals(contentType, "application/octet-stream", StringComparison.OrdinalIgnoreCase)
            ? null
            : value;

        var payload = new GetSecretValueResponse(
            Arn: KeyVaultSecretClient.BuildArn(secretId),
            Name: secretId,
            VersionId: responseVersionId,
            SecretString: secretString,
            SecretBinary: secretBinary,
            VersionStages: versionStages,
            CreatedDate: createdDate);

        await SecretsManagerOperationSupport.WriteJsonAsync(context, payload, SecretsManagerJsonContext.Default.GetSecretValueResponse, cancellationToken).ConfigureAwait(false);
    }

    private static bool VersionMatchesStage(JsonElement secret, string? requestedVersionStage)
    {
        if (string.IsNullOrWhiteSpace(requestedVersionStage))
        {
            return true;
        }

        var hasStoredStages = KeyVaultSecretClient.TryGetRawTag(secret, KeyVaultSecretClient.VersionStagesTag, out _);
        if (!hasStoredStages)
        {
            return string.Equals(requestedVersionStage, "AWSCURRENT", StringComparison.Ordinal);
        }

        foreach (var stage in KeyVaultSecretClient.GetVersionStages(secret))
        {
            if (string.Equals(stage, requestedVersionStage, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<StageResolutionResult> ResolveVersionIdForStageAsync(
        HttpContext context,
        KeyVaultSecretClient client,
        string token,
        string secretId,
        string versionStage,
        CancellationToken cancellationToken)
    {
        var nextToken = string.Empty;
        string? untaggedCurrentFallback = null;
        long untaggedCurrentFallbackCreated = -1;
        string? stageMatch = null;
        long stageMatchCreated = -1;
        do
        {
            var requestUri = client.BuildVaultUri(KeyVaultSecretClient.BuildSecretVersionsPath(secretId));
            if (!string.IsNullOrWhiteSpace(nextToken))
            {
                requestUri += "&$skiptoken=" + Uri.EscapeDataString(nextToken);
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                await SecretsManagerOperationSupport.WriteAwsErrorAsync(context, SecretsManagerOperationSupport.MapStatusCode(response.StatusCode), SecretsManagerOperationSupport.MapErrorCode(response.StatusCode), "Key Vault request failed.").ConfigureAwait(false);
                return new StageResolutionResult(null, true);
            }

            using var versionsDocument = await SecretsManagerOperationSupport.ReadJsonDocumentAsync(response.Content, cancellationToken).ConfigureAwait(false);
            if (versionsDocument.RootElement.TryGetProperty("value", out var versions) && versions.ValueKind == JsonValueKind.Array)
            {
                foreach (var version in versions.EnumerateArray())
                {
                    var id = version.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String
                        ? idElement.GetString() ?? string.Empty
                        : string.Empty;
                    var candidateVersionId = KeyVaultSecretClient.GetVersionId(id);
                    var candidateCreated = GetCreatedUnixTime(version);
                    var hasStoredStages = KeyVaultSecretClient.TryGetRawTag(version, KeyVaultSecretClient.VersionStagesTag, out _);
                    var stages = hasStoredStages ? KeyVaultSecretClient.GetVersionStages(version) : [];
                    foreach (var stage in stages)
                    {
                        if (string.Equals(stage, versionStage, StringComparison.Ordinal))
                        {
                            if (candidateCreated > stageMatchCreated)
                            {
                                stageMatch = candidateVersionId;
                                stageMatchCreated = candidateCreated;
                            }
                        }
                    }

                    if (!hasStoredStages
                        && string.Equals(versionStage, "AWSCURRENT", StringComparison.Ordinal))
                    {
                        if (candidateCreated > untaggedCurrentFallbackCreated)
                        {
                            untaggedCurrentFallback = candidateVersionId;
                            untaggedCurrentFallbackCreated = candidateCreated;
                        }
                    }
                }
            }

            nextToken = versionsDocument.RootElement.TryGetProperty("nextLink", out var nextLink) && nextLink.ValueKind == JsonValueKind.String
                ? KeyVaultSecretClient.ExtractSkipToken(nextLink.GetString()) ?? string.Empty
                : string.Empty;
        }
        while (!string.IsNullOrWhiteSpace(nextToken));

        return new StageResolutionResult(stageMatch ?? untaggedCurrentFallback, false);
    }

    private static long GetCreatedUnixTime(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("attributes", out var attributes)
            && attributes.ValueKind == JsonValueKind.Object
            && attributes.TryGetProperty("created", out var created)
            && created.ValueKind == JsonValueKind.Number
            && created.TryGetInt64(out var value))
        {
            return value;
        }

        return 0;
    }

    private static async Task<StageResolutionResult> ResolveVersionIdForClientRequestTokenAsync(
        HttpContext context,
        KeyVaultSecretClient client,
        string token,
        string secretId,
        string clientRequestToken,
        CancellationToken cancellationToken)
    {
        var nextToken = string.Empty;
        do
        {
            var requestUri = client.BuildVaultUri(KeyVaultSecretClient.BuildSecretVersionsPath(secretId));
            if (!string.IsNullOrWhiteSpace(nextToken))
            {
                requestUri += "&$skiptoken=" + Uri.EscapeDataString(nextToken);
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                await SecretsManagerOperationSupport.WriteAwsErrorAsync(context, SecretsManagerOperationSupport.MapStatusCode(response.StatusCode), SecretsManagerOperationSupport.MapErrorCode(response.StatusCode), "Key Vault request failed.").ConfigureAwait(false);
                return new StageResolutionResult(null, true);
            }

            using var versionsDocument = await SecretsManagerOperationSupport.ReadJsonDocumentAsync(response.Content, cancellationToken).ConfigureAwait(false);
            if (versionsDocument.RootElement.TryGetProperty("value", out var versions) && versions.ValueKind == JsonValueKind.Array)
            {
                foreach (var version in versions.EnumerateArray())
                {
                    if (!KeyVaultSecretClient.TryGetRawTag(version, KeyVaultSecretClient.ClientRequestTokenTag, out var candidateToken)
                        || !string.Equals(candidateToken, clientRequestToken, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var id = version.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String
                        ? idElement.GetString() ?? string.Empty
                        : string.Empty;
                    return new StageResolutionResult(KeyVaultSecretClient.GetVersionId(id), false);
                }
            }

            nextToken = versionsDocument.RootElement.TryGetProperty("nextLink", out var nextLink) && nextLink.ValueKind == JsonValueKind.String
                ? KeyVaultSecretClient.ExtractSkipToken(nextLink.GetString()) ?? string.Empty
                : string.Empty;
        }
        while (!string.IsNullOrWhiteSpace(nextToken));

        return new StageResolutionResult(null, false);
    }

    private readonly record struct StageResolutionResult(string? VersionId, bool ErrorWritten);
}
