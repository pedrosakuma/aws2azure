using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
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
        string? resource = null)
    {
        context.Response.StatusCode = statusCode;
        var requestId = ResolveRequestId(context);
        context.Response.Headers["x-amz-request-id"] = requestId;

        if (format == AwsErrorFormat.Xml)
        {
            context.Response.ContentType = "application/xml; charset=utf-8";
            await context.Response.WriteAsync(BuildXml(code, message, resource, requestId));
        }
        else
        {
            context.Response.ContentType = "application/x-amz-json-1.0";
            await context.Response.WriteAsync(BuildJson(code, message));
        }
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

    private sealed class Utf8StringWriter : System.IO.StringWriter
    {
        public Utf8StringWriter(StringBuilder sb) : base(sb) { }
        public override Encoding Encoding => Encoding.UTF8;
    }

    public static string BuildJson(string code, string message)
        => JsonSerializer.Serialize(
            new AwsJsonError(code, message),
            AwsErrorJsonContext.Default.AwsJsonError);

    private static string ResolveRequestId(HttpContext context)
    {
        if (context.Response.Headers.TryGetValue("x-amz-request-id", out var existing)
            && existing.Count > 0 && !string.IsNullOrEmpty(existing[0]))
        {
            return existing[0]!;
        }
        return context.TraceIdentifier;
    }
}

internal sealed record AwsJsonError(
    [property: JsonPropertyName("__type")] string Type,
    [property: JsonPropertyName("message")] string Message);

[JsonSerializable(typeof(AwsJsonError))]
internal sealed partial class AwsErrorJsonContext : JsonSerializerContext
{
}
