using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Aws2Azure.Modules.Kinesis.WireProtocol;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Aws2Azure.UnitTests.Kinesis;

public class KinesisWireProtocolParserTests
{
    [Theory]
    [InlineData("Kinesis_20131202.PutRecord", KinesisOperation.PutRecord)]
    [InlineData("Kinesis_20131202.PutRecords", KinesisOperation.PutRecords)]
    [InlineData("Kinesis_20131202.GetRecords", KinesisOperation.GetRecords)]
    [InlineData("Kinesis_20131202.GetShardIterator", KinesisOperation.GetShardIterator)]
    [InlineData("Kinesis_20131202.DescribeStream", KinesisOperation.DescribeStream)]
    [InlineData("Kinesis_20131202.DescribeStreamSummary", KinesisOperation.DescribeStreamSummary)]
    [InlineData("Kinesis_20131202.ListShards", KinesisOperation.ListShards)]
    public async Task Parses_recognised_targets(string target, KinesisOperation expected)
    {
        var ctx = NewPostCtx(target, body: "{}");
        var r = await KinesisWireProtocolParser.ParseAsync(ctx, CancellationToken.None);

        Assert.Null(r.Error);
        Assert.Equal(expected, r.Operation);
        Assert.Equal(target, r.Target);
    }

    [Fact]
    public async Task Rejects_non_post_method()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = HttpMethods.Get;
        var r = await KinesisWireProtocolParser.ParseAsync(ctx, CancellationToken.None);

        Assert.NotNull(r.Error);
        Assert.Equal(StatusCodes.Status400BadRequest, r.Error!.StatusCode);
        Assert.Equal("InvalidAction", r.Error.Code);
    }

    [Fact]
    public async Task Rejects_missing_target_header()
    {
        var ctx = NewPostCtx(target: null, body: "{}");
        var r = await KinesisWireProtocolParser.ParseAsync(ctx, CancellationToken.None);

        Assert.NotNull(r.Error);
        Assert.Equal("MissingActionException", r.Error!.Code);
    }

    [Theory]
    [InlineData("Kinesis_20131202.CreateStream")]
    [InlineData("Kinesis_20131202.SplitShard")]
    [InlineData("DynamoDB_20120810.PutItem")]
    [InlineData("garbage")]
    public async Task Rejects_unknown_or_mismatched_target(string target)
    {
        var ctx = NewPostCtx(target, body: "{}");
        var r = await KinesisWireProtocolParser.ParseAsync(ctx, CancellationToken.None);

        Assert.NotNull(r.Error);
        Assert.Equal("UnknownOperationException", r.Error!.Code);
    }

    [Fact]
    public async Task Rejects_body_above_buffer_cap_via_content_length()
    {
        var ctx = NewPostCtx("Kinesis_20131202.PutRecords", body: "{}");
        ctx.Request.ContentLength = KinesisWireProtocolParser.MaxBodyBytes + 1L;

        var r = await KinesisWireProtocolParser.ParseAsync(ctx, CancellationToken.None);

        Assert.NotNull(r.Error);
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, r.Error!.StatusCode);
        Assert.Equal("RequestEntityTooLarge", r.Error.Code);
    }

    [Fact]
    public async Task Rejects_non_object_body()
    {
        var ctx = NewPostCtx("Kinesis_20131202.PutRecord", body: "[1,2,3]");
        var r = await KinesisWireProtocolParser.ParseAsync(ctx, CancellationToken.None);

        Assert.NotNull(r.Error);
        Assert.Equal("SerializationException", r.Error!.Code);
    }

    [Fact]
    public async Task Accepts_empty_body_for_recognised_op()
    {
        var ctx = NewPostCtx("Kinesis_20131202.ListShards", body: "");
        ctx.Request.ContentLength = 0;
        var r = await KinesisWireProtocolParser.ParseAsync(ctx, CancellationToken.None);

        Assert.Null(r.Error);
        Assert.Equal(KinesisOperation.ListShards, r.Operation);
        Assert.Empty(r.Body);
    }

    private static DefaultHttpContext NewPostCtx(string? target, string body)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = HttpMethods.Post;
        if (target is not null) ctx.Request.Headers["X-Amz-Target"] = target;
        var bytes = Encoding.UTF8.GetBytes(body);
        ctx.Request.Body = new MemoryStream(bytes);
        ctx.Request.ContentLength = bytes.Length;
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }
}
