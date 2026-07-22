using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aws2Azure.Core.Azure;
using Aws2Azure.Core.Configuration;

namespace Aws2Azure.Modules.SecretsManager;

internal sealed class KeyVaultSecretClient
{
    private const string ApiVersion = "7.4";
    private const string InternalTagPrefix = "aws2azure-";
    internal const string ClientRequestTokenTag = InternalTagPrefix + "client-request-token";
    internal const string PayloadSha256Tag = InternalTagPrefix + "payload-sha256";
    internal const string VersionStagesTag = InternalTagPrefix + "version-stages";
    internal const string IntendedVersionStagesTag = InternalTagPrefix + "intended-version-stages";
    internal const string DefaultStageTransitionTag = InternalTagPrefix + "default-stage-transition";

    private readonly AzureHttpClient _http;
    private readonly EntraIdTokenProvider _tokenProvider;
    private readonly KeyVaultCredentials _credentials;

    public KeyVaultSecretClient(AzureHttpClient http, EntraIdTokenProvider tokenProvider, KeyVaultCredentials credentials)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _tokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
    }

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
                if (string.IsNullOrEmpty(keyName) || IsInternalTag(keyName))
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
            if (IsInternalTag(property.Name))
            {
                continue;
            }

            result[property.Name] = property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString() ?? string.Empty
                : property.Value.ToString();
        }

        return result;
    }

    public static IReadOnlyDictionary<string, string> GetRawTags(JsonElement root)
    {
        var empty = new Dictionary<string, string>(StringComparer.Ordinal);
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("tags", out var tags)
            || tags.ValueKind != JsonValueKind.Object)
        {
            return empty;
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in tags.EnumerateObject())
        {
            result[property.Name] = property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString() ?? string.Empty
                : property.Value.ToString();
        }

        return result;
    }

    public static string[] ReadVersionStages(JsonDocument document)
    {
        if (!document.RootElement.TryGetProperty("VersionStages", out var stagesElement))
        {
            return ["AWSCURRENT"];
        }

        if (stagesElement.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException("VersionStages must be an array.");
        }

        var stages = new List<string>();
        foreach (var item in stagesElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                throw new ArgumentException("VersionStages must contain only strings.");
            }

            var stage = item.GetString();
            if (string.IsNullOrWhiteSpace(stage))
            {
                throw new ArgumentException("VersionStages must not contain empty labels.");
            }

            if (stage.IndexOf('\n') >= 0 || stage.IndexOf('\r') >= 0)
            {
                throw new ArgumentException("VersionStages labels must not contain newline characters.");
            }

            stages.Add(stage);
        }

        if (stages.Count == 0)
        {
            throw new ArgumentException("VersionStages must contain at least one label.");
        }

        return stages.ToArray();
    }

    public static IReadOnlyDictionary<string, string> BuildInternalTags(
        string? clientRequestToken,
        string payloadSha256,
        IReadOnlyList<string> intendedVersionStages,
        bool defaultStageTransition)
    {
        var tags = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [PayloadSha256Tag] = payloadSha256,
            [VersionStagesTag] = "\n",
            [IntendedVersionStagesTag] = EncodeVersionStages(intendedVersionStages),
            [DefaultStageTransitionTag] = defaultStageTransition ? "true" : "false",
        };

        if (!string.IsNullOrWhiteSpace(clientRequestToken))
        {
            tags[ClientRequestTokenTag] = clientRequestToken;
        }

        return tags;
    }

    public static IReadOnlyDictionary<string, string> WithVersionStages(IReadOnlyDictionary<string, string>? tags, IReadOnlyList<string> versionStages)
    {
        var result = tags is null || tags.Count == 0
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(tags, StringComparer.Ordinal);
        result[VersionStagesTag] = versionStages.Count == 0 ? "\n" : EncodeVersionStages(versionStages);

        return result;
    }

    public static string[] DecodeStoredVersionStages(string value)
        => DecodeVersionStages(value);

    public static bool TryGetRawTag(JsonElement root, string name, out string value)
    {
        value = string.Empty;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("tags", out var tags)
            || tags.ValueKind != JsonValueKind.Object
            || !tags.TryGetProperty(name, out var tag)
            || tag.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = tag.GetString() ?? string.Empty;
        return !string.IsNullOrEmpty(value);
    }

    public static string GetPayloadSha256(string? value, string? contentType)
    {
        var material = (contentType ?? string.Empty) + "\n" + (value ?? string.Empty);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static string BuildJsonBody(string? secretString, string? secretBinary, string? description, IReadOnlyDictionary<string, string>? tags)
    {
        var payload = new KeyVaultSecretRequest(
            Value: string.IsNullOrWhiteSpace(secretBinary) ? secretString : EncodeSecretBinary(DecodeSecretBinary(secretBinary)),
            ContentType: string.IsNullOrWhiteSpace(secretBinary) ? null : "application/octet-stream",
            Tags: tags is null || tags.Count == 0 ? null : tags,
            Attributes: new KeyVaultSecretAttributes(true, null, null, null),
            Description: string.IsNullOrWhiteSpace(description) ? null : description);

        return JsonSerializer.Serialize(payload, SecretsManagerJsonContext.Default.KeyVaultSecretRequest);
    }

    public static string BuildTagsJsonBody(IReadOnlyDictionary<string, string>? tags)
    {
        var payload = new KeyVaultSecretTagsRequest(tags);
        return JsonSerializer.Serialize(payload, SecretsManagerJsonContext.Default.KeyVaultSecretTagsRequest);
    }

    public static string BuildSecretPath(string name) => "/secrets/" + Uri.EscapeDataString(name);

    public static string BuildSecretVersionPath(string name, string versionId) => "/secrets/" + Uri.EscapeDataString(name) + "/" + Uri.EscapeDataString(versionId);

    public static string BuildSecretVersionsPath(string name) => "/secrets/" + Uri.EscapeDataString(name) + "/versions";

    private static bool IsInternalTag(string name)
        => name.StartsWith(InternalTagPrefix, StringComparison.Ordinal);

    private static string EncodeVersionStages(IReadOnlyList<string> stages)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < stages.Count; i++)
        {
            if (i > 0)
            {
                builder.Append('\n');
            }

            builder.Append(stages[i]);
        }

        return builder.ToString();
    }

    private static string[] DecodeVersionStages(string value)
        => value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
