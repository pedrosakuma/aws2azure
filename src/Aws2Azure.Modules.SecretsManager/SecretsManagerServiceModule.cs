using System.Net;
using System.Collections.Frozen;
using System.Linq;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Aws2Azure.Core;
using Aws2Azure.Core.Azure;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Core.Modules;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.SecretsManager;

/// <summary>
/// Secrets Manager → Key Vault module. Routes the core Secrets Manager
/// operations to Key Vault using AAD client credentials and emits AWS-shaped
/// JSON responses so SDK callers can use the same wire contract.
/// </summary>
public sealed class SecretsManagerServiceModule : IServiceModule
{
    private readonly AzureHttpClient _http;
    private readonly ICredentialResolver _credentials;
    private readonly EntraIdTokenProvider _tokenProvider;

    public SecretsManagerServiceModule(
        AzureHttpClient http,
        ICredentialResolver credentials,
        CapabilityMatrix capabilities,
        EntraIdTokenProvider? tokenProvider = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(capabilities);
        _http = http;
        _credentials = credentials;
        _tokenProvider = tokenProvider ?? new EntraIdTokenProvider(http);
        Capabilities = capabilities;
    }

    public string ServiceName => "secretsmanager";
    public bool RequiresSigV4 => true;
    public IReadOnlyList<string> RequiredSignedHeaders { get; } = ["x-amz-target"];
    public AwsErrorFormat ErrorFormat => AwsErrorFormat.Json;
    public IReadOnlySet<string> KnownOperations => _knownOperations;
    private static readonly FrozenSet<string> _knownOperations = new[]
    {
        "GetSecretValue", "CreateSecret", "DeleteSecret",
        "DescribeSecret", "ListSecrets", "UpdateSecret",
    }.ToFrozenSet();
    public CapabilityMatrix Capabilities { get; }

    public bool MatchesHost(string host)
    {
        if (string.IsNullOrEmpty(host))
        {
            return false;
        }

        return host.StartsWith("secretsmanager.", StringComparison.OrdinalIgnoreCase)
            || host.StartsWith("secretsmanager-", StringComparison.OrdinalIgnoreCase)
            || host.Equals("secretsmanager", StringComparison.OrdinalIgnoreCase);
    }

