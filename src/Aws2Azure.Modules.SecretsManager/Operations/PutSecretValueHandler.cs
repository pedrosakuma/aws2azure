using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.SecretsManager.Operations;

internal static class PutSecretValueHandler
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> IdempotencyLocks = new(StringComparer.Ordinal);

    public static async Task HandleAsync(HttpContext context, KeyVaultSecretClient client, JsonDocument document, CancellationToken cancellationToken)
    {
        var name = KeyVaultSecretClient.NormalizeSecretName(SecretsManagerOperationSupport.ReadString(document, "SecretId") ?? string.Empty);
        var secretString = SecretsManagerOperationSupport.ReadString(document, "SecretString");
        var secretBinary = SecretsManagerOperationSupport.ReadString(document, "SecretBinary");
        var versionStagesSpecified = document.RootElement.TryGetProperty("VersionStages", out _);
        var versionStages = KeyVaultSecretClient.ReadVersionStages(document);
        var clientRequestToken = SecretsManagerOperationSupport.ReadString(document, "ClientRequestToken");
        var contentType = string.IsNullOrWhiteSpace(secretBinary) ? null : "application/octet-stream";
        var storedValue = string.IsNullOrWhiteSpace(secretBinary)
            ? secretString
            : KeyVaultSecretClient.EncodeSecretBinary(KeyVaultSecretClient.DecodeSecretBinary(secretBinary));
        var payloadSha256 = KeyVaultSecretClient.GetPayloadSha256(storedValue, contentType);
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

        if (string.IsNullOrWhiteSpace(clientRequestToken))
        {
            await WriteNewVersionAsync(context, client, token, name, secretString, secretBinary, clientRequestToken, payloadSha256, versionStages, versionStagesSpecified, cancellationToken).ConfigureAwait(false);
            return;
        }

        var idempotencyLock = IdempotencyLocks.GetOrAdd(name, static _ => new SemaphoreSlim(1, 1));
        await idempotencyLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await TryWriteIdempotentResponseAsync(context, client, token, name, clientRequestToken, payloadSha256, versionStages, versionStagesSpecified, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            await WriteNewVersionAsync(context, client, token, name, secretString, secretBinary, clientRequestToken, payloadSha256, versionStages, versionStagesSpecified, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            idempotencyLock.Release();
        }
    }

    private static async Task WriteNewVersionAsync(
        HttpContext context,
        KeyVaultSecretClient client,
        string token,
        string name,
        string? secretString,
        string? secretBinary,
        string? clientRequestToken,
        string payloadSha256,
        IReadOnlyList<string> versionStages,
        bool versionStagesSpecified,
        CancellationToken cancellationToken)
    {
        var existingVersions = await ListVersionsAsync(context, client, token, name, cancellationToken).ConfigureAwait(false);
        if (existingVersions is null)
        {
            return;
        }

        var internalTags = KeyVaultSecretClient.BuildInternalTags(clientRequestToken, payloadSha256, versionStages);
        using var request = new HttpRequestMessage(HttpMethod.Put, client.BuildVaultUri(KeyVaultSecretClient.BuildSecretPath(name)));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(KeyVaultSecretClient.BuildJsonBody(
            secretString,
            secretBinary,
            null,
            internalTags), Encoding.UTF8, "application/json");

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
        var newVersionId = KeyVaultSecretClient.GetVersionId(id);
        var responseTags = KeyVaultSecretClient.GetRawTags(secretDocument.RootElement);
        var newVersion = new SecretVersionMetadata(
            newVersionId,
            responseTags.Count == 0 ? internalTags : responseTags,
            KeyVaultSecretClient.GetCreatedDate(secretDocument.RootElement).ToUnixTimeSeconds(),
            HasStoredStages: true);
        if (!await ApplyStageTransitionsAsync(context, client, token, name, existingVersions, newVersion, versionStages, versionStagesSpecified, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var payload = new PutSecretValueResponse(
            Arn: KeyVaultSecretClient.BuildArn(name),
            Name: name,
            VersionId: string.IsNullOrWhiteSpace(clientRequestToken) ? newVersionId : clientRequestToken,
            VersionStages: versionStages);

        await SecretsManagerOperationSupport.WriteJsonAsync(context, payload, SecretsManagerJsonContext.Default.PutSecretValueResponse, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> TryWriteIdempotentResponseAsync(
        HttpContext context,
        KeyVaultSecretClient client,
        string token,
        string name,
        string clientRequestToken,
        string payloadSha256,
        IReadOnlyList<string> versionStages,
        bool versionStagesSpecified,
        CancellationToken cancellationToken)
    {
        var existingVersions = await ListVersionsAsync(context, client, token, name, cancellationToken).ConfigureAwait(false);
        if (existingVersions is null)
        {
            return true;
        }

        foreach (var version in existingVersions)
        {
            if (!version.Tags.TryGetValue(KeyVaultSecretClient.ClientRequestTokenTag, out var candidateToken)
                || !string.Equals(candidateToken, clientRequestToken, StringComparison.Ordinal))
            {
                continue;
            }

            if (!version.Tags.TryGetValue(KeyVaultSecretClient.PayloadSha256Tag, out var candidateSha256)
                || !string.Equals(candidateSha256, payloadSha256, StringComparison.Ordinal))
            {
                await SecretsManagerOperationSupport.WriteAwsErrorAsync(context, StatusCodes.Status409Conflict, "ResourceExistsException", "ClientRequestToken is already associated with a different secret value.").ConfigureAwait(false);
                return true;
            }

            var effectiveStages = version.HasStoredStages ? version.VersionStages : versionStages;
            if (!await ApplyStageTransitionsAsync(context, client, token, name, existingVersions, version, effectiveStages, version.HasStoredStages || versionStagesSpecified, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

            var payload = new PutSecretValueResponse(
                Arn: KeyVaultSecretClient.BuildArn(name),
                Name: name,
                VersionId: candidateToken,
                VersionStages: effectiveStages);
            await SecretsManagerOperationSupport.WriteJsonAsync(context, payload, SecretsManagerJsonContext.Default.PutSecretValueResponse, cancellationToken).ConfigureAwait(false);
            return true;
        }

        return false;
    }

    private static async Task<List<SecretVersionMetadata>?> ListVersionsAsync(
        HttpContext context,
        KeyVaultSecretClient client,
        string token,
        string name,
        CancellationToken cancellationToken)
    {
        var result = new List<SecretVersionMetadata>();
        var nextToken = string.Empty;
        do
        {
            var requestUri = client.BuildVaultUri(KeyVaultSecretClient.BuildSecretVersionsPath(name));
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
                return null;
            }

            using var versionsDocument = await SecretsManagerOperationSupport.ReadJsonDocumentAsync(response.Content, cancellationToken).ConfigureAwait(false);
            if (versionsDocument.RootElement.TryGetProperty("value", out var versions) && versions.ValueKind == JsonValueKind.Array)
            {
                foreach (var version in versions.EnumerateArray())
                {
                    var id = version.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String
                        ? idElement.GetString() ?? string.Empty
                        : string.Empty;
                    result.Add(new SecretVersionMetadata(
                        KeyVaultSecretClient.GetVersionId(id),
                        KeyVaultSecretClient.GetRawTags(version),
                        KeyVaultSecretClient.GetCreatedDate(version).ToUnixTimeSeconds(),
                        KeyVaultSecretClient.TryGetRawTag(version, KeyVaultSecretClient.VersionStagesTag, out _)));
                }
            }

            nextToken = versionsDocument.RootElement.TryGetProperty("nextLink", out var nextLink) && nextLink.ValueKind == JsonValueKind.String
                ? KeyVaultSecretClient.ExtractSkipToken(nextLink.GetString()) ?? string.Empty
                : string.Empty;
        }
        while (!string.IsNullOrWhiteSpace(nextToken));

        return result;
    }

    private static async Task<bool> ApplyStageTransitionsAsync(
        HttpContext context,
        KeyVaultSecretClient client,
        string token,
        string name,
        IReadOnlyList<SecretVersionMetadata> existingVersions,
        SecretVersionMetadata newVersion,
        IReadOnlyList<string> requestedStages,
        bool versionStagesSpecified,
        CancellationToken cancellationToken)
    {
        var stagesToMove = new HashSet<string>(requestedStages, StringComparer.Ordinal);
        var defaultPut = !versionStagesSpecified
            && requestedStages.Count == 1
            && string.Equals(requestedStages[0], "AWSCURRENT", StringComparison.Ordinal);
        var previousCurrent = defaultPut ? FindCurrentVersion(existingVersions, newVersion.VersionId) : null;

        foreach (var version in existingVersions)
        {
            if (string.Equals(version.VersionId, newVersion.VersionId, StringComparison.Ordinal))
            {
                continue;
            }

            var updatedStages = new List<string>(version.VersionStages);
            var changed = RemoveAny(updatedStages, stagesToMove);

            if (defaultPut)
            {
                if (string.Equals(version.VersionId, previousCurrent?.VersionId, StringComparison.Ordinal))
                {
                    changed |= AddIfMissing(updatedStages, "AWSPREVIOUS");
                }
                else
                {
                    changed |= RemoveStage(updatedStages, "AWSPREVIOUS");
                }
            }

            if (changed && !await UpdateVersionStagesAsync(context, client, token, name, version, updatedStages, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }
        }

        if (!StagesEqual(newVersion.VersionStages, requestedStages)
            && !await UpdateVersionStagesAsync(context, client, token, name, newVersion, requestedStages, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        return true;
    }

    private static SecretVersionMetadata? FindCurrentVersion(IReadOnlyList<SecretVersionMetadata> versions, string newVersionId)
    {
        SecretVersionMetadata? tagged = null;
        SecretVersionMetadata? untagged = null;
        foreach (var version in versions)
        {
            if (string.Equals(version.VersionId, newVersionId, StringComparison.Ordinal))
            {
                continue;
            }

            if (ContainsStage(version.VersionStages, "AWSCURRENT"))
            {
                if (tagged is null || version.Created > tagged.Created)
                {
                    tagged = version;
                }
            }
            else if (!version.HasStoredStages && (untagged is null || version.Created > untagged.Created))
            {
                untagged = version;
            }
        }

        return tagged ?? untagged;
    }

    private static async Task<bool> UpdateVersionStagesAsync(
        HttpContext context,
        KeyVaultSecretClient client,
        string token,
        string name,
        SecretVersionMetadata version,
        IReadOnlyList<string> stages,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, client.BuildVaultUri(KeyVaultSecretClient.BuildSecretVersionPath(name, version.VersionId)));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(
            KeyVaultSecretClient.BuildTagsJsonBody(KeyVaultSecretClient.WithVersionStages(version.Tags, stages)),
            Encoding.UTF8,
            "application/json");

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            return true;
        }

        await SecretsManagerOperationSupport.WriteAwsErrorAsync(context, SecretsManagerOperationSupport.MapStatusCode(response.StatusCode), SecretsManagerOperationSupport.MapErrorCode(response.StatusCode), "Key Vault request failed.").ConfigureAwait(false);
        return false;
    }

    private static bool RemoveAny(List<string> stages, HashSet<string> labels)
    {
        var changed = false;
        for (var i = stages.Count - 1; i >= 0; i--)
        {
            if (labels.Contains(stages[i]))
            {
                stages.RemoveAt(i);
                changed = true;
            }
        }

        return changed;
    }

    private static bool RemoveStage(List<string> stages, string label)
    {
        var changed = false;
        for (var i = stages.Count - 1; i >= 0; i--)
        {
            if (string.Equals(stages[i], label, StringComparison.Ordinal))
            {
                stages.RemoveAt(i);
                changed = true;
            }
        }

        return changed;
    }

    private static bool AddIfMissing(List<string> stages, string label)
    {
        if (ContainsStage(stages, label))
        {
            return false;
        }

        stages.Add(label);
        return true;
    }

    private static bool ContainsStage(IReadOnlyList<string> stages, string label)
    {
        foreach (var stage in stages)
        {
            if (string.Equals(stage, label, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool StagesEqual(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var i = 0; i < left.Count; i++)
        {
            if (!string.Equals(left[i], right[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private sealed record SecretVersionMetadata(
        string VersionId,
        IReadOnlyDictionary<string, string> Tags,
        long Created,
        bool HasStoredStages)
    {
        public string[] VersionStages => Tags.TryGetValue(KeyVaultSecretClient.VersionStagesTag, out var stages)
            ? stages.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : HasStoredStages ? [] : ["AWSCURRENT"];
    }
}
