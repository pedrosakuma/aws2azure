using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using Aws2Azure.Core.Xml;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Core.Modules;

/// <summary>
/// Emits an AWS-shaped error response in either S3 XML form or the
/// JSON form used by the rest of the AWS services.
/// </summary>
public static class AwsErrorResponse
{
    public static async Task WriteAsync(
        HttpContext context,
        AwsErrorFormat format,
        int statusCode,
        string code,
        string message,
        string? resource = null,
        string jsonContentType = "application/x-amz-json-1.0",
        string requestIdHeaderName = "x-amz-request-id")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestIdHeaderName);
        context.Response.StatusCode = statusCode;
        var requestId = ResolveRequestId(context, requestIdHeaderName);
        context.Response.Headers[requestIdHeaderName] = requestId;

        if (format == AwsErrorFormat.Xml)
        {
            context.Response.ContentType = "application/xml; charset=utf-8";
            await context.Response.WriteAsync(BuildXml(code, message, resource, requestId));
        }
        else
        {
            context.Response.ContentType = jsonContentType;
            await context.Response.WriteAsync(BuildJson(code, message));
        }
    }

    /// <summary>
    /// Writes an AWS-JSON <em>frontend-rejection</em> error whose <c>message</c>
    /// field must be omitted to match real AWS behaviour (issue #854). Real AWS
    /// Kinesis / DynamoDB return
    /// <c>{"__type":"SerializationException"}</c> or
    /// <c>{"__type":"UnknownOperationException"}</c> with no <c>message</c>
    /// field when the AWS-JSON frontend rejects the request before dispatch
    /// (malformed body, unknown <c>X-Amz-Target</c>). Handler-level
    /// <c>SerializationException</c> emitted from an operation after
    /// dispatch still carries a message and must use
    /// <see cref="WriteAsync(HttpContext, AwsErrorFormat, int, string, string, string?, string, string)"/>.
    /// </summary>
    public static async Task WriteJsonWithoutMessageAsync(
        HttpContext context,
        int statusCode,
        string code,
        string jsonContentType = "application/x-amz-json-1.0",
        string requestIdHeaderName = "x-amz-request-id")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestIdHeaderName);
        context.Response.StatusCode = statusCode;
        var requestId = ResolveRequestId(context, requestIdHeaderName);
        context.Response.Headers[requestIdHeaderName] = requestId;
        context.Response.ContentType = jsonContentType;
        await context.Response.WriteAsync(BuildJsonWithoutMessage(code));
    }

    public static string BuildXml(string code, string message, string? resource, string requestId)
    {
        // Use XmlWriter directly (AOT-safe; XmlSerializer is banned).
        // XmlWriter.Create(StringBuilder, …) hard-codes encoding="utf-16" in
        // the XML declaration regardless of the requested Encoding setting,
        // so we wrap the StringBuilder in a UTF-8-reporting StringWriter to
        // keep the declaration in sync with the bytes ASP.NET ultimately
        // sends on the wire.
        var sb = new StringBuilder();
        using (var sw = new Utf8StringWriter(sb))
        using (var writer = XmlWriter.Create(sw, new XmlWriterSettings
        {
            Indent = false,
            OmitXmlDeclaration = false,
            Encoding = Encoding.UTF8,
            CloseOutput = false,
        }))
        {
            writer.WriteStartDocument();
            writer.WriteStartElement("Error");
            writer.WriteElementString("Code", code);
            writer.WriteElementString("Message", message);
            if (!string.IsNullOrEmpty(resource))
            {
                writer.WriteElementString("Resource", resource);
            }
            writer.WriteElementString("RequestId", requestId);
            writer.WriteEndElement();
            writer.WriteEndDocument();
            writer.Flush();
        }
        return sb.ToString();
    }

    public static string BuildJson(string code, string message)
        => JsonSerializer.Serialize(
            new AwsJsonError(code, message),
            AwsErrorJsonContext.Default.AwsJsonError);

    /// <summary>
    /// Renders <c>{"__type":"&lt;code&gt;"}</c> with no <c>message</c> field.
    /// See <see cref="WriteJsonWithoutMessageAsync"/> for when to use this.
    /// </summary>
    public static string BuildJsonWithoutMessage(string code)
        => JsonSerializer.Serialize(
            new AwsJsonError(code, Message: null),
            AwsErrorJsonContext.Default.AwsJsonError);

    private static string ResolveRequestId(HttpContext context, string requestIdHeaderName)
    {
        if (context.Response.Headers.TryGetValue(requestIdHeaderName, out var existing)
            && existing.Count > 0 && !string.IsNullOrEmpty(existing[0]))
        {
            return existing[0]!;
        }
        return context.TraceIdentifier;
    }
}

internal sealed record AwsJsonError(
    [property: JsonPropertyName("__type")] string Type,
    [property: JsonPropertyName("message")] string? Message);

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AwsJsonError))]
internal sealed partial class AwsErrorJsonContext : JsonSerializerContext
{
}
