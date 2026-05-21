using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Aws2Azure.Modules.DynamoDb.WireProtocol;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Aws2Azure.UnitTests.DynamoDb;

public class DynamoDbParserReviewFixTests
{
    [Fact]
    public async Task Parser_rejects_content_length_over_limit_with_413_envelope()
    {
        // Advertise oversized body via ContentLength so we reject up front
        // without trying to drain.
        var ctx = NewPostCtx("DynamoDB_20120810.BatchWriteItem", body: "{}");
        ctx.Request.ContentLength = DynamoDbWireProtocolParser.MaxBodyBytes + 1L;

        var r = await DynamoDbWireProtocolParser.ParseAsync(ctx, CancellationToken.None);

        Assert.NotNull(r.Error);
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, r.Error!.StatusCode);
        Assert.Equal("RequestEntityTooLarge", r.Error.Code);
    }

    [Fact]
    public async Task Parser_rejects_streamed_body_over_limit_with_413_envelope()
    {
        // Hide actual size by leaving ContentLength null and streaming
        // more bytes than the cap. ReadBoundedBodyAsync's internal throw
        // must be converted to a parse error, not escape to the caller.
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = HttpMethods.Post;
        ctx.Request.ContentType = "application/x-amz-json-1.0";
        ctx.Request.Headers["X-Amz-Target"] = "DynamoDB_20120810.BatchWriteItem";
        // No ContentLength → forces the streaming path.
        ctx.Request.Body = new EndlessStream(DynamoDbWireProtocolParser.MaxBodyBytes + 8192);
        ctx.Response.Body = new MemoryStream();

        var r = await DynamoDbWireProtocolParser.ParseAsync(ctx, CancellationToken.None);

        Assert.NotNull(r.Error);
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, r.Error!.StatusCode);
        Assert.Equal("RequestEntityTooLarge", r.Error.Code);
    }

    private static DefaultHttpContext NewPostCtx(string target, string body)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = HttpMethods.Post;
        ctx.Request.ContentType = "application/x-amz-json-1.0";
        ctx.Request.Headers["X-Amz-Target"] = target;
        var bytes = Encoding.UTF8.GetBytes(body);
        ctx.Request.Body = new MemoryStream(bytes);
        ctx.Request.ContentLength = bytes.Length;
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    /// <summary>
    /// Read-only stream that returns zero bytes indefinitely up to
    /// <see cref="_total"/> then EOF. Used to drive the streaming
    /// over-limit path without allocating the entire payload.
    /// </summary>
    private sealed class EndlessStream : Stream
    {
        private readonly long _total;
        private long _produced;

        public EndlessStream(long total) { _total = total; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_produced >= _total) return 0;
            var take = (int)Math.Min(count, _total - _produced);
            Array.Clear(buffer, offset, take);
            _produced += take;
            return take;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            => Task.FromResult(Read(buffer, offset, count));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_produced >= _total) return ValueTask.FromResult(0);
            var take = (int)Math.Min(buffer.Length, _total - _produced);
            buffer.Span.Slice(0, take).Clear();
            _produced += take;
            return ValueTask.FromResult(take);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _total;
        public override long Position { get => _produced; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
