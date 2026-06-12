using System.Net;
using System.Text;
using System.Text.Json;
using Aws2Azure.Core.Azure;
using Aws2Azure.Core.Configuration;

namespace Aws2Azure.Modules.SecretsManager;

internal sealed class KeyVaultSecretClient
{
    private const string ApiVersion = "7.4";
    private static readonly string[] SupportedOperations = ["GetSecretValue", "CreateSecret", "UpdateSecret", "DeleteSecret", "ListSecrets", "DescribeSecret"];

    private readonly AzureHttpClient _http;
    private readonly EntraIdTokenProvider _tokenProvider;
    private readonly KeyVaultCredentials _credentials;

    public KeyVaultSecretClient(AzureHttpClient http, EntraIdTokenProvider tokenProvider, KeyVaultCredentials credentials)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _tokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
    }

    public static bool IsSupported(string operationName) => Array.Exists(SupportedOperations, item => string.Equals(item, operationName, StringComparison.OrdinalIgnoreCase));

    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        var auth = new AadAuthSettings(_credentials.AuthMode, _credentials.TenantId, _credentials.ClientId, _credentials.ClientSecret);
        return await _tokenProvider.GetTokenAsync(
            auth,
            "https://vault.azure.net/.default",
            cancellationToken).ConfigureAwait(false);
    }

    public string BuildVaultUri(string path)
    {
        var baseUri = _credentials.VaultUrl.TrimEnd('/');
        return baseUri + path + "?api-version=" + ApiVersion;
    }

    public static string NormalizeSecretName(string secretId)
    {
        if (string.IsNullOrWhiteSpace(secretId))
        {
            throw new ArgumentException("SecretId is required.", nameof(secretId));
        }

        if (secretId.StartsWith("arn:", StringComparison.OrdinalIgnoreCase))
        {
            var start = secretId.IndexOf(":secret:", StringComparison.OrdinalIgnoreCase);
            return start >= 0 ? secretId[(start + ":secret:".Length)..] : secretId;
        }

        return secretId.Trim('/');
    }

    public static string BuildArn(string name)
        => $"arn:aws:secretsmanager:azure:keyvault:secret:{name}";

    /// <summary>
    /// Extracts the secret name from a Key Vault resource id such as
    /// <c>https://vault.vault.azure.net/secrets/{name}</c> or
    /// <c>.../secrets/{name}/{version}</c>. Key Vault list items expose only an
    /// <c>id</c> URL (no <c>name</c> field), so the name must be parsed from it.
    /// </summary>
    public static string GetSecretNameFromId(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return string.Empty;
        }

        var parts = id.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (string.Equals(parts[i], "secrets", StringComparison.OrdinalIgnoreCase))
            {
                return parts[i + 1];
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// Extracts the Key Vault <c>$skiptoken</c> continuation value from a list
    /// response <c>nextLink</c> URL so it can be surfaced as an opaque AWS
    /// <c>NextToken</c>. Also tolerates a bare <c>skiptoken</c> spelling.
    /// </summary>
    public static string? ExtractSkipToken(string? nextLink)
    {
        if (string.IsNullOrWhiteSpace(nextLink))
        {
            return null;
        }

        const string marker = "skiptoken=";
        var idx = nextLink.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return null;
        }

        var start = idx + marker.Length;
        var end = nextLink.IndexOf('&', start);
        var raw = end < 0 ? nextLink[start..] : nextLink[start..end];
        return string.IsNullOrEmpty(raw) ? null : Uri.UnescapeDataString(raw);
    }

    public static string GetVersionId(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return Guid.NewGuid().ToString("N");
        }

        if (TryGetVersionId(id, out var versionId) && !string.IsNullOrWhiteSpace(versionId))
        {
            return versionId;
        }

        var parts = id.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 ? parts[^1] : Guid.NewGuid().ToString("N");
    }

    public static bool TryGetVersionId(string id, out string? versionId)
    {
        versionId = null;
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        var parts = id.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (string.Equals(parts[i], "versions", StringComparison.OrdinalIgnoreCase))
            {
                versionId = parts[i + 1];
                return !string.IsNullOrWhiteSpace(versionId);
            }
        }

        return false;
    }

    public static string? GetSecretValue(JsonDocument document, string fieldName)
    {
        if (document.RootElement.TryGetProperty(fieldName, out var property) && property.ValueKind == JsonValueKind.String)
        {
            return property.GetString();
        }

        return null;
    }

    public static byte[] DecodeSecretBinary(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        return Convert.FromBase64String(value);
    }

    public static string EncodeSecretBinary(byte[] bytes)
        => Convert.ToBase64String(bytes);

    public static bool IsBinarySecret(JsonElement root)
    {
        if (root.TryGetProperty("SecretBinary", out var secretBinary) && secretBinary.ValueKind == JsonValueKind.String)
        {
            return true;
        }

        if (root.TryGetProperty("SecretString", out var secretString) && secretString.ValueKind == JsonValueKind.String)
        {
            return false;
        }

        return false;
    }

    public static string GetDescription(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("description", out var lower) && lower.ValueKind == JsonValueKind.String)
        {
            return lower.GetString() ?? string.Empty;
        }

        return root.TryGetProperty("Description", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
    }

    public static DateTimeOffset GetCreatedDate(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("attributes", out var attributes)
            && attributes.ValueKind == JsonValueKind.Object
            && attributes.TryGetProperty("created", out var created)
            && created.ValueKind == JsonValueKind.Number)
        {
            return DateTimeOffset.FromUnixTimeSeconds(created.GetInt64());
        }

        return DateTimeOffset.UtcNow;
    }

    public static DateTimeOffset? GetLastChangedDate(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("attributes", out var attributes)
            && attributes.ValueKind == JsonValueKind.Object
            && attributes.TryGetProperty("updated", out var updated)
            && updated.ValueKind == JsonValueKind.Number)
        {
            return DateTimeOffset.FromUnixTimeSeconds(updated.GetInt64());
        }

        return null;
    }

    public static IReadOnlyDictionary<string, string> GetTags(JsonElement root)
    {
        var empty = new Dictionary<string, string>(StringComparer.Ordinal);
        if (root.ValueKind != JsonValueKind.Object)
        {
            return empty;
        }

        JsonElement tags;
        if (root.TryGetProperty("tags", out var lower) && (lower.ValueKind == JsonValueKind.Object || lower.ValueKind == JsonValueKind.Array))
        {
            tags = lower;
        }
        else if (root.TryGetProperty("Tags", out var upper) && (upper.ValueKind == JsonValueKind.Object || upper.ValueKind == JsonValueKind.Array))
        {
            tags = upper;
        }
        else
        {
            return empty;
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        // Key Vault exposes tags as a JSON map; AWS Secrets Manager sends them
        // as an array of { "Key": ..., "Value": ... } objects. Accept both.
        if (tags.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in tags.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object
                    || !entry.TryGetProperty("Key", out var key)
                    || key.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var keyName = key.GetString();
                if (string.IsNullOrEmpty(keyName))
                {
                    continue;
                }

                result[keyName] = entry.TryGetProperty("Value", out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString() ?? string.Empty
                    : string.Empty;
            }

            return result;
        }

        foreach (var property in tags.EnumerateObject())
        {
            result[property.Name] = property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString() ?? string.Empty
                : property.Value.ToString();
        }

        return result;
    }

    public static string BuildJsonBody(string? secretString, string? secretBinary, string? description, IReadOnlyDictionary<string, string>? tags)
    {
        var payload = new KeyVaultSecretRequest(
            Value: string.IsNullOrWhiteSpace(secretBinary) ? secretString : EncodeSecretBinary(DecodeSecretBinary(secretBinary)),
            ContentType: string.IsNullOrWhiteSpace(secretBinary) ? null : "application/octet-stream",
            Tags: tags is null || tags.Count == 0 ? null : tags,
            Attributes: new KeyVaultSecretAttributes(true, null, null, DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
            Description: string.IsNullOrWhiteSpace(description) ? null : description);

        return JsonSerializer.Serialize(payload, SecretsManagerJsonContext.Default.KeyVaultSecretRequest);
    }

    public static string RemoveVersionSuffix(string id)
    {
        var slash = id.LastIndexOf('/');
        return slash >= 0 ? id[..slash] : id;
    }

    public static string? ExtractVersion(string uri)
    {
        var parts = uri.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 1 ? parts[^1] : null;
    }

    public static string BuildSecretPath(string name) => "/secrets/" + Uri.EscapeDataString(name);

    public static string BuildSecretVersionPath(string name, string versionId) => "/secrets/" + Uri.EscapeDataString(name) + "/" + Uri.EscapeDataString(versionId);
}
