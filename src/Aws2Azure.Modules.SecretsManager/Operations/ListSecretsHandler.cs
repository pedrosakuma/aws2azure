using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.SecretsManager.Operations;

internal static class ListSecretsHandler
{
    public static async Task HandleAsync(HttpContext context, KeyVaultSecretClient client, JsonDocument document, CancellationToken cancellationToken)
    {
        var token = await client.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);

        var skipToken = SecretsManagerOperationSupport.ReadString(document, "NextToken");
        var maxResults = SecretsManagerOperationSupport.ReadInt(document, "MaxResults");
        var requestUri = client.BuildVaultUri("/secrets");
        if (maxResults is > 0)
        {
            requestUri += "&maxresults=" + maxResults.Value.ToString(CultureInfo.InvariantCulture);
        }

        if (!string.IsNullOrWhiteSpace(skipToken))
        {
            requestUri += "&$skiptoken=" + Uri.EscapeDataString(skipToken);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            await SecretsManagerOperationSupport.WriteAwsErrorAsync(context, SecretsManagerOperationSupport.MapStatusCode(response.StatusCode), SecretsManagerOperationSupport.MapErrorCode(response.StatusCode), "Key Vault request failed.").ConfigureAwait(false);
            return;
        }

        using var secretDocument = await SecretsManagerOperationSupport.ReadJsonDocumentAsync(response.Content, cancellationToken).ConfigureAwait(false);

        var items = new List<ListSecretsItem>();
        var nextToken = secretDocument.RootElement.TryGetProperty("nextLink", out var nextLink) && nextLink.ValueKind == JsonValueKind.String
            ? KeyVaultSecretClient.ExtractSkipToken(nextLink.GetString())
            : null;
        if (secretDocument.RootElement.TryGetProperty("value", out var valueElement) && valueElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in valueElement.EnumerateArray())
            {
                var id = item.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String ? idElement.GetString() ?? string.Empty : string.Empty;
                var name = item.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
                    ? nameElement.GetString() ?? string.Empty
                    : KeyVaultSecretClient.GetSecretNameFromId(id);
                var description = KeyVaultSecretClient.GetDescription(item);
                var tags = KeyVaultSecretClient.GetTags(item);
                var versionIdsToStages = KeyVaultSecretClient.TryGetVersionId(id, out var versionId) && !string.IsNullOrWhiteSpace(versionId)
                    ? new Dictionary<string, IReadOnlyList<string>>
                    {
                        [versionId!] = ["AWSCURRENT"],
                    }
                    : null;
                var createdDate = KeyVaultSecretClient.GetCreatedDate(item);
                var lastChangedDate = KeyVaultSecretClient.GetLastChangedDate(item);

                items.Add(new ListSecretsItem(
                    Arn: KeyVaultSecretClient.BuildArn(name),
                    Name: name,
                    Description: description,
                    CreatedDate: createdDate,
                    LastChangedDate: lastChangedDate,
                    Tags: SecretsManagerOperationSupport.ToTagArray(tags),
                    VersionIdsToStages: versionIdsToStages));
            }
        }

        await SecretsManagerOperationSupport.WriteJsonAsync(context, new ListSecretsResponse(items, nextToken), SecretsManagerJsonContext.Default.ListSecretsResponse, cancellationToken).ConfigureAwait(false);
    }
}
