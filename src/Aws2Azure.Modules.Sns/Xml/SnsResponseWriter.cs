using System.Text;
using System.Threading.Tasks;
using System.Xml;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.Sns.Xml;

public static class SnsResponseWriter
{
    public const string XmlNamespace = "http://sns.amazonaws.com/doc/2010-03-31/";
    public const string XmlContentType = "text/xml; charset=utf-8";

    public static async Task WriteEmptyResponseAsync(HttpContext context, string operationName)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrEmpty(operationName);

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = XmlContentType;
        context.Response.Headers["x-amzn-requestid"] = ResolveRequestId(context);
        await context.Response.WriteAsync(BuildEmptyResponse(operationName, context)).ConfigureAwait(false);
    }

    internal static string BuildEmptyResponse(string operationName, HttpContext context)
    {
        var sb = new StringBuilder();
        using (var sw = new Utf8StringWriter(sb))
        using (var writer = XmlWriter.Create(sw, XmlSettings))
        {
            writer.WriteStartDocument();
            writer.WriteStartElement(operationName + "Response", XmlNamespace);
            writer.WriteStartElement(operationName + "Result", XmlNamespace);
            writer.WriteEndElement();
            writer.WriteStartElement("ResponseMetadata", XmlNamespace);
            writer.WriteElementString("RequestId", XmlNamespace, ResolveRequestId(context));
            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteEndDocument();
            writer.Flush();
        }

        return sb.ToString();
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
