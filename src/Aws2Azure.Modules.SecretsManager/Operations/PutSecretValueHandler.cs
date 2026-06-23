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
            await WriteNewVersionAsync(context, client, token, name, secretString, secretBinary, clientRequestToken, payloadSha256, versionStages, cancellationToken).ConfigureAwait(false);
            return;
        }

        var idempotencyLock = IdempotencyLocks.GetOrAdd(name, static _ => new SemaphoreSlim(1, 1));
        await idempotencyLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await TryWriteIdempotentResponseAsync(context, client, token, name, clientRequestToken, payloadSha256, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            await WriteNewVersionAsync(context, client, token, name, secretString, secretBinary, clientRequestToken, payloadSha256, versionStages, cancellationToken).ConfigureAwait(false);
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
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, client.BuildVaultUri(KeyVaultSecretClient.BuildSecretPath(name)));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(KeyVaultSecretClient.BuildJsonBody(
            secretString,
            secretBinary,
            null,
            KeyVaultSecretClient.BuildInternalTags(clientRequestToken, payloadSha256, versionStages)), Encoding.UTF8, "application/json");

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

        var payload = new PutSecretValueResponse(
            Arn: KeyVaultSecretClient.BuildArn(name),
            Name: name,
            VersionId: string.IsNullOrWhiteSpace(clientRequestToken) ? KeyVaultSecretClient.GetVersionId(id) : clientRequestToken,
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
        CancellationToken cancellationToken)
    {
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
                return true;
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

                    if (!KeyVaultSecretClient.TryGetRawTag(version, KeyVaultSecretClient.PayloadSha256Tag, out var candidateSha256)
                        || !string.Equals(candidateSha256, payloadSha256, StringComparison.Ordinal))
                    {
                        await SecretsManagerOperationSupport.WriteAwsErrorAsync(context, StatusCodes.Status409Conflict, "ResourceExistsException", "ClientRequestToken is already associated with a different secret value.").ConfigureAwait(false);
                        return true;
                    }

                    var id = version.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String
                        ? idElement.GetString() ?? string.Empty
                        : string.Empty;
                    var payload = new PutSecretValueResponse(
                        Arn: KeyVaultSecretClient.BuildArn(name),
                        Name: name,
                        VersionId: candidateToken,
                        VersionStages: KeyVaultSecretClient.GetVersionStages(version));
                    await SecretsManagerOperationSupport.WriteJsonAsync(context, payload, SecretsManagerJsonContext.Default.PutSecretValueResponse, cancellationToken).ConfigureAwait(false);
                    return true;
                }
            }

            nextToken = versionsDocument.RootElement.TryGetProperty("nextLink", out var nextLink) && nextLink.ValueKind == JsonValueKind.String
                ? KeyVaultSecretClient.ExtractSkipToken(nextLink.GetString()) ?? string.Empty
                : string.Empty;
        }
        while (!string.IsNullOrWhiteSpace(nextToken));

        return false;
    }
}