    public async ValueTask HandleAsync(HttpContext context)
    {
        var target = context.Request.Headers["X-Amz-Target"].ToString();
        var operation = string.IsNullOrWhiteSpace(target)
            ? context.Request.Path.Value ?? "SecretsManager"
            : target.Split('.').LastOrDefault() ?? target;

        if (!KeyVaultSecretClient.IsSupported(operation))
        {
            await WriteAwsErrorAsync(context, StatusCodes.Status501NotImplemented, "NotImplementedException", $"Secrets Manager operation '{operation}' is not implemented yet.").ConfigureAwait(false);
            return;
        }

        var accessKeyId = context.Items["aws2azure.accessKeyId"] as string;
        if (string.IsNullOrWhiteSpace(accessKeyId))
        {
            await WriteAwsErrorAsync(context, StatusCodes.Status403Forbidden, "MissingAuthenticationTokenException", "Request is missing AWS credentials.").ConfigureAwait(false);
            return;
        }

        if (_credentials.GetAzureCredentialsFor(accessKeyId, AzureService.KeyVault) is not KeyVaultCredentials keyVault)
        {
            await WriteAwsErrorAsync(context, StatusCodes.Status403Forbidden, "AccessDeniedException", "No Key Vault credentials configured for the supplied AWS access key.").ConfigureAwait(false);
            return;
        }

        var client = new KeyVaultSecretClient(_http, _tokenProvider, keyVault);
        try
        {
            using var reader = new StreamReader(context.Request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            var body = await reader.ReadToEndAsync(context.RequestAborted).ConfigureAwait(false);
            using var document = string.IsNullOrWhiteSpace(body) ? JsonDocument.Parse("{}") : JsonDocument.Parse(body);

            switch (operation)
            {
                case "GetSecretValue":
                    await HandleGetSecretValueAsync(context, client, document, context.RequestAborted).ConfigureAwait(false);
                    return;
                case "CreateSecret":
                    await HandleCreateSecretAsync(context, client, document, context.RequestAborted).ConfigureAwait(false);
                    return;
                case "UpdateSecret":
                    await HandleUpdateSecretAsync(context, client, document, context.RequestAborted).ConfigureAwait(false);
                    return;
                case "DeleteSecret":
                    await HandleDeleteSecretAsync(context, client, document, context.RequestAborted).ConfigureAwait(false);
                    return;
                case "ListSecrets":
                    await HandleListSecretsAsync(context, client, document, context.RequestAborted).ConfigureAwait(false);
                    return;
                case "DescribeSecret":
                    await HandleDescribeSecretAsync(context, client, document, context.RequestAborted).ConfigureAwait(false);
                    return;
                default:
                    // Defensive: the IsSupported gate and this dispatch table are both
                    // case-sensitive (ordinal), so they cannot drift, but never let an
                    // unmatched operation fall through and leave the response unwritten.
                    await WriteAwsErrorAsync(context, StatusCodes.Status501NotImplemented, "NotImplementedException", $"Secrets Manager operation '{operation}' is not implemented yet.").ConfigureAwait(false);
                    return;
            }
        }
        catch (EntraIdTokenException ex)
        {
            await WriteAwsErrorAsync(context, (int)ex.BackendStatus, ex.StatusCode switch
            {
                HttpStatusCode.TooManyRequests => "ThrottlingException",
                _ when ex.BackendStatus == HttpStatusCode.ServiceUnavailable => "InternalServiceError",
                _ => "AccessDeniedException",
            }, ex.Message).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            await WriteAwsErrorAsync(context, StatusCodes.Status503ServiceUnavailable, "InternalServiceError", ex.Message).ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            await WriteAwsErrorAsync(context, StatusCodes.Status400BadRequest, "InvalidParameterException", ex.Message).ConfigureAwait(false);
        }
        catch (FormatException ex)
        {
            await WriteAwsErrorAsync(context, StatusCodes.Status400BadRequest, "InvalidParameterException", ex.Message).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            await WriteAwsErrorAsync(context, StatusCodes.Status400BadRequest, "InvalidParameterException", ex.Message).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            await WriteAwsErrorAsync(context, StatusCodes.Status400BadRequest, "InvalidParameterException", ex.Message).ConfigureAwait(false);
        }
    }

    private static async Task WriteAwsErrorAsync(HttpContext context, int statusCode, string code, string message)
    {
        await AwsErrorResponse.WriteAsync(context, AwsErrorFormat.Json, statusCode, code, message, resource: null, jsonContentType: "application/x-amz-json-1.1").ConfigureAwait(false);
    }

    private static async Task WriteJsonAsync<T>(HttpContext context, T payload, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/x-amz-json-1.1";
        await context.Response.WriteAsync(JsonSerializer.Serialize(payload, typeInfo)).ConfigureAwait(false);
    }

    private static string? ReadString(JsonDocument document, string propertyName)
        => document.RootElement.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static int? ReadInt(JsonDocument document, string propertyName)
        => document.RootElement.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt32(out var value)
            ? value
            : null;

    private async Task HandleGetSecretValueAsync(HttpContext context, KeyVaultSecretClient client, JsonDocument document, CancellationToken cancellationToken)
    {
        var secretId = KeyVaultSecretClient.NormalizeSecretName(ReadString(document, "SecretId") ?? string.Empty);
        var versionId = ReadString(document, "VersionId");
        var token = await client.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        var path = versionId is null
            ? KeyVaultSecretClient.BuildSecretPath(secretId)
            : KeyVaultSecretClient.BuildSecretVersionPath(secretId, versionId);
        using var request = new HttpRequestMessage(HttpMethod.Get, client.BuildVaultUri(path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            await WriteAwsErrorAsync(context, MapStatusCode(response.StatusCode), MapErrorCode(response.StatusCode), "Key Vault request failed.").ConfigureAwait(false);
            return;
        }

        using var secretDocument = JsonDocument.Parse(body);
        var value = secretDocument.RootElement.TryGetProperty("value", out var valueElement) && valueElement.ValueKind == JsonValueKind.String
            ? valueElement.GetString() ?? string.Empty
            : string.Empty;
        var id = secretDocument.RootElement.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String
            ? idElement.GetString() ?? string.Empty
            : string.Empty;
        var contentType = secretDocument.RootElement.TryGetProperty("contentType", out var contentTypeElement) && contentTypeElement.ValueKind == JsonValueKind.String
            ? contentTypeElement.GetString()
            : null;
        var createdDate = KeyVaultSecretClient.GetCreatedDate(secretDocument.RootElement);

        var secretBinary = string.Equals(contentType, "application/octet-stream", StringComparison.OrdinalIgnoreCase)
            ? KeyVaultSecretClient.EncodeSecretBinary(KeyVaultSecretClient.DecodeSecretBinary(value))
            : null;

        var secretString = string.Equals(contentType, "application/octet-stream", StringComparison.OrdinalIgnoreCase)
            ? null
            : value;

        var payload = new GetSecretValueResponse(
            Arn: KeyVaultSecretClient.BuildArn(secretId),
            Name: secretId,
            VersionId: KeyVaultSecretClient.GetVersionId(id),
            SecretString: secretString,
            SecretBinary: secretBinary,
            VersionStages: ["AWSCURRENT"],
            CreatedDate: createdDate);

        await WriteJsonAsync(context, payload, SecretsManagerJsonContext.Default.GetSecretValueResponse).ConfigureAwait(false);
    }

    private async Task HandleCreateSecretAsync(HttpContext context, KeyVaultSecretClient client, JsonDocument document, CancellationToken cancellationToken)
    {
        var name = KeyVaultSecretClient.NormalizeSecretName(ReadString(document, "Name") ?? string.Empty);
        var token = await client.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);

        var exists = await SecretExistsAsync(context, client, token, name, cancellationToken).ConfigureAwait(false);
        if (exists is null)
        {
            return;
        }

        if (exists.Value)
        {
            await WriteAwsErrorAsync(context, StatusCodes.Status409Conflict, "ResourceExistsException", $"Secret '{name}' already exists.").ConfigureAwait(false);
            return;
        }

        using var request = new HttpRequestMessage(HttpMethod.Put, client.BuildVaultUri(KeyVaultSecretClient.BuildSecretPath(name)));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(KeyVaultSecretClient.BuildJsonBody(
            ReadString(document, "SecretString"),
            ReadString(document, "SecretBinary"),
            ReadString(document, "Description"),
            KeyVaultSecretClient.GetTags(document.RootElement)), Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            await WriteAwsErrorAsync(context, MapStatusCode(response.StatusCode), MapErrorCode(response.StatusCode), "Key Vault request failed.").ConfigureAwait(false);
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var secretDocument = JsonDocument.Parse(body);
        var id = secretDocument.RootElement.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String
            ? idElement.GetString() ?? string.Empty
            : string.Empty;
        var createdDate = KeyVaultSecretClient.GetCreatedDate(secretDocument.RootElement);

        var payload = new CreateSecretResponse(
            Arn: KeyVaultSecretClient.BuildArn(name),
            Name: name,
            VersionId: KeyVaultSecretClient.GetVersionId(id),
            VersionStages: ["AWSCURRENT"],
            CreatedDate: createdDate);

        await WriteJsonAsync(context, payload, SecretsManagerJsonContext.Default.CreateSecretResponse).ConfigureAwait(false);
    }

    private async Task HandleUpdateSecretAsync(HttpContext context, KeyVaultSecretClient client, JsonDocument document, CancellationToken cancellationToken)
    {
        var name = KeyVaultSecretClient.NormalizeSecretName(ReadString(document, "SecretId") ?? ReadString(document, "Name") ?? string.Empty);
        var token = await client.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);

        // Key Vault PUT /secrets/{name} is an upsert, but AWS UpdateSecret must
        // fail with ResourceNotFoundException when the secret does not exist.
        var exists = await SecretExistsAsync(context, client, token, name, cancellationToken).ConfigureAwait(false);
        if (exists is null)
        {
            return;
        }

        if (!exists.Value)
        {
            await WriteAwsErrorAsync(context, StatusCodes.Status404NotFound, "ResourceNotFoundException", $"Secrets Manager can't find the specified secret '{name}'.").ConfigureAwait(false);
            return;
        }

        using var request = new HttpRequestMessage(HttpMethod.Put, client.BuildVaultUri(KeyVaultSecretClient.BuildSecretPath(name)));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(KeyVaultSecretClient.BuildJsonBody(
            ReadString(document, "SecretString"),
            ReadString(document, "SecretBinary"),
            ReadString(document, "Description"),
            KeyVaultSecretClient.GetTags(document.RootElement)), Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            await WriteAwsErrorAsync(context, MapStatusCode(response.StatusCode), MapErrorCode(response.StatusCode), "Key Vault request failed.").ConfigureAwait(false);
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var secretDocument = JsonDocument.Parse(body);
        var id = secretDocument.RootElement.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String
            ? idElement.GetString() ?? string.Empty
            : string.Empty;
        var createdDate = KeyVaultSecretClient.GetCreatedDate(secretDocument.RootElement);

        var payload = new UpdateSecretResponse(
            Arn: KeyVaultSecretClient.BuildArn(name),
            Name: name,
            VersionId: KeyVaultSecretClient.GetVersionId(id),
            VersionStages: ["AWSCURRENT"],
            CreatedDate: createdDate);

        await WriteJsonAsync(context, payload, SecretsManagerJsonContext.Default.UpdateSecretResponse).ConfigureAwait(false);
    }

    private async Task HandleDeleteSecretAsync(HttpContext context, KeyVaultSecretClient client, JsonDocument document, CancellationToken cancellationToken)
    {
        var name = KeyVaultSecretClient.NormalizeSecretName(ReadString(document, "SecretId") ?? string.Empty);
        var token = await client.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Delete, client.BuildVaultUri(KeyVaultSecretClient.BuildSecretPath(name)));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            await WriteAwsErrorAsync(context, MapStatusCode(response.StatusCode), MapErrorCode(response.StatusCode), "Key Vault request failed.").ConfigureAwait(false);
            return;
        }

        var payload = new DeleteSecretResponse(
            Arn: KeyVaultSecretClient.BuildArn(name),
            Name: name,
            DeletionDate: DateTimeOffset.UtcNow.AddDays(7),
            DeletedDate: DateTimeOffset.UtcNow,
            VersionId: null);

        await WriteJsonAsync(context, payload, SecretsManagerJsonContext.Default.DeleteSecretResponse).ConfigureAwait(false);
    }

