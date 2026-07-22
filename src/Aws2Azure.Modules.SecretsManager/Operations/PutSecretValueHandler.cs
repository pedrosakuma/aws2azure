using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.SecretsManager.Operations;

internal static class PutSecretValueHandler
{
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

        await using var secretLock = await SecretVersionCoordinator.AcquireLockAsync(name, cancellationToken).ConfigureAwait(false);
        var written = await CreateVersionAsync(
            context,
            client,
            token,
            name,
            secretString,
            secretBinary,
            description: null,
            clientRequestToken,
            payloadSha256,
            versionStages,
            versionStagesSpecified,
            cancellationToken).ConfigureAwait(false);
        if (written is null)
        {
            return;
        }

        var payload = new PutSecretValueResponse(
            Arn: KeyVaultSecretClient.BuildArn(name),
            Name: name,
            VersionId: string.IsNullOrWhiteSpace(clientRequestToken) ? written.Value.VersionId : clientRequestToken,
            VersionStages: written.Value.VersionStages);
        await SecretsManagerOperationSupport.WriteJsonAsync(context, payload, SecretsManagerJsonContext.Default.PutSecretValueResponse, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<NewVersionResult?> CreateVersionAsync(
        HttpContext context,
        KeyVaultSecretClient client,
        string token,
        string name,
        string? secretString,
        string? secretBinary,
        string? description,
        string? clientRequestToken,
        string payloadSha256,
        IReadOnlyList<string> versionStages,
        bool versionStagesSpecified,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(clientRequestToken))
        {
            var existing = await SecretVersionCoordinator.ListVersionsAsync(context, client, token, name, cancellationToken).ConfigureAwait(false);
            if (existing is null)
            {
                return null;
            }

            var tokenResolution = SecretVersionCoordinator.ResolveToken(existing, clientRequestToken, payloadSha256);
            if (tokenResolution.Conflict)
            {
                await SecretVersionCoordinator.WriteConflictAsync(context, "ClientRequestToken is already associated with a different secret value.").ConfigureAwait(false);
                return null;
            }

            if (tokenResolution.Version is not null)
            {
                var replayed = await SecretVersionCoordinator.PublishVersionAsync(
                    context,
                    client,
                    token,
                    name,
                    tokenResolution.Version.VersionId,
                    clientRequestToken,
                    payloadSha256,
                    versionStages,
                    defaultStageTransition: !versionStagesSpecified,
                    cancellationToken).ConfigureAwait(false);
                return replayed is null
                    ? null
                    : new NewVersionResult(
                        replayed.Value.VersionId,
                        DateTimeOffset.FromUnixTimeSeconds(tokenResolution.Version.Created),
                        replayed.Value.VersionStages);
            }
        }

        var internalTags = KeyVaultSecretClient.BuildInternalTags(
            clientRequestToken,
            payloadSha256,
            versionStages,
            defaultStageTransition: !versionStagesSpecified);
        using var request = new HttpRequestMessage(HttpMethod.Put, client.BuildVaultUri(KeyVaultSecretClient.BuildSecretPath(name)));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(
            KeyVaultSecretClient.BuildJsonBody(secretString, secretBinary, description, internalTags),
            Encoding.UTF8,
            "application/json");
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            await SecretsManagerOperationSupport.WriteAwsErrorAsync(context, SecretsManagerOperationSupport.MapStatusCode(response.StatusCode), SecretsManagerOperationSupport.MapErrorCode(response.StatusCode), "Key Vault request failed.").ConfigureAwait(false);
            return null;
        }

        using var secretDocument = await SecretsManagerOperationSupport.ReadJsonDocumentAsync(response.Content, cancellationToken).ConfigureAwait(false);
        var id = secretDocument.RootElement.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String
            ? idElement.GetString() ?? string.Empty
            : string.Empty;
        var newVersionId = KeyVaultSecretClient.GetVersionId(id);
        var createdDate = KeyVaultSecretClient.GetCreatedDate(secretDocument.RootElement);
        var published = await SecretVersionCoordinator.PublishVersionAsync(
            context,
            client,
            token,
            name,
            newVersionId,
            clientRequestToken,
            payloadSha256,
            versionStages,
            defaultStageTransition: !versionStagesSpecified,
            cancellationToken).ConfigureAwait(false);
        return published is null
            ? null
            : new NewVersionResult(published.Value.VersionId, createdDate, published.Value.VersionStages);
    }

    internal readonly record struct NewVersionResult(
        string VersionId,
        DateTimeOffset CreatedDate,
        IReadOnlyList<string> VersionStages);
}
