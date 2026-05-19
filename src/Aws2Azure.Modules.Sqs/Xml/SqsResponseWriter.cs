using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Xml;
using Aws2Azure.Modules.Sqs.WireProtocol;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.Sqs.Xml;

/// <summary>
/// Builds SQS-shaped success bodies in either the legacy Query XML envelope
/// or the AWS-JSON 1.0 envelope based on the wire protocol the caller used.
/// </summary>
internal static class SqsResponseWriter
{
    public const string QueryNamespace = "http://queue.amazonaws.com/doc/2012-11-05/";
    public const string AwsJsonContentType = "application/x-amz-json-1.0";
    public const string QueryXmlContentType = "text/xml; charset=utf-8";

    public static Task WriteCreateQueueAsync(HttpContext ctx, SqsWireProtocol protocol, string queueUrl) =>
        WriteAsync(ctx, protocol,
            xmlEnvelope: "CreateQueueResponse",
            xmlResult: "CreateQueueResult",
            jsonProps: new[] { ("QueueUrl", (object)queueUrl) },
            xmlContent: w => w.WriteElementString("QueueUrl", QueryNamespace, queueUrl));

    public static Task WriteGetQueueUrlAsync(HttpContext ctx, SqsWireProtocol protocol, string queueUrl) =>
        WriteAsync(ctx, protocol,
            xmlEnvelope: "GetQueueUrlResponse",
            xmlResult: "GetQueueUrlResult",
            jsonProps: new[] { ("QueueUrl", (object)queueUrl) },
            xmlContent: w => w.WriteElementString("QueueUrl", QueryNamespace, queueUrl));

    public static Task WriteDeleteQueueAsync(HttpContext ctx, SqsWireProtocol protocol) =>
        WriteAsync(ctx, protocol,
            xmlEnvelope: "DeleteQueueResponse",
            xmlResult: null,
            jsonProps: Array.Empty<(string, object)>(),
            xmlContent: null);

    public static Task WriteListQueuesAsync(
        HttpContext ctx, SqsWireProtocol protocol,
        IReadOnlyList<string> queueUrls,
        string? nextToken)
    {
        if (protocol == SqsWireProtocol.AwsJson)
        {
            var json = JsonSerializer.Serialize(
                new ListQueuesPayload(queueUrls, nextToken),
                SqsListJsonContext.Default.ListQueuesPayload);
            return WriteJsonAsync(ctx, json);
        }

        var sb = new StringBuilder();
        using (var sw = new Utf8StringWriter(sb))
        using (var w = XmlWriter.Create(sw, XmlSettings))
        {
            w.WriteStartDocument();
            w.WriteStartElement("ListQueuesResponse", QueryNamespace);
            w.WriteStartElement("ListQueuesResult", QueryNamespace);
            foreach (var url in queueUrls)
            {
                w.WriteElementString("QueueUrl", QueryNamespace, url);
            }
            if (!string.IsNullOrEmpty(nextToken))
            {
                w.WriteElementString("NextToken", QueryNamespace, nextToken);
            }
            w.WriteEndElement();
            WriteResponseMetadata(w, ctx);
            w.WriteEndElement();
            w.WriteEndDocument();
            w.Flush();
        }
        return WriteXmlAsync(ctx, sb.ToString());
    }

    public static Task WriteGetQueueAttributesAsync(
        HttpContext ctx, SqsWireProtocol protocol,
        IReadOnlyDictionary<string, string> attributes)
    {
        if (protocol == SqsWireProtocol.AwsJson)
        {
            var json = JsonSerializer.Serialize(
                new GetQueueAttributesPayload(attributes),
                SqsListJsonContext.Default.GetQueueAttributesPayload);
            return WriteJsonAsync(ctx, json);
        }

        var sb = new StringBuilder();
        using (var sw = new Utf8StringWriter(sb))
        using (var w = XmlWriter.Create(sw, XmlSettings))
        {
            w.WriteStartDocument();
            w.WriteStartElement("GetQueueAttributesResponse", QueryNamespace);
            w.WriteStartElement("GetQueueAttributesResult", QueryNamespace);
            foreach (var kv in attributes)
            {
                w.WriteStartElement("Attribute", QueryNamespace);
                w.WriteElementString("Name", QueryNamespace, kv.Key);
                w.WriteElementString("Value", QueryNamespace, kv.Value);
                w.WriteEndElement();
            }
            w.WriteEndElement();
            WriteResponseMetadata(w, ctx);
            w.WriteEndElement();
            w.WriteEndDocument();
            w.Flush();
        }
        return WriteXmlAsync(ctx, sb.ToString());
    }

