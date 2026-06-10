using System.Text;
using Aws2Azure.Core.SigV4;
using Aws2Azure.Modules.S3.Streaming;
using Xunit;

namespace Aws2Azure.UnitTests.S3;

public class AwsChunkedDecoderTests
{
    [Fact]
    public async Task Decodes_single_chunk_without_signature_verification()
    {
        // Arrange
        var payload = "Hello, World!"u8.ToArray();
        var chunked = BuildChunkedPayload(payload, chunkSize: payload.Length, signatureFunc: _ => "dummy");

        using var inputStream = new MemoryStream(chunked);
        using var decoder = new AwsChunkedDecoder(inputStream, signingContext: null);

        // Act
        using var output = new MemoryStream();
        await decoder.CopyToAsync(output);

        // Assert
        Assert.Equal(payload, output.ToArray());
    }

    [Fact]
    public async Task Decodes_multiple_chunks_without_signature_verification()
    {
        // Arrange: 100 bytes split into 30-byte chunks
        var payload = new byte[100];
        new Random(42).NextBytes(payload);
        var chunked = BuildChunkedPayload(payload, chunkSize: 30, signatureFunc: _ => "ignored");

        using var inputStream = new MemoryStream(chunked);
        using var decoder = new AwsChunkedDecoder(inputStream, signingContext: null);

        // Act
        using var output = new MemoryStream();
        await decoder.CopyToAsync(output);

        // Assert
        Assert.Equal(payload, output.ToArray());
    }

    [Fact]
    public async Task Decodes_empty_payload()
    {
        // Arrange: only the final zero-size chunk
        var chunked = Encoding.ASCII.GetBytes("0;chunk-signature=deadbeef\r\n\r\n");

        using var inputStream = new MemoryStream(chunked);
        using var decoder = new AwsChunkedDecoder(inputStream, signingContext: null);

        // Act
        using var output = new MemoryStream();
        await decoder.CopyToAsync(output);

        // Assert
        Assert.Empty(output.ToArray());
    }

    [Fact]
    public async Task Verifies_chunk_signatures_correctly()
    {
        // Arrange: simulate real signing chain
        var payload = "Test data for signing"u8.ToArray();
        var signingKey = SigningKey.Derive("secret", "20260526", "us-east-1", "s3");
        var amzDate = "20260526T120000Z";
        var credentialScope = "20260526/us-east-1/s3/aws4_request";
        var seedSignature = "seed1234567890abcdef1234567890abcdef1234567890abcdef1234567890ab";

        var signingContext = new ChunkSigningContext
        {
            SigningKey = signingKey,
            AmzDate = amzDate,
            CredentialScope = credentialScope,
            SeedSignature = seedSignature,
        };

        // Build properly signed chunked payload
        var chunked = BuildSignedChunkedPayload(payload, payload.Length, signingKey, amzDate, credentialScope, seedSignature);

        using var inputStream = new MemoryStream(chunked);
        using var decoder = new AwsChunkedDecoder(inputStream, signingContext);

        // Act
        using var output = new MemoryStream();
        await decoder.CopyToAsync(output);

        // Assert
        Assert.Equal(payload, output.ToArray());
    }

