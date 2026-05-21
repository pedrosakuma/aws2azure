using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Aws2Azure.Modules.DynamoDb.WireProtocol;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Aws2Azure.UnitTests.DynamoDb;

public class DynamoDbWireProtocolParserTests
{
    [Theory]
    [InlineData("DynamoDB_20120810.GetItem", DynamoDbOperation.GetItem)]
    [InlineData("DynamoDB_20120810.PutItem", DynamoDbOperation.PutItem)]
    [InlineData("DynamoDB_20120810.UpdateItem", DynamoDbOperation.UpdateItem)]
    [InlineData("DynamoDB_20120810.DeleteItem", DynamoDbOperation.DeleteItem)]
    [InlineData("DynamoDB_20120810.BatchGetItem", DynamoDbOperation.BatchGetItem)]
    [InlineData("DynamoDB_20120810.BatchWriteItem", DynamoDbOperation.BatchWriteItem)]
    [InlineData("DynamoDB_20120810.Query", DynamoDbOperation.Query)]
    [InlineData("DynamoDB_20120810.Scan", DynamoDbOperation.Scan)]
    [InlineData("DynamoDB_20120810.CreateTable", DynamoDbOperation.CreateTable)]
    [InlineData("DynamoDB_20120810.DeleteTable", DynamoDbOperation.DeleteTable)]
    [InlineData("DynamoDB_20120810.DescribeTable", DynamoDbOperation.DescribeTable)]
    [InlineData("DynamoDB_20120810.ListTables", DynamoDbOperation.ListTables)]
    public async Task Parses_recognised_targets(string target, DynamoDbOperation expected)
    {
        var ctx = NewPostCtx(target, body: "{}");
        var r = await DynamoDbWireProtocolParser.ParseAsync(ctx, CancellationToken.None);

        Assert.Null(r.Error);
        Assert.Equal(expected, r.Operation);
        Assert.Equal(target, r.Target);
    }

    [Fact]
    public async Task Rejects_non_post_method()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = HttpMethods.Get;
        var r = await DynamoDbWireProtocolParser.ParseAsync(ctx, CancellationToken.None);

        Assert.NotNull(r.Error);
        Assert.Equal(StatusCodes.Status400BadRequest, r.Error!.StatusCode);
        Assert.Equal("InvalidAction", r.Error.Code);
    }

    [Fact]
    public async Task Rejects_missing_target_header()
    {
        var ctx = NewPostCtx(target: null, body: "{}");
        var r = await DynamoDbWireProtocolParser.ParseAsync(ctx, CancellationToken.None);

        Assert.NotNull(r.Error);
        Assert.Equal("MissingActionException", r.Error!.Code);
    }

    [Theory]
    [InlineData("AmazonSQS.SendMessage")]
    [InlineData("DynamoDB_20120810.NotARealOp")]
    [InlineData("DynamoDB_20180717.GetItem")] // wrong version prefix
    [InlineData("GetItem")] // no service prefix
    public async Task Rejects_unknown_targets(string target)
    {
        var ctx = NewPostCtx(target, body: "{}");
        var r = await DynamoDbWireProtocolParser.ParseAsync(ctx, CancellationToken.None);

        Assert.NotNull(r.Error);
        Assert.Equal("UnknownOperationException", r.Error!.Code);
        Assert.Equal(DynamoDbOperation.Unknown, r.Operation);
    }

    [Fact]
    public async Task Rejects_non_object_body()
    {
        var ctx = NewPostCtx("DynamoDB_20120810.GetItem", body: "[1,2,3]");
        var r = await DynamoDbWireProtocolParser.ParseAsync(ctx, CancellationToken.None);

        Assert.NotNull(r.Error);
        Assert.Equal("SerializationException", r.Error!.Code);
    }

    [Fact]
    public async Task Rejects_malformed_json_body()
    {
        var ctx = NewPostCtx("DynamoDB_20120810.GetItem", body: "{\"x\":");
        var r = await DynamoDbWireProtocolParser.ParseAsync(ctx, CancellationToken.None);

        Assert.NotNull(r.Error);
        Assert.Equal("SerializationException", r.Error!.Code);
    }

    [Fact]
    public async Task Accepts_empty_body_for_parameterless_op()
    {
        // ListTables happily takes no body on many SDK paths.
        var ctx = NewPostCtx("DynamoDB_20120810.ListTables", body: string.Empty);
        ctx.Request.ContentLength = 0;
        var r = await DynamoDbWireProtocolParser.ParseAsync(ctx, CancellationToken.None);

        Assert.Null(r.Error);
        Assert.Equal(DynamoDbOperation.ListTables, r.Operation);
        Assert.Empty(r.Body);
    }

    [Fact]
    public async Task Sniff_operation_returns_op_without_reading_body()
    {
        var ctx = NewPostCtx("DynamoDB_20120810.PutItem", body: "{}");
        var op = DynamoDbWireProtocolParser.SniffOperation(ctx);
        Assert.Equal(DynamoDbOperation.PutItem, op);
    }

    [Fact]
    public async Task Preserves_body_bytes_verbatim()
    {
        const string body = "{\"TableName\":\"t\",\"Key\":{\"id\":{\"S\":\"abc\"}}}";
        var ctx = NewPostCtx("DynamoDB_20120810.GetItem", body);
        var r = await DynamoDbWireProtocolParser.ParseAsync(ctx, CancellationToken.None);

        Assert.Null(r.Error);
        Assert.Equal(body, Encoding.UTF8.GetString(r.Body));
    }

    private static DefaultHttpContext NewPostCtx(string? target, string body)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = HttpMethods.Post;
        ctx.Request.ContentType = "application/x-amz-json-1.0";
        if (target is not null) ctx.Request.Headers["X-Amz-Target"] = target;
        var bytes = Encoding.UTF8.GetBytes(body);
        ctx.Request.Body = new MemoryStream(bytes);
        ctx.Request.ContentLength = bytes.Length;
        return ctx;
    }
}
