using System.Collections.Generic;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.SecretsManager.Operations;

internal static class CreateSecretHandler
{
    public static async Task HandleAsync(HttpContext context, KeyVaultSecretClient client, JsonDocument document, CancellationToken cancellationToken)
    {
        var name = KeyVaultSecretClient.NormalizeSecretName(SecretsManagerOperationSupport.ReadString(document, "Name") ?? string.Empty);
        var secretString = SecretsManagerOperationSupport.ReadString(document, "SecretString");
        var secretBinary = SecretsManagerOperationSupport.ReadString(document, "SecretBinary");
        SecretsManagerOperationSupport.ValidateExactlyOneSecretValue(secretString, secretBinary);
        var description = SecretsManagerOperationSupport.ReadString(document, "Description");
        var clientRequestToken = SecretsManagerOperationSupport.ReadString(document, "ClientRequestToken");
        var contentType = string.IsNullOrEmpty(secretBinary) ? null : "application/octet-stream";
        var storedValue = string.IsNullOrEmpty(secretBinary)
            ? secretString
            : KeyVaultSecretClient.EncodeSecretBinary(KeyVaultSecretClient.DecodeSecretBinary(secretBinary));
        var payloadSha256 = KeyVaultSecretClient.GetPayloadSha256(storedValue, contentType);
        var token = await client.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);

        var exists = await SecretsManagerOperationSupport.SecretExistsAsync(context, client, token, name, cancellationToken).ConfigureAwait(false);
        if (exists is null)
        {
            return;
        }

        if (exists.Value)
        {
            if (!string.IsNullOrWhiteSpace(clientRequestToken))
            {
                var replayPayload = await TryReplayExistingVersionAsync(
                    context,
                    client,
                    token,
                    name,
                    clientRequestToken,
                    payloadSha256,
                    cancellationToken).ConfigureAwait(false);
                if (replayPayload is not null)
                {
                    await SecretsManagerOperationSupport.WriteJsonAsync(context, replayPayload, SecretsManagerJsonContext.Default.CreateSecretResponse, cancellationToken).ConfigureAwait(false);
                    return;
                }
            }

            await SecretsManagerOperationSupport.WriteAwsErrorAsync(context, StatusCodes.Status400BadRequest, "ResourceExistsException", $"Secret '{name}' already exists.").ConfigureAwait(false);
            return;
        }

        var tags = new Dictionary<string, string>(KeyVaultSecretClient.GetTags(document.RootElement), StringComparer.Ordinal)
        {
            [KeyVaultSecretClient.PayloadSha256Tag] = payloadSha256,
            [KeyVaultSecretClient.VersionStagesTag] = "AWSCURRENT",
            [KeyVaultSecretClient.PublicationStateTag] = "published",
        };
        if (!string.IsNullOrWhiteSpace(clientRequestToken))
        {
            tags[KeyVaultSecretClient.ClientRequestTokenTag] = clientRequestToken;
        }

        using var request = new HttpRequestMessage(HttpMethod.Put, client.BuildVaultUri(KeyVaultSecretClient.BuildSecretPath(name)));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(KeyVaultSecretClient.BuildJsonBody(
            secretString,
            secretBinary,
            description,
            tags), Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == System.Net.HttpStatusCode.Conflict && !string.IsNullOrWhiteSpace(clientRequestToken))
            {
                var replayPayload = await TryReplayExistingVersionAsync(
                    context,
                    client,
                    token,
                    name,
                    clientRequestToken,
                    payloadSha256,
                    cancellationToken).ConfigureAwait(false);
                if (replayPayload is not null)
                {
                    await SecretsManagerOperationSupport.WriteJsonAsync(context, replayPayload, SecretsManagerJsonContext.Default.CreateSecretResponse, cancellationToken).ConfigureAwait(false);
                    return;
                }
            }

            await SecretsManagerOperationSupport.WriteAwsErrorAsync(context, SecretsManagerOperationSupport.MapStatusCode(response.StatusCode), SecretsManagerOperationSupport.MapErrorCode(response.StatusCode), "Key Vault request failed.").ConfigureAwait(false);
            return;
        }

        using var secretDocument = await SecretsManagerOperationSupport.ReadJsonDocumentAsync(response.Content, cancellationToken).ConfigureAwait(false);
        var id = secretDocument.RootElement.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String
            ? idElement.GetString() ?? string.Empty
            : string.Empty;
        var createdDate = KeyVaultSecretClient.GetCreatedDate(secretDocument.RootElement);

        var payload = new CreateSecretResponse(
            Arn: KeyVaultSecretClient.BuildArn(name),
            Name: name,
            VersionId: string.IsNullOrWhiteSpace(clientRequestToken) ? KeyVaultSecretClient.GetVersionId(id) : clientRequestToken,
            VersionStages: ["AWSCURRENT"],
            CreatedDate: createdDate);

        await SecretsManagerOperationSupport.WriteJsonAsync(context, payload, SecretsManagerJsonContext.Default.CreateSecretResponse, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<CreateSecretResponse?> TryReplayExistingVersionAsync(
        HttpContext context,
        KeyVaultSecretClient client,
        string token,
        string name,
        string clientRequestToken,
        string payloadSha256,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var versions = await SecretVersionCoordinator.ListVersionsAsync(context, client, token, name, cancellationToken).ConfigureAwait(false);
            if (versions is null)
            {
                return null;
            }

            var tokenResolution = SecretVersionCoordinator.ResolveToken(versions, clientRequestToken, payloadSha256);
            if (tokenResolution.Conflict)
            {
                await SecretVersionCoordinator.WriteConflictAsync(context, "ClientRequestToken is already associated with a different secret value.").ConfigureAwait(false);
                return null;
            }

            if (tokenResolution.Version is not null)
            {
                return new CreateSecretResponse(
                    Arn: KeyVaultSecretClient.BuildArn(name),
                    Name: name,
                    VersionId: clientRequestToken,
                    VersionStages: tokenResolution.Version.VersionStages,
                    CreatedDate: DateTimeOffset.FromUnixTimeSeconds(tokenResolution.Version.Created));
            }

            if (attempt < 7)
            {
                var delay = Math.Min(50 << Math.Min(attempt, 4), 1_000);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }

        return null;
    }
}
