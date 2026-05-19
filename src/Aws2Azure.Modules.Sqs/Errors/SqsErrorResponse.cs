using System;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Xml;
using Aws2Azure.Modules.Sqs.WireProtocol;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.Sqs.Errors;

/// <summary>
/// Renders an SQS-shaped error in the protocol the caller used. Query
/// callers get the classic XML <c>&lt;ErrorResponse&gt;</c> envelope;
/// AWS-JSON callers get <c>{"__type":"...","message":"..."}</c>. SQS-style
/// errors carry a <c>Type</c> (<c>Sender</c> or <c>Receiver</c>) which
/// SDKs use to decide retry behaviour.
/// </summary>
public static class SqsErrorResponse
{
    public enum FaultType { Sender, Receiver }

    public static async Task WriteAsync(
        HttpContext context,
        SqsWireProtocol protocol,
        int statusCode,
        string code,
        string message,
        FaultType faultType = FaultType.Sender)
    {
        context.Response.StatusCode = statusCode;
        var requestId = ResolveRequestId(context);
        context.Response.Headers["x-amzn-requestid"] = requestId;

        if (protocol == SqsWireProtocol.AwsJson)
        {
            context.Response.ContentType = "application/x-amz-json-1.0";
            // AWS-JSON SQS errors use a flat {"__type":"<Service>#<Code>",
            // "message":"..."} envelope. The HTTP status code carries the
            // Sender/Receiver hint (4xx = Sender, 5xx = Receiver), so we
            // don't need to emit Type separately.
            var payload = JsonSerializer.Serialize(
                new SqsJsonError("com.amazonaws.sqs#" + code, message),
                SqsErrorJsonContext.Default.SqsJsonError);
            await context.Response.WriteAsync(payload).ConfigureAwait(false);
            return;
        }

        // Default to the SQS query-protocol XML envelope: this matches
        // every SQS SDK error parser before the JSON migration.
        context.Response.ContentType = "text/xml; charset=utf-8";
        await context.Response.WriteAsync(
            BuildQueryXml(code, message, faultType, requestId)).ConfigureAwait(false);
    }

    public static string BuildQueryXml(string code, string message, FaultType faultType, string requestId)
    {
        // XmlWriter.Create(StringBuilder, …) always emits encoding="utf-16"
        // in the XML declaration regardless of the requested Encoding setting,
        // because StringBuilder is a UTF-16 sink. We then write the response
        // bytes as UTF-8, so strict XML parsers can choke on the declared/
        // actual encoding mismatch. Route the writer through a UTF-8-aware
        // StringWriter so the declaration matches the wire encoding.
        var sb = new StringBuilder();
        using (var sw = new Utf8StringWriter(sb))
        using (var w = XmlWriter.Create(sw, new XmlWriterSettings
        {
            Indent = false,
            OmitXmlDeclaration = false,
            Encoding = Encoding.UTF8,
            CloseOutput = false,
        }))
        {
            w.WriteStartDocument();
            w.WriteStartElement("ErrorResponse", "http://queue.amazonaws.com/doc/2012-11-05/");
            w.WriteStartElement("Error");
            w.WriteElementString("Type", faultType.ToString());
            w.WriteElementString("Code", code);
            w.WriteElementString("Message", message);
            w.WriteEndElement(); // Error
            w.WriteElementString("RequestId", requestId);
            w.WriteEndElement(); // ErrorResponse
            w.WriteEndDocument();
            w.Flush();
        }
        return sb.ToString();
    }

    private sealed class Utf8StringWriter : System.IO.StringWriter
    {
        public Utf8StringWriter(StringBuilder sb) : base(sb) { }
        public override Encoding Encoding => Encoding.UTF8;
    }

    private static string ResolveRequestId(HttpContext context)
    {
        if (context.Response.Headers.TryGetValue("x-amzn-requestid", out var existing)
            && existing.Count > 0 && !string.IsNullOrEmpty(existing[0]))
        {
            return existing[0]!;
        }
        return context.TraceIdentifier;
    }
}

internal sealed record SqsJsonError(
    [property: JsonPropertyName("__type")] string Type,
    [property: JsonPropertyName("message")] string Message);

[JsonSerializable(typeof(SqsJsonError))]
internal sealed partial class SqsErrorJsonContext : JsonSerializerContext
{
}
