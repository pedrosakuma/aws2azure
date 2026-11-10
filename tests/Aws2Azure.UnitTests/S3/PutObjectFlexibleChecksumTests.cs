using System.Net;
using System.Security.Cryptography;
using System.Text;
using Aws2Azure.Core.Azure;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Modules.S3;
using Aws2Azure.Modules.S3.Internal;
using Aws2Azure.Modules.S3.Operations;
using Aws2Azure.TestSupport.Http;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.UnitTests.S3;

public sealed class PutObjectFlexibleChecksumTests
{
    private const string AccountName = "acct";
    private const string AccountKeyBase64 = "dGVzdC1rZXktQS0xMjM0NTY3ODkwYWJjZGVm";

    [Theory]
    [InlineData("CRC32", "x-amz-checksum-crc32", "y/Q5Jg==")]
    [InlineData("CRC32C", "x-amz-checksum-crc32c", "4waSgw==")]
    [InlineData("CRC64NVME", "x-amz-checksum-crc64nvme", "rosUhgp5mIg=")]
    [InlineData("SHA1", "x-amz-checksum-sha1", "98O8HYCOBHMq32eZZczDTKeuNEE=")]
    [InlineData("SHA256", "x-amz-checksum-sha256", "FeKw08M4keuw8e9gnsQZQgwg4yDOlMZfvIwzEkSOsiU=")]
    public async Task PutObject_accepts_matching_header_checksum(
        string algorithm,
        string checksumHeader,
        string checksum)
    {
        var handler = new ScriptedHandler();
        handler.Enqueue(AzureResponse(HttpStatusCode.Created));
        using var http = new AzureHttpClient(handler, ownsHandler: false);
        var context = CreatePutContext(
            "123456789"u8.ToArray(),
            [
                new("x-amz-sdk-checksum-algorithm", algorithm),
                new(checksumHeader, checksum),
            ]);

        await PutAsync(context, NewBlobClient(http), CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("123456789", request.Body);
        Assert.DoesNotContain(checksumHeader, request.Headers.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(checksumHeader, request.ContentHeaders.Keys, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PutObject_returns_bad_digest_for_header_checksum_mismatch()
    {
        var handler = new BodyCountingHandler();
        using var http = new AzureHttpClient(handler, ownsHandler: false);
        var payload = "123456789"u8.ToArray();
        var context = CreatePutContext(
            payload,
            [
                new("x-amz-sdk-checksum-algorithm", "CRC32"),
                new("x-amz-checksum-crc32", "AAAAAA=="),
            ]);

        await PutAsync(context, NewBlobClient(http), CancellationToken.None);

        await AssertBadDigestAsync(context);
        Assert.Equal(1, handler.CallCount);
        Assert.Equal(payload.Length - 1, handler.BytesReceived);
    }

    [Theory]
    [InlineData("not-base64")]
    [InlineData("AAAA")]
    public async Task PutObject_returns_bad_digest_for_malformed_header_checksum(string checksum)
    {
        var handler = new ScriptedHandler();
        using var http = new AzureHttpClient(handler, ownsHandler: false);
        var context = CreatePutContext(
            "payload"u8.ToArray(),
            [
                new("x-amz-sdk-checksum-algorithm", "CRC32"),
                new("x-amz-checksum-crc32", checksum),
            ]);

        await PutAsync(context, NewBlobClient(http), CancellationToken.None);

        await AssertBadDigestAsync(context);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task PutObject_rejects_unsupported_checksum_algorithm()
    {
        var handler = new ScriptedHandler();
        using var http = new AzureHttpClient(handler, ownsHandler: false);
        var context = CreatePutContext(
            "payload"u8.ToArray(),
            [new("x-amz-sdk-checksum-algorithm", "BLAKE3")]);

        await PutAsync(context, NewBlobClient(http), CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Contains("InvalidArgument", await TestHttpContext.ReadBodyAsync(context), StringComparison.Ordinal);
        Assert.Contains("BLAKE3", await TestHttpContext.ReadBodyAsync(context), StringComparison.Ordinal);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task PutObject_rejects_unknown_algorithm_specific_checksum_header()
    {
        var handler = new ScriptedHandler();
        using var http = new AzureHttpClient(handler, ownsHandler: false);
        var context = CreatePutContext(
            "payload"u8.ToArray(),
            [new("x-amz-checksum-blake3", "AAAAAA==")]);

        await PutAsync(context, NewBlobClient(http), CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Contains("InvalidArgument", await TestHttpContext.ReadBodyAsync(context), StringComparison.Ordinal);
        Assert.Contains("x-amz-checksum-blake3", await TestHttpContext.ReadBodyAsync(context), StringComparison.Ordinal);
        Assert.Equal(0, handler.CallCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PutObject_accepts_matching_aws_chunked_checksum_trailer(bool signed)
    {
        var payload = "streamed checksum payload"u8.ToArray();
        var checksum = Convert.ToBase64String(Crc32(payload));
        var framed = BuildAwsChunked(payload, signed, $"x-amz-checksum-crc32:{checksum}");
        var handler = new ScriptedHandler();
        handler.Enqueue(AzureResponse(HttpStatusCode.Created));
        using var http = new AzureHttpClient(handler, ownsHandler: false);
        var context = CreateTrailerContext(framed, payload.Length, signed, "CRC32", "x-amz-checksum-crc32");

        await PutAsync(context, NewBlobClient(http), CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(Encoding.UTF8.GetString(payload), Assert.Single(handler.Requests).Body);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PutObject_returns_bad_digest_for_aws_chunked_checksum_trailer_mismatch(bool signed)
    {
        var payload = "streamed checksum payload"u8.ToArray();
        var framed = BuildAwsChunked(payload, signed, "x-amz-checksum-crc32:AAAAAA==");
        var handler = new BodyCountingHandler();
        using var http = new AzureHttpClient(handler, ownsHandler: false);
        var context = CreateTrailerContext(framed, payload.Length, signed, "CRC32", "x-amz-checksum-crc32");

        await PutAsync(context, NewBlobClient(http), CancellationToken.None);

        await AssertBadDigestAsync(context);
        Assert.Equal(1, handler.CallCount);
        Assert.Equal(payload.Length - 1, handler.BytesReceived);
    }

    [Fact]
    public async Task PutObject_returns_bad_digest_for_malformed_aws_chunked_checksum_trailer()
    {
        var payload = "streamed checksum payload"u8.ToArray();
        var framed = BuildAwsChunked(payload, signed: false, "x-amz-checksum-crc32:not-base64");
        var handler = new ScriptedHandler();
        handler.Enqueue(AzureResponse(HttpStatusCode.Created));
        using var http = new AzureHttpClient(handler, ownsHandler: false);
        var context = CreateTrailerContext(
            framed,
            payload.Length,
            signed: false,
            "CRC32",
            "x-amz-checksum-crc32");

        await PutAsync(context, NewBlobClient(http), CancellationToken.None);

        await AssertBadDigestAsync(context);
    }

    [Fact]
    public async Task PutObject_returns_bad_digest_for_duplicate_aws_chunked_checksum_trailer()
    {
        var payload = "streamed checksum payload"u8.ToArray();
        var checksum = Convert.ToBase64String(Crc32(payload));
        var framed = BuildAwsChunked(
            payload,
            signed: false,
            $"x-amz-checksum-crc32:{checksum}\r\nx-amz-checksum-crc32:{checksum}");
        var handler = new BodyCountingHandler();
        using var http = new AzureHttpClient(handler, ownsHandler: false);
        var context = CreateTrailerContext(
            framed,
            payload.Length,
            signed: false,
            "CRC32",
            "x-amz-checksum-crc32");

        await PutAsync(context, NewBlobClient(http), CancellationToken.None);

        await AssertBadDigestAsync(context);
        Assert.Equal(payload.Length - 1, handler.BytesReceived);
    }

    [Fact]
    public async Task PutObject_rejects_unknown_checksum_trailer_before_upload()
    {
        var payload = "payload"u8.ToArray();
        var framed = BuildAwsChunked(payload, signed: false, "x-amz-checksum-blake3:AAAAAA==");
        var handler = new ScriptedHandler();
        using var http = new AzureHttpClient(handler, ownsHandler: false);
        var context = CreateTrailerContext(
            framed,
            payload.Length,
            signed: false,
            "BLAKE3",
            "x-amz-checksum-blake3");

        await PutAsync(context, NewBlobClient(http), CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Contains("InvalidArgument", await TestHttpContext.ReadBodyAsync(context), StringComparison.Ordinal);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task PutObject_validates_empty_and_multi_buffer_payload_boundaries()
    {
        var payload = new byte[(128 * 1024) + 17];
        Array.Fill(payload, (byte)'x');
        var checksum = Convert.ToBase64String(SHA256.HashData(payload));
        var handler = new ScriptedHandler();
        handler.Enqueue(AzureResponse(HttpStatusCode.Created));
        using var http = new AzureHttpClient(handler, ownsHandler: false);
        var context = CreatePutContext(
            payload,
            [
                new("x-amz-sdk-checksum-algorithm", "SHA256"),
                new("x-amz-checksum-sha256", checksum),
            ]);

        await PutAsync(context, NewBlobClient(http), CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(payload.Length, Encoding.UTF8.GetByteCount(Assert.Single(handler.Requests).Body!));

        var emptyHandler = new ScriptedHandler();
        emptyHandler.Enqueue(AzureResponse(HttpStatusCode.Created));
        using var emptyHttp = new AzureHttpClient(emptyHandler, ownsHandler: false);
        var emptyContext = CreatePutContext(
            [],
            [
                new("x-amz-sdk-checksum-algorithm", "SHA256"),
                new("x-amz-checksum-sha256", Convert.ToBase64String(SHA256.HashData([]))),
            ]);

        await PutAsync(emptyContext, NewBlobClient(emptyHttp), CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, emptyContext.Response.StatusCode);
        Assert.Equal(string.Empty, Assert.Single(emptyHandler.Requests).Body);
    }

    [Fact]
    public async Task PutObject_rejects_empty_payload_checksum_mismatch_before_azure_call()
    {
        var handler = new ScriptedHandler();
        handler.Enqueue(AzureResponse(HttpStatusCode.Created));
        using var http = new AzureHttpClient(handler, ownsHandler: false);
        var context = CreatePutContext(
            [],
            [
                new("x-amz-sdk-checksum-algorithm", "SHA256"),
                new("x-amz-checksum-sha256", Convert.ToBase64String(new byte[32])),
            ]);

        await PutAsync(context, NewBlobClient(http), CancellationToken.None);

        await AssertBadDigestAsync(context);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task PutObject_checksum_validation_preserves_cancellation()
    {
        var payload = new byte[1024];
        var checksum = Convert.ToBase64String(SHA256.HashData(payload));
        var handler = new ScriptedHandler();
        handler.Enqueue(AzureResponse(HttpStatusCode.Created));
        using var http = new AzureHttpClient(handler, ownsHandler: false);
        var context = CreatePutContext(
            payload,
            [
                new("x-amz-sdk-checksum-algorithm", "SHA256"),
                new("x-amz-checksum-sha256", checksum),
            ]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => PutAsync(context, NewBlobClient(http), cancellation.Token));

        Assert.NotEqual(StatusCodes.Status400BadRequest, context.Response.StatusCode);
    }

    private static async Task PutAsync(HttpContext context, BlobClient blob, CancellationToken cancellationToken) =>
        await ObjectHandlers.HandleAsync(
            context,
            new S3RouteResult(S3Operation.PutObject, "bucket", "object.txt", VirtualHosted: false),
            blob,
            cancellationToken);

    private static DefaultHttpContext CreatePutContext(
        byte[] body,
        IEnumerable<KeyValuePair<string, string>> headers)
    {
        var context = TestHttpContext.CreateContext(
            method: HttpMethods.Put,
            path: "/bucket/object.txt",
            headers: headers);
        context.Request.Body = new MemoryStream(body);
        context.Request.ContentLength = body.LongLength;
        return context;
    }

    private static DefaultHttpContext CreateTrailerContext(
        byte[] framedBody,
        int decodedLength,
        bool signed,
        string algorithm,
        string trailerName)
    {
        var context = CreatePutContext(
            framedBody,
            [
                new(
                    "x-amz-content-sha256",
                    signed
                        ? "STREAMING-AWS4-HMAC-SHA256-PAYLOAD-TRAILER"
                        : "STREAMING-UNSIGNED-PAYLOAD-TRAILER"),
                new("x-amz-decoded-content-length", decodedLength.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new("Content-Encoding", "aws-chunked"),
                new("x-amz-sdk-checksum-algorithm", algorithm),
                new("x-amz-trailer", trailerName),
            ]);
        return context;
    }

    private static byte[] BuildAwsChunked(byte[] payload, bool signed, string trailer)
    {
        using var stream = new MemoryStream();
        var header = signed
            ? $"{payload.Length:x};chunk-signature=dummy\r\n"
            : $"{payload.Length:x}\r\n";
        stream.Write(Encoding.ASCII.GetBytes(header));
        stream.Write(payload);
        stream.Write("\r\n"u8);
        stream.Write(Encoding.ASCII.GetBytes(signed
            ? "0;chunk-signature=dummy\r\n"
            : "0\r\n"));
        stream.Write(Encoding.ASCII.GetBytes(trailer));
        stream.Write("\r\n\r\n"u8);
        return stream.ToArray();
    }

    private static byte[] Crc32(ReadOnlySpan<byte> payload)
    {
        var crc = uint.MaxValue;
        foreach (var value in payload)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xEDB88320u);
            }
        }

        var result = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(result, crc ^ uint.MaxValue);
        return result;
    }

    private static async Task AssertBadDigestAsync(HttpContext context)
    {
        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Contains("BadDigest", await TestHttpContext.ReadBodyAsync(context), StringComparison.Ordinal);
    }

    private static BlobClient NewBlobClient(AzureHttpClient http) =>
        new(http, new BlobCredentials
        {
            AccountName = AccountName,
            AccountKey = AccountKeyBase64,
        });

    private static HttpResponseMessage AzureResponse(HttpStatusCode status) =>
        new(status)
        {
            Content = new ByteArrayContent([]),
        };

    private sealed class BodyCountingHandler : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        public long BytesReceived { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            if (request.Content is not null)
            {
                await request.Content.CopyToAsync(new CountingWriteStream(this), cancellationToken);
            }

            return AzureResponse(HttpStatusCode.Created);
        }

        private sealed class CountingWriteStream(BodyCountingHandler owner) : Stream
        {
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => owner.BytesReceived;
            public override long Position { get => owner.BytesReceived; set => throw new NotSupportedException(); }

            public override void Write(byte[] buffer, int offset, int count) =>
                owner.BytesReceived += count;

            public override ValueTask WriteAsync(
                ReadOnlyMemory<byte> buffer,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                owner.BytesReceived += buffer.Length;
                return ValueTask.CompletedTask;
            }

            public override void Flush() { }
            public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }
    }
}
