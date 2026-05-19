using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using Aws2Azure.Modules.Sqs.Errors;
using Aws2Azure.Modules.Sqs.WireProtocol;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Aws2Azure.UnitTests.Sqs;

public class SqsErrorResponseTests
{
    [Fact]
    public void Query_xml_declares_utf8_encoding_in_declaration()
    {
        var xml = SqsErrorResponse.BuildQueryXml(
            "MissingAction", "Action required.", SqsErrorResponse.FaultType.Sender, "req-1");
        Assert.Contains("encoding=\"utf-8\"", xml);
        Assert.DoesNotContain("utf-16", xml);
    }

    [Fact]
    public void Query_xml_uses_sqs_namespace_and_envelope()
    {
        var xml = SqsErrorResponse.BuildQueryXml(
            "InvalidAction", "Bad action", SqsErrorResponse.FaultType.Sender, "req-1");

        using var sr = new StringReader(xml);
        var doc = new XmlDocument();
        doc.Load(sr);
        var root = doc.DocumentElement!;
        Assert.Equal("ErrorResponse", root.LocalName);
        Assert.Equal("http://queue.amazonaws.com/doc/2012-11-05/", root.NamespaceURI);
        Assert.NotNull(root["Error", root.NamespaceURI]);
        Assert.Equal("Sender", root["Error", root.NamespaceURI]!["Type", root.NamespaceURI]!.InnerText);
        Assert.Equal("InvalidAction", root["Error", root.NamespaceURI]!["Code", root.NamespaceURI]!.InnerText);
        Assert.Equal("req-1", root["RequestId", root.NamespaceURI]!.InnerText);
    }

    [Fact]
    public async Task WriteAsync_query_sets_xml_content_type_and_request_id_header()
    {
        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new MemoryStream();

        await SqsErrorResponse.WriteAsync(ctx, SqsWireProtocol.Query, 400, "MissingAction", "msg");

        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Equal("text/xml; charset=utf-8", ctx.Response.ContentType);
        Assert.True(ctx.Response.Headers.ContainsKey("x-amzn-requestid"));
    }

    [Fact]
    public async Task WriteAsync_awsjson_emits_typed_payload_and_amz_json_content_type()
    {
        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new MemoryStream();

        await SqsErrorResponse.WriteAsync(ctx, SqsWireProtocol.AwsJson, 400, "MissingAction", "msg");

        Assert.Equal("application/x-amz-json-1.0", ctx.Response.ContentType);
        ctx.Response.Body.Position = 0;
        var body = await new StreamReader(ctx.Response.Body, Encoding.UTF8).ReadToEndAsync();
        Assert.Contains("\"__type\":\"com.amazonaws.sqs#MissingAction\"", body);
        Assert.Contains("\"message\":\"msg\"", body);
    }
}
