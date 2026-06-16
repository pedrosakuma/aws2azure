using System.Text;
using System.Threading.Tasks;
using System.Xml;
using Aws2Azure.Core.Xml;
using Aws2Azure.Modules.Sns.Xml;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.Sns.Errors;

public static class SnsErrorResponse
{
    public static async Task WriteErrorAsync(
        HttpContext context,
        int httpStatus,
        string errorType,
        string errorCode,
        string message)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrEmpty(errorType);
        ArgumentException.ThrowIfNullOrEmpty(errorCode);
        ArgumentException.ThrowIfNullOrEmpty(message);

        var requestId = SnsResponseWriter.ResolveRequestId(context);
        context.Response.StatusCode = httpStatus;
        context.Response.ContentType = SnsResponseWriter.XmlContentType;
        context.Response.Headers["x-amzn-requestid"] = requestId;
        await context.Response.WriteAsync(BuildXml(errorType, errorCode, message, requestId)).ConfigureAwait(false);
    }

    internal static string BuildXml(string errorType, string errorCode, string message, string requestId)
    {
        var sb = new StringBuilder();
        using (var sw = new Utf8StringWriter(sb))
        using (var writer = XmlWriter.Create(sw, XmlSettings))
        {
            writer.WriteStartDocument();
            writer.WriteStartElement("ErrorResponse", SnsResponseWriter.XmlNamespace);
            writer.WriteStartElement("Error", SnsResponseWriter.XmlNamespace);
            writer.WriteElementString("Type", SnsResponseWriter.XmlNamespace, errorType);
            writer.WriteElementString("Code", SnsResponseWriter.XmlNamespace, errorCode);
            writer.WriteElementString("Message", SnsResponseWriter.XmlNamespace, message);
            writer.WriteEndElement();
            writer.WriteElementString("RequestId", SnsResponseWriter.XmlNamespace, requestId);
            writer.WriteEndElement();
            writer.WriteEndDocument();
            writer.Flush();
        }

        return sb.ToString();
    }

    private static readonly XmlWriterSettings XmlSettings = new()
    {
        Indent = false,
        OmitXmlDeclaration = false,
        Encoding = Encoding.UTF8,
        CloseOutput = false,
    };
}
