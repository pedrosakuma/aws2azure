using System.IO;
using System.Text;
using System.Threading;
using Aws2Azure.Modules.Sqs;
using Aws2Azure.Modules.Sqs.WireProtocol;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Aws2Azure.UnitTests.Sqs;

public class SqsWireProtocolParserTests
{
    [Fact]
    public async Task Query_protocol_form_body_resolves_action_and_parameters()
    {
        var ctx = NewContext(
            contentType: "application/x-www-form-urlencoded",
            body: "Action=CreateQueue&QueueName=my-q&Version=2012-11-05&Attribute.1.Name=VisibilityTimeout&Attribute.1.Value=30");

        var result = await SqsWireProtocolParser.ParseAsync(ctx, CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal(SqsWireProtocol.Query, result.Protocol);
        Assert.Equal(SqsOperation.CreateQueue, result.Operation);
        Assert.Equal("my-q", result.Parameters["QueueName"]);
        Assert.Equal("VisibilityTimeout", result.Parameters["Attribute.1.Name"]);
        Assert.Equal("30", result.Parameters["Attribute.1.Value"]);
        Assert.Null(result.JsonBody);
    }

    [Fact]
    public async Task Query_protocol_query_string_only_resolves_action()
    {
        var ctx = NewContext(queryString: "?Action=GetQueueUrl&QueueName=my-q");

        var result = await SqsWireProtocolParser.ParseAsync(ctx, CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal(SqsWireProtocol.Query, result.Protocol);
        Assert.Equal(SqsOperation.GetQueueUrl, result.Operation);
        Assert.Equal("my-q", result.Parameters["QueueName"]);
    }

    [Fact]
    public async Task Aws_json_protocol_with_target_header_resolves_action()
    {
        var ctx = NewContext(
            contentType: "application/x-amz-json-1.0",
            body: """{"QueueName":"my-q","Attributes":{"VisibilityTimeout":"30"}}""",
            extraHeaders: new[] { ("X-Amz-Target", "AmazonSQS.CreateQueue") });

        var result = await SqsWireProtocolParser.ParseAsync(ctx, CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal(SqsWireProtocol.AwsJson, result.Protocol);
        Assert.Equal(SqsOperation.CreateQueue, result.Operation);
        Assert.Equal("my-q", result.Parameters["QueueName"]);
        // Nested objects (Attributes) are not flattened — handlers consume JsonBody.
        Assert.False(result.Parameters.ContainsKey("Attributes"));
        Assert.NotNull(result.JsonBody);
    }

    [Fact]
    public async Task Aws_json_protocol_with_action_in_body_is_accepted_when_target_missing()
    {
        var ctx = NewContext(
            contentType: "application/x-amz-json-1.0",
            body: """{"Action":"ListQueues"}""");

        var result = await SqsWireProtocolParser.ParseAsync(ctx, CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal(SqsWireProtocol.AwsJson, result.Protocol);
        Assert.Equal(SqsOperation.ListQueues, result.Operation);
    }

    [Fact]
    public async Task Missing_action_returns_error()
    {
        var ctx = NewContext(contentType: "application/x-www-form-urlencoded", body: "QueueName=x");

        var result = await SqsWireProtocolParser.ParseAsync(ctx, CancellationToken.None);

        Assert.NotNull(result.Error);
        Assert.Equal("MissingAction", result.Error!.Code);
    }

    [Fact]
    public async Task Unknown_action_returns_InvalidAction()
    {
        var ctx = NewContext(
            contentType: "application/x-www-form-urlencoded",
            body: "Action=BogusOp");

        var result = await SqsWireProtocolParser.ParseAsync(ctx, CancellationToken.None);

        Assert.NotNull(result.Error);
        Assert.Equal("InvalidAction", result.Error!.Code);
        Assert.Equal(SqsOperation.Unknown, result.Operation);
    }

    [Fact]
    public async Task Malformed_json_body_returns_MalformedQueryString()
    {
        var ctx = NewContext(
            contentType: "application/x-amz-json-1.0",
            body: "{not-json",
            extraHeaders: new[] { ("X-Amz-Target", "AmazonSQS.SendMessage") });

        var result = await SqsWireProtocolParser.ParseAsync(ctx, CancellationToken.None);

        Assert.NotNull(result.Error);
        Assert.Equal("MalformedQueryString", result.Error!.Code);
    }

    [Fact]
    public async Task Oversized_body_returns_InvalidRequest()
    {
        // Trigger the size-cap branch by lying about Content-Length.
        var ctx = NewContext(contentType: "application/x-amz-json-1.0",
            body: "{\"Action\":\"ListQueues\"}",
            extraHeaders: new[] { ("X-Amz-Target", "AmazonSQS.ListQueues") });
        ctx.Request.ContentLength = SqsWireProtocolParser.MaxBodyBytes + 1;

        var result = await SqsWireProtocolParser.ParseAsync(ctx, CancellationToken.None);

        Assert.NotNull(result.Error);
        Assert.Equal("InvalidRequest", result.Error!.Code);
    }

    [Fact]
    public async Task Form_url_encoded_with_wrong_content_type_still_parses_via_fallback()
    {
        // Some signed-URL callers send the form body with a vanilla
        // text/plain Content-Type after canonicalisation. The fallback
        // path must still recover the Action+params.
        var ctx = NewContext(
            contentType: "text/plain",
            body: "Action=DeleteMessage&QueueUrl=https%3A%2F%2Fsqs%2Fmy-q&ReceiptHandle=abc%2Bdef");

        var result = await SqsWireProtocolParser.ParseAsync(ctx, CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal(SqsWireProtocol.Query, result.Protocol);
        Assert.Equal(SqsOperation.DeleteMessage, result.Operation);
        Assert.Equal("https://sqs/my-q", result.Parameters["QueueUrl"]);
        Assert.Equal("abc+def", result.Parameters["ReceiptHandle"]);
    }

    [Fact]
    public async Task Aws_json_with_no_action_returns_MissingAction()
    {
        var ctx = NewContext(
            contentType: "application/x-amz-json-1.0",
            body: "{}");

        var result = await SqsWireProtocolParser.ParseAsync(ctx, CancellationToken.None);

        Assert.NotNull(result.Error);
        Assert.Equal("MissingAction", result.Error!.Code);
    }

    [Fact]
    public async Task Form_body_over_cap_returns_InvalidRequest()
    {
        var ctx = NewContext(contentType: "application/x-www-form-urlencoded",
            body: "Action=ListQueues");
        ctx.Request.ContentLength = SqsWireProtocolParser.MaxBodyBytes + 1;

        var result = await SqsWireProtocolParser.ParseAsync(ctx, CancellationToken.None);

        Assert.NotNull(result.Error);
        Assert.Equal("InvalidRequest", result.Error!.Code);
    }

    [Fact]
    public void Sniff_returns_query_for_form_post()
    {
        var ctx = NewContext(contentType: "application/x-www-form-urlencoded", body: "Action=X");
        Assert.Equal(SqsWireProtocol.Query, SqsWireProtocolParser.Sniff(ctx));
    }

    [Fact]
    public void Sniff_returns_awsjson_for_target_header()
    {
        var ctx = NewContext(extraHeaders: new[] { ("X-Amz-Target", "AmazonSQS.ListQueues") });
        Assert.Equal(SqsWireProtocol.AwsJson, SqsWireProtocolParser.Sniff(ctx));
    }

    private static DefaultHttpContext NewContext(
        string? contentType = null,
        string? body = null,
        string? queryString = null,
        (string Name, string Value)[]? extraHeaders = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "POST";
        ctx.Request.Path = "/";
        if (!string.IsNullOrEmpty(queryString))
        {
            ctx.Request.QueryString = new QueryString(queryString);
        }
        if (!string.IsNullOrEmpty(contentType))
        {
            ctx.Request.ContentType = contentType;
        }
        if (body is not null)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            ctx.Request.Body = new MemoryStream(bytes);
            ctx.Request.ContentLength = bytes.Length;
        }
        if (extraHeaders is not null)
        {
            foreach (var (n, v) in extraHeaders) ctx.Request.Headers[n] = v;
        }
        return ctx;
    }
}
