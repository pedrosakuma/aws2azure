using System.Text;
using System.Threading.Tasks;
using System.Xml;
using Aws2Azure.Modules.Sns.Operations;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.Sns.Xml;

public static class SnsResponseWriter
{
    public const string XmlNamespace = "http://sns.amazonaws.com/doc/2010-03-31/";
    public const string XmlContentType = "text/xml; charset=utf-8";

    public static Task WriteEmptyResponseAsync(HttpContext context, string operationName)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrEmpty(operationName);

        return WriteResponseAsync(context, writer =>
        {
            writer.WriteStartElement(operationName + "Response", XmlNamespace);
            writer.WriteStartElement(operationName + "Result", XmlNamespace);
            writer.WriteEndElement();
            WriteResponseMetadata(writer, ResolveRequestId(context));
            writer.WriteEndElement();
        });
    }

    public static Task WriteCreateTopicResponseAsync(HttpContext context, string topicArn)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrEmpty(topicArn);

        return WriteResponseAsync(context, writer =>
        {
            writer.WriteStartElement("CreateTopicResponse", XmlNamespace);
            writer.WriteStartElement("CreateTopicResult", XmlNamespace);
            writer.WriteElementString("TopicArn", XmlNamespace, topicArn);
            writer.WriteEndElement();
            WriteResponseMetadata(writer, ResolveRequestId(context));
            writer.WriteEndElement();
        });
    }

    public static Task WriteListTopicsResponseAsync(HttpContext context, IReadOnlyList<string> topicArns, string? nextToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(topicArns);

        return WriteResponseAsync(context, writer =>
        {
            writer.WriteStartElement("ListTopicsResponse", XmlNamespace);
            writer.WriteStartElement("ListTopicsResult", XmlNamespace);
            writer.WriteStartElement("Topics", XmlNamespace);
            for (var i = 0; i < topicArns.Count; i++)
            {
                writer.WriteStartElement("member", XmlNamespace);
                writer.WriteElementString("TopicArn", XmlNamespace, topicArns[i]);
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
            if (!string.IsNullOrWhiteSpace(nextToken))
            {
                writer.WriteElementString("NextToken", XmlNamespace, nextToken);
            }

            writer.WriteEndElement();
            WriteResponseMetadata(writer, ResolveRequestId(context));
            writer.WriteEndElement();
        });
    }

    internal static Task WritePublishResponseAsync(HttpContext context, string messageId)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrEmpty(messageId);

        return WriteResponseAsync(context, writer =>
        {
            writer.WriteStartElement("PublishResponse", XmlNamespace);
            writer.WriteStartElement("PublishResult", XmlNamespace);
            writer.WriteElementString("MessageId", XmlNamespace, messageId);
            writer.WriteStartElement("SequenceNumber", XmlNamespace);
            writer.WriteEndElement();
            writer.WriteEndElement();
            WriteResponseMetadata(writer, ResolveRequestId(context));
            writer.WriteEndElement();
        });
    }

    internal static Task WritePublishBatchResponseAsync(HttpContext context, PublishBatchResult result)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(result);

        return WriteResponseAsync(context, writer =>
        {
            writer.WriteStartElement("PublishBatchResponse", XmlNamespace);
            writer.WriteStartElement("PublishBatchResult", XmlNamespace);

            writer.WriteStartElement("Successful", XmlNamespace);
            for (var i = 0; i < result.Successful.Count; i++)
            {
                writer.WriteStartElement("member", XmlNamespace);
                writer.WriteElementString("Id", XmlNamespace, result.Successful[i].Id);
                writer.WriteElementString("MessageId", XmlNamespace, result.Successful[i].MessageId);
                writer.WriteStartElement("SequenceNumber", XmlNamespace);
                writer.WriteEndElement();
                writer.WriteEndElement();
            }
            writer.WriteEndElement();

            writer.WriteStartElement("Failed", XmlNamespace);
            for (var i = 0; i < result.Failed.Count; i++)
            {
                writer.WriteStartElement("member", XmlNamespace);
                writer.WriteElementString("Id", XmlNamespace, result.Failed[i].Id);
                writer.WriteElementString("Code", XmlNamespace, result.Failed[i].Code);
                writer.WriteElementString("Message", XmlNamespace, result.Failed[i].Message);
                writer.WriteElementString("SenderFault", XmlNamespace, result.Failed[i].SenderFault ? "true" : "false");
                writer.WriteEndElement();
            }
            writer.WriteEndElement();

            writer.WriteEndElement();
            WriteResponseMetadata(writer, ResolveRequestId(context));
            writer.WriteEndElement();
        });
    }

    internal static string ResolveRequestId(HttpContext context)
    {
        if (context.Response.Headers.TryGetValue("x-amzn-requestid", out var existing)
            && existing.Count > 0
            && !string.IsNullOrEmpty(existing[0]))
        {
            return existing[0]!;
        }

        return context.TraceIdentifier;
    }

    private static Task WriteResponseAsync(HttpContext context, Action<XmlWriter> writeBody)
    {
        var requestId = ResolveRequestId(context);
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = XmlContentType;
        context.Response.Headers["x-amzn-requestid"] = requestId;

        var sb = new StringBuilder();
        using (var sw = new Utf8StringWriter(sb))
        using (var writer = XmlWriter.Create(sw, XmlSettings))
        {
            writer.WriteStartDocument();
            writeBody(writer);
            writer.WriteEndDocument();
            writer.Flush();
        }

        return context.Response.WriteAsync(sb.ToString());
    }

    private static void WriteResponseMetadata(XmlWriter writer, string requestId)
    {
        writer.WriteStartElement("ResponseMetadata", XmlNamespace);
        writer.WriteElementString("RequestId", XmlNamespace, requestId);
        writer.WriteEndElement();
    }

    private static readonly XmlWriterSettings XmlSettings = new()
    {
        Indent = false,
        OmitXmlDeclaration = false,
        Encoding = Encoding.UTF8,
        CloseOutput = false,
    };

    private sealed class Utf8StringWriter(StringBuilder builder) : System.IO.StringWriter(builder)
    {
        public override Encoding Encoding => Encoding.UTF8;
    }
}
