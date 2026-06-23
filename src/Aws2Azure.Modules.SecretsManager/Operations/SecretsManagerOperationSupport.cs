using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Aws2Azure.Core.Buffers;
using Aws2Azure.Core.Modules;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.SecretsManager.Operations;

internal static class SecretsManagerOperationSupport
{
    private const string JsonContentType = "application/x-amz-json-1.1";

    public static async Task WriteAwsErrorAsync(HttpContext context, int statusCode, string code, string message)
    {
        await AwsErrorResponse.WriteAsync(context, AwsErrorFormat.Json, statusCode, code, message, resource: null, jsonContentType: JsonContentType).ConfigureAwait(false);
    }

    public static async Task WriteJsonAsync<T>(HttpContext context, T payload, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
    {
        using var buffer = new PooledByteBufferWriter();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            JsonSerializer.Serialize(writer, payload, typeInfo);
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = JsonContentType;
        await context.Response.BodyWriter.WriteAsync(buffer.WrittenMemory, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<JsonDocument> ReadJsonDocumentAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public static string? ReadString(JsonDocument document, string propertyName)
        => document.RootElement.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    public static int? ReadInt(JsonDocument document, string propertyName)
        => document.RootElement.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt32(out var value)
            ? value
            : null;

    public static async Task<bool?> SecretExistsAsync(HttpContext context, KeyVaultSecretClient client, string token, string name, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, client.BuildVaultUri(KeyVaultSecretClient.BuildSecretPath(name)));
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

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

    public static SecretsManagerTag[] ToTagArray(IReadOnlyDictionary<string, string> tags)
    {
        if (tags.Count == 0)
        {
            return [];
        }

        var result = new SecretsManagerTag[tags.Count];
        var index = 0;
        foreach (var tag in tags)
        {
            result[index++] = new SecretsManagerTag(tag.Key, tag.Value);
        }

        return result;
    }

    public static int MapStatusCode(HttpStatusCode statusCode)
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

    public static string MapErrorCode(HttpStatusCode statusCode)
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