    private async Task HandleListSecretsAsync(HttpContext context, KeyVaultSecretClient client, JsonDocument document, CancellationToken cancellationToken)
    {
        var token = await client.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);

        // AWS NextToken carries the Key Vault $skiptoken so callers can page.
        // We always rebuild our own vault URI (never trust an inbound URL) to
        // avoid turning NextToken into an SSRF vector with a live bearer token.
        var skipToken = ReadString(document, "NextToken");
        var maxResults = ReadInt(document, "MaxResults");
        var requestUri = client.BuildVaultUri("/secrets");
        if (maxResults is > 0)
        {
            requestUri += "&maxresults=" + maxResults.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
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
            await WriteAwsErrorAsync(context, MapStatusCode(response.StatusCode), MapErrorCode(response.StatusCode), "Key Vault request failed.").ConfigureAwait(false);
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var secretDocument = JsonDocument.Parse(body);

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
                var tagList = tags.Count == 0
                    ? Array.Empty<SecretsManagerTag>()
                    : tags.Select(static kvp => new SecretsManagerTag(kvp.Key, kvp.Value)).ToArray();
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
                    Tags: tagList,
                    VersionIdsToStages: versionIdsToStages));
            }
        }

        await WriteJsonAsync(context, new ListSecretsResponse(items, nextToken), SecretsManagerJsonContext.Default.ListSecretsResponse).ConfigureAwait(false);
    }

    private async Task HandleDescribeSecretAsync(HttpContext context, KeyVaultSecretClient client, JsonDocument document, CancellationToken cancellationToken)
    {
        var name = KeyVaultSecretClient.NormalizeSecretName(ReadString(document, "SecretId") ?? string.Empty);
        var token = await client.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Get, client.BuildVaultUri(KeyVaultSecretClient.BuildSecretPath(name)));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            await WriteAwsErrorAsync(context, MapStatusCode(response.StatusCode), MapErrorCode(response.StatusCode), "Key Vault request failed.").ConfigureAwait(false);
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var secretDocument = JsonDocument.Parse(body);
        var nameValue = secretDocument.RootElement.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String ? nameElement.GetString() ?? string.Empty : name;
        var id = secretDocument.RootElement.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String ? idElement.GetString() ?? string.Empty : string.Empty;
        var created = KeyVaultSecretClient.GetCreatedDate(secretDocument.RootElement);
        var lastChanged = KeyVaultSecretClient.GetLastChangedDate(secretDocument.RootElement);
        var description = KeyVaultSecretClient.GetDescription(secretDocument.RootElement);
        var tags = KeyVaultSecretClient.GetTags(secretDocument.RootElement);
        var tagList = tags.Count == 0
            ? Array.Empty<SecretsManagerTag>()
            : tags.Select(static kvp => new SecretsManagerTag(kvp.Key, kvp.Value)).ToArray();
        var versionIdsToStages = string.IsNullOrWhiteSpace(id)
            ? null
            : new Dictionary<string, IReadOnlyList<string>>
            {
                [KeyVaultSecretClient.GetVersionId(id)] = ["AWSCURRENT"],
            };

        var payload = new DescribeSecretResponse(
            Arn: KeyVaultSecretClient.BuildArn(nameValue),
            Name: nameValue,
            Description: description,
            CreatedDate: created,
            LastChangedDate: lastChanged,
            Tags: tagList,
            VersionIdsToStages: versionIdsToStages,
            RotationEnabled: null,
            DeletedDate: null);

        await WriteJsonAsync(context, payload, SecretsManagerJsonContext.Default.DescribeSecretResponse).ConfigureAwait(false);
    }

    private async Task<bool?> SecretExistsAsync(HttpContext context, KeyVaultSecretClient client, string token, string name, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, client.BuildVaultUri(KeyVaultSecretClient.BuildSecretPath(name)));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        if (!response.IsSuccessStatusCode)
        {
            await WriteAwsErrorAsync(context, MapStatusCode(response.StatusCode), MapErrorCode(response.StatusCode), "Key Vault request failed.").ConfigureAwait(false);
            return null;
        }

        return true;
    }

    private static int MapStatusCode(HttpStatusCode statusCode)
        => statusCode switch
        {
            HttpStatusCode.NotFound => StatusCodes.Status404NotFound,
            HttpStatusCode.Conflict => StatusCodes.Status409Conflict,
            HttpStatusCode.BadRequest => StatusCodes.Status400BadRequest,
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => StatusCodes.Status403Forbidden,
            HttpStatusCode.TooManyRequests => StatusCodes.Status429TooManyRequests,
            >= HttpStatusCode.InternalServerError => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status400BadRequest,
        };

    private static string MapErrorCode(HttpStatusCode statusCode)
        => statusCode switch
        {
            HttpStatusCode.NotFound => "ResourceNotFoundException",
            HttpStatusCode.Conflict => "ResourceExistsException",
            HttpStatusCode.BadRequest => "InvalidParameterException",
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "AccessDeniedException",
            HttpStatusCode.TooManyRequests => "ThrottlingException",
            >= HttpStatusCode.InternalServerError => "InternalServiceError",
            _ => "InvalidParameterException",
        };
}