    private static Task WriteAsync(
        HttpContext ctx, SqsWireProtocol protocol,
        string xmlEnvelope, string? xmlResult,
        (string Name, object Value)[] jsonProps,
        Action<XmlWriter>? xmlContent)
    {
        if (protocol == SqsWireProtocol.AwsJson)
        {
            return WriteJsonAsync(ctx, BuildSimpleJson(jsonProps));
        }

        var sb = new StringBuilder();
        using (var sw = new Utf8StringWriter(sb))
        using (var w = XmlWriter.Create(sw, XmlSettings))
        {
            w.WriteStartDocument();
            w.WriteStartElement(xmlEnvelope, QueryNamespace);
            if (xmlResult is not null)
            {
                w.WriteStartElement(xmlResult, QueryNamespace);
                xmlContent?.Invoke(w);
                w.WriteEndElement();
            }
            WriteResponseMetadata(w, ctx);
            w.WriteEndElement();
            w.WriteEndDocument();
            w.Flush();
        }
        return WriteXmlAsync(ctx, sb.ToString());
    }

    private static string BuildSimpleJson((string Name, object Value)[] props)
    {
        // No source-gen context needed for the trivial 1-property shape used
        // by CreateQueue / GetQueueUrl / DeleteQueue — emit it by hand to
        // stay AOT-clean.
        var sb = new StringBuilder("{");
        for (var i = 0; i < props.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append('"').Append(JsonEncodedText.Encode(props[i].Name)).Append("\":");
            switch (props[i].Value)
            {
                case string s:
                    sb.Append('"').Append(JsonEncodedText.Encode(s)).Append('"');
                    break;
                case bool b:
                    sb.Append(b ? "true" : "false");
                    break;
                default:
                    sb.Append('"').Append(JsonEncodedText.Encode(props[i].Value.ToString() ?? "")).Append('"');
                    break;
            }
        }
        sb.Append('}');
        return sb.ToString();
    }

    private static void WriteResponseMetadata(XmlWriter w, HttpContext ctx)
    {
        w.WriteStartElement("ResponseMetadata", QueryNamespace);
        w.WriteElementString("RequestId", QueryNamespace, ctx.TraceIdentifier);
        w.WriteEndElement();
    }

    private static Task WriteXmlAsync(HttpContext ctx, string body)
    {
        ctx.Response.StatusCode = StatusCodes.Status200OK;
        ctx.Response.ContentType = QueryXmlContentType;
        ctx.Response.Headers["x-amzn-requestid"] = ctx.TraceIdentifier;
        return ctx.Response.WriteAsync(body);
    }

    private static Task WriteJsonAsync(HttpContext ctx, string body)
    {
        ctx.Response.StatusCode = StatusCodes.Status200OK;
        ctx.Response.ContentType = AwsJsonContentType;
        ctx.Response.Headers["x-amzn-requestid"] = ctx.TraceIdentifier;
        return ctx.Response.WriteAsync(body);
    }

    private static readonly XmlWriterSettings XmlSettings = new()
    {
        Indent = false,
        OmitXmlDeclaration = false,
        Encoding = Encoding.UTF8,
        CloseOutput = false,
    };

    private sealed class Utf8StringWriter : StringWriter
    {
        public Utf8StringWriter(StringBuilder sb) : base(sb) { }
        public override Encoding Encoding => Encoding.UTF8;
    }
}

internal sealed record ListQueuesPayload(
    [property: JsonPropertyName("QueueUrls")] IReadOnlyList<string> QueueUrls,
    [property: JsonPropertyName("NextToken")] string? NextToken);

internal sealed record GetQueueAttributesPayload(
    [property: JsonPropertyName("Attributes")] IReadOnlyDictionary<string, string> Attributes);

[JsonSerializable(typeof(ListQueuesPayload))]
[JsonSerializable(typeof(GetQueueAttributesPayload))]
internal sealed partial class SqsListJsonContext : JsonSerializerContext
{
}