    [Fact]
    public async Task Throws_on_invalid_chunk_signature()
    {
        // Arrange
        var payload = "Test data"u8.ToArray();
        var signingKey = SigningKey.Derive("secret", "20260526", "us-east-1", "s3");

        var signingContext = new ChunkSigningContext
        {
            SigningKey = signingKey,
            AmzDate = "20260526T120000Z",
            CredentialScope = "20260526/us-east-1/s3/aws4_request",
            SeedSignature = "seed0000000000000000000000000000000000000000000000000000000000",
        };

        // Build with wrong signature
        var chunked = BuildChunkedPayload(payload, payload.Length, _ => "wrongsignature00000000000000000000000000000000000000000000000000");

        using var inputStream = new MemoryStream(chunked);
        using var decoder = new AwsChunkedDecoder(inputStream, signingContext);

        // Act & Assert
        using var output = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() => decoder.CopyToAsync(output));
    }

    [Fact]
    public async Task Handles_large_payload_in_multiple_chunks()
    {
        // Arrange: 1MB payload in 64KB chunks
        var payload = new byte[1024 * 1024];
        new Random(123).NextBytes(payload);
        var chunked = BuildChunkedPayload(payload, chunkSize: 64 * 1024, signatureFunc: _ => "sig");

        using var inputStream = new MemoryStream(chunked);
        using var decoder = new AwsChunkedDecoder(inputStream, signingContext: null);

        // Act
        using var output = new MemoryStream();
        await decoder.CopyToAsync(output);

        // Assert
        Assert.Equal(payload, output.ToArray());
    }

    [Fact]
    public async Task Decodes_unsigned_chunks_without_signature_extension()
    {
        // STREAMING-UNSIGNED-PAYLOAD: chunk headers carry no chunk-signature.
        var payload = new byte[250];
        new Random(7).NextBytes(payload);
        var chunked = BuildUnsignedChunkedPayload(payload, chunkSize: 64, trailer: null);

        using var inputStream = new MemoryStream(chunked);
        using var decoder = new AwsChunkedDecoder(inputStream, signingContext: null, leaveOpen: false, expectTrailer: false);

        using var output = new MemoryStream();
        await decoder.CopyToAsync(output);

        Assert.Equal(payload, output.ToArray());
    }

    [Fact]
    public async Task Decodes_unsigned_payload_trailer_variant()
    {
        // STREAMING-UNSIGNED-PAYLOAD-TRAILER: unsigned chunks + a trailing
        // checksum header section. The default for modern AWS SDKs over HTTPS.
        var payload = "the quick brown fox jumps over the lazy dog"u8.ToArray();
        var chunked = BuildUnsignedChunkedPayload(
            payload, chunkSize: 16, trailer: "x-amz-checksum-crc32:dummyCrc32Base64==");

        using var inputStream = new MemoryStream(chunked);
        using var decoder = new AwsChunkedDecoder(inputStream, signingContext: null, leaveOpen: false, expectTrailer: true);

        using var output = new MemoryStream();
        await decoder.CopyToAsync(output);

        Assert.Equal(payload, output.ToArray());
    }

    [Fact]
    public async Task Decodes_signed_payload_trailer_variant()
    {
        // STREAMING-AWS4-HMAC-SHA256-PAYLOAD-TRAILER: signed chunks followed by
        // a trailing checksum header and an x-amz-trailer-signature line. This
        // is what AWSSDK.S3 4.x emits by default (issue #258).
        var payload = "Test data for signed trailer"u8.ToArray();
        var signingKey = SigningKey.Derive("secret", "20260526", "us-east-1", "s3");
        var amzDate = "20260526T120000Z";
        var credentialScope = "20260526/us-east-1/s3/aws4_request";
        var seedSignature = "seed1234567890abcdef1234567890abcdef1234567890abcdef1234567890ab";

        var signingContext = new ChunkSigningContext
        {
            SigningKey = signingKey,
            AmzDate = amzDate,
            CredentialScope = credentialScope,
            SeedSignature = seedSignature,
        };

        var chunked = BuildSignedChunkedPayload(
            payload, payload.Length, signingKey, amzDate, credentialScope, seedSignature,
            trailer: "x-amz-checksum-crc32:dummyCrc32Base64==\r\nx-amz-trailer-signature:deadbeef");

        using var inputStream = new MemoryStream(chunked);
        using var decoder = new AwsChunkedDecoder(inputStream, signingContext, leaveOpen: false, expectTrailer: true);

        using var output = new MemoryStream();
        await decoder.CopyToAsync(output);

        Assert.Equal(payload, output.ToArray());
    }

    [Fact]
    public async Task Decodes_empty_payload_with_unsigned_trailer()
    {
        // 0\r\n<trailer>\r\n with no data chunks.
        var chunked = Encoding.ASCII.GetBytes("0\r\nx-amz-checksum-crc32:AAAAAA==\r\n\r\n");

        using var inputStream = new MemoryStream(chunked);
        using var decoder = new AwsChunkedDecoder(inputStream, signingContext: null, leaveOpen: false, expectTrailer: true);

        using var output = new MemoryStream();
        await decoder.CopyToAsync(output);

        Assert.Empty(output.ToArray());
    }

    private static byte[] BuildChunkedPayload(byte[] payload, int chunkSize, Func<byte[], string> signatureFunc)
    {
        using var ms = new MemoryStream();

        var offset = 0;
        while (offset < payload.Length)
        {
            var remaining = payload.Length - offset;
            var thisChunkSize = Math.Min(chunkSize, remaining);
            var chunkData = payload.AsSpan(offset, thisChunkSize).ToArray();

            // Write chunk header
            var header = $"{thisChunkSize:x};chunk-signature={signatureFunc(chunkData)}\r\n";
            ms.Write(Encoding.ASCII.GetBytes(header));

            // Write chunk data
            ms.Write(chunkData);

            // Write trailing CRLF
            ms.Write("\r\n"u8);

            offset += thisChunkSize;
        }

        // Final zero-size chunk
        var finalHeader = $"0;chunk-signature={signatureFunc([])}\r\n\r\n";
        ms.Write(Encoding.ASCII.GetBytes(finalHeader));

        return ms.ToArray();
    }

    private static byte[] BuildUnsignedChunkedPayload(byte[] payload, int chunkSize, string? trailer)
    {
        using var ms = new MemoryStream();

        var offset = 0;
        while (offset < payload.Length)
        {
            var thisChunkSize = Math.Min(chunkSize, payload.Length - offset);

            // Unsigned chunk header: just the hex size, no chunk-signature.
            ms.Write(Encoding.ASCII.GetBytes($"{thisChunkSize:x}\r\n"));
            ms.Write(payload.AsSpan(offset, thisChunkSize));
            ms.Write("\r\n"u8);

            offset += thisChunkSize;
        }

        // Final zero-size chunk.
        ms.Write("0\r\n"u8);
        if (trailer is not null)
        {
            ms.Write(Encoding.ASCII.GetBytes(trailer));
            ms.Write("\r\n"u8);
        }
        ms.Write("\r\n"u8); // blank line terminates the stream / trailer

        return ms.ToArray();
    }

    private static byte[] BuildSignedChunkedPayload(
        byte[] payload,
        int chunkSize,
        byte[] signingKey,
        string amzDate,
        string credentialScope,
        string seedSignature,
        string? trailer = null)
    {
        using var ms = new MemoryStream();
        var previousSignature = seedSignature;
        var emptySha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

        var offset = 0;
        while (offset < payload.Length)
        {
            var remaining = payload.Length - offset;
            var thisChunkSize = Math.Min(chunkSize, remaining);
            var chunkData = payload.AsSpan(offset, thisChunkSize).ToArray();

            // Calculate chunk signature
            var chunkHash = SigningKey.Sha256Hex(chunkData);
            var stringToSign = string.Join('\n',
                "AWS4-HMAC-SHA256-PAYLOAD",
                amzDate,
                credentialScope,
                previousSignature,
                emptySha256,
                chunkHash);
            var sigBytes = SigningKey.HmacSha256(signingKey, Encoding.UTF8.GetBytes(stringToSign));
            var signature = SigningKey.ToLowerHex(sigBytes);

            // Write chunk header
            var header = $"{thisChunkSize:x};chunk-signature={signature}\r\n";
            ms.Write(Encoding.ASCII.GetBytes(header));

            // Write chunk data
            ms.Write(chunkData);

            // Write trailing CRLF
            ms.Write("\r\n"u8);

            previousSignature = signature;
            offset += thisChunkSize;
        }

        // Final zero-size chunk with its own signature
        var finalChunkHash = SigningKey.Sha256Hex([]);
        var finalStringToSign = string.Join('\n',
            "AWS4-HMAC-SHA256-PAYLOAD",
            amzDate,
            credentialScope,
            previousSignature,
            emptySha256,
            finalChunkHash);
        var finalSigBytes = SigningKey.HmacSha256(signingKey, Encoding.UTF8.GetBytes(finalStringToSign));
        var finalSignature = SigningKey.ToLowerHex(finalSigBytes);

        ms.Write(Encoding.ASCII.GetBytes($"0;chunk-signature={finalSignature}\r\n"));
        if (trailer is not null)
        {
            ms.Write(Encoding.ASCII.GetBytes(trailer));
            ms.Write("\r\n"u8);
        }
        ms.Write("\r\n"u8); // blank line terminates the stream / trailer

        return ms.ToArray();
    }
}
