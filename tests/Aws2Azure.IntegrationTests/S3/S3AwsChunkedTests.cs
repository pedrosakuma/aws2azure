using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Aws2Azure.Core.SigV4;
using Aws2Azure.IntegrationTests.Fixtures;
using Xunit;

namespace Aws2Azure.IntegrationTests.S3;

/// <summary>
/// Integration tests for aws-chunked payload encoding (STREAMING-AWS4-HMAC-SHA256-PAYLOAD).
/// </summary>
[Collection(S3IntegrationCollection.Name)]
public class S3AwsChunkedTests
{
    private readonly S3IntegrationFixture _fx;
    public S3AwsChunkedTests(S3IntegrationFixture fx) => _fx = fx;

    [SkippableFact]
    public async Task PutObject_with_aws_chunked_single_chunk_round_trips()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping S3 integration test.");

        var bucket = "it-chunk-" + Guid.NewGuid().ToString("N")[..8];
        var key = "chunked-single.txt";
        var body = "Hello from aws-chunked single chunk!"u8.ToArray();

        await PutBucket(bucket);

        // Send with aws-chunked encoding
        using var putResp = await SendAwsChunkedAsync(bucket, key, body);
        Assert.True(putResp.IsSuccessStatusCode,
            $"PUT aws-chunked → {(int)putResp.StatusCode} {await putResp.Content.ReadAsStringAsync()}");
        Assert.NotNull(putResp.Headers.ETag);

        // GET and verify round-trip
        using var getResp = await GetObjectAsync(bucket, key);
        Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);
        var retrieved = await getResp.Content.ReadAsByteArrayAsync();
        Assert.Equal(body, retrieved);
    }

    [SkippableFact]
    public async Task PutObject_with_aws_chunked_multiple_chunks_round_trips()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping S3 integration test.");

        var bucket = "it-chunk-" + Guid.NewGuid().ToString("N")[..8];
        var key = "chunked-multi.bin";

        // Create body that will be split into multiple chunks
        var body = new byte[32 * 1024]; // 32KB
        Random.Shared.NextBytes(body);

        await PutBucket(bucket);

        // Send with aws-chunked encoding (8KB chunks)
        using var putResp = await SendAwsChunkedAsync(bucket, key, body, chunkSize: 8 * 1024);
        Assert.True(putResp.IsSuccessStatusCode,
            $"PUT aws-chunked multi → {(int)putResp.StatusCode} {await putResp.Content.ReadAsStringAsync()}");

        // GET and verify round-trip
        using var getResp = await GetObjectAsync(bucket, key);
        Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);
        var retrieved = await getResp.Content.ReadAsByteArrayAsync();
        Assert.Equal(body, retrieved);
    }

    [SkippableFact]
    public async Task PutObject_with_aws_chunked_empty_body_succeeds()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping S3 integration test.");

        var bucket = "it-chunk-" + Guid.NewGuid().ToString("N")[..8];
        var key = "chunked-empty.txt";

        await PutBucket(bucket);

        // Send empty body with aws-chunked
        using var putResp = await SendAwsChunkedAsync(bucket, key, Array.Empty<byte>());
        Assert.True(putResp.IsSuccessStatusCode,
            $"PUT aws-chunked empty → {(int)putResp.StatusCode} {await putResp.Content.ReadAsStringAsync()}");

        // GET and verify
        using var getResp = await GetObjectAsync(bucket, key);
        Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);
        var retrieved = await getResp.Content.ReadAsByteArrayAsync();
        Assert.Empty(retrieved);
    }

    [SkippableFact]
    public async Task PutObject_with_unsigned_chunked_trailer_round_trips()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping S3 integration test.");

        var bucket = "it-chunk-" + Guid.NewGuid().ToString("N")[..8];
        var key = "chunked-unsigned-trailer.bin";

        var body = new byte[20 * 1024];
        Random.Shared.NextBytes(body);

        await PutBucket(bucket);

        // STREAMING-UNSIGNED-PAYLOAD-TRAILER is the AWSSDK 4.x / modern boto3
        // default when flexible checksums are enabled over HTTPS.
        using var putResp = await SendUnsignedChunkedTrailerAsync(bucket, key, body, chunkSize: 8 * 1024);
        Assert.True(putResp.IsSuccessStatusCode,
            $"PUT unsigned-chunked-trailer → {(int)putResp.StatusCode} {await putResp.Content.ReadAsStringAsync()}");

        using var getResp = await GetObjectAsync(bucket, key);
        Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);
        var retrieved = await getResp.Content.ReadAsByteArrayAsync();
        Assert.Equal(body, retrieved);
    }

    private async Task<HttpResponseMessage> SendUnsignedChunkedTrailerAsync(
        string bucket,
        string key,
        byte[] body,
        int chunkSize = 64 * 1024)
    {
        var path = $"/{bucket}/{key}";
        var absolute = new Uri(_fx.Client.BaseAddress!, path);
        var req = new HttpRequestMessage(HttpMethod.Put, absolute);

        var stamp = DateTimeOffset.UtcNow.UtcDateTime;
        var amzDate = stamp.ToString(SigV4Constants.AmzDateFormat, CultureInfo.InvariantCulture);
        var shortDate = stamp.ToString(SigV4Constants.AmzShortDateFormat, CultureInfo.InvariantCulture);
        var region = "us-east-1";
        var service = "s3";
        var scope = $"{shortDate}/{region}/{service}/{SigV4Constants.TerminationString}";

        var payloadHash = "STREAMING-UNSIGNED-PAYLOAD-TRAILER";

        req.Headers.TryAddWithoutValidation(SigV4Constants.AmzDateHeader, amzDate);
        req.Headers.TryAddWithoutValidation(SigV4Constants.AmzContentSha256Header, payloadHash);
        req.Headers.TryAddWithoutValidation("x-amz-decoded-content-length", body.Length.ToString());
        req.Headers.TryAddWithoutValidation("x-amz-trailer", "x-amz-checksum-crc32");

        var headers = new List<KeyValuePair<string, string>>
        {
            new("host", absolute.Authority),
            new(SigV4Constants.AmzDateHeader, amzDate),
            new(SigV4Constants.AmzContentSha256Header, payloadHash),
            new("x-amz-decoded-content-length", body.Length.ToString()),
            new("x-amz-trailer", "x-amz-checksum-crc32"),
        };
        var signedHeaders = new[]
        {
            "host", SigV4Constants.AmzContentSha256Header, SigV4Constants.AmzDateHeader,
            "x-amz-decoded-content-length", "x-amz-trailer",
        };
        Array.Sort(signedHeaders, StringComparer.Ordinal);

        var canonical = CanonicalRequest.Build(
            "PUT",
            Uri.UnescapeDataString(absolute.AbsolutePath),
            "",
            headers,
            signedHeaders,
            payloadHash,
            s3PathStyle: true);

        var stringToSign = CanonicalRequest.StringToSign(amzDate, scope, canonical);
        var signingKey = SigningKey.Derive(_fx.Secret, shortDate, region, service);
        var sigBytes = HMACSHA256.HashData(signingKey, Encoding.UTF8.GetBytes(stringToSign));
        var seedSignature = SigningKey.ToLowerHex(sigBytes);

        var auth =
            $"{SigV4Constants.Algorithm} " +
            $"Credential={_fx.AccessKeyId}/{scope}, " +
            $"SignedHeaders={string.Join(';', signedHeaders)}, " +
            $"Signature={seedSignature}";
        req.Headers.TryAddWithoutValidation("Authorization", auth);

        var chunkedBody = BuildUnsignedChunkedTrailerBody(body, chunkSize);

        req.Content = new ByteArrayContent(chunkedBody);
        req.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/octet-stream");
        req.Content.Headers.TryAddWithoutValidation("Content-Encoding", "aws-chunked");
        req.Content.Headers.ContentLength = chunkedBody.Length;

        return await _fx.Client.SendAsync(req);
    }

    private static byte[] BuildUnsignedChunkedTrailerBody(byte[] data, int chunkSize)
    {
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, Encoding.ASCII, leaveOpen: true);
        writer.NewLine = "\r\n";

        var offset = 0;
        while (offset < data.Length)
        {
            var remaining = data.Length - offset;
            var thisChunkSize = Math.Min(remaining, chunkSize);

            // Unsigned chunk header: hex-size\r\n (no chunk-signature extension).
            writer.Write($"{thisChunkSize:x}");
            writer.WriteLine();
            writer.Flush();

            ms.Write(data.AsSpan(offset, thisChunkSize));

            writer.WriteLine();
            writer.Flush();
            offset += thisChunkSize;
        }

        // Final zero-size chunk followed by the trailer section terminated by a
        // blank line. The checksum value is not re-validated by the proxy.
        writer.Write("0");
        writer.WriteLine();
        writer.Write("x-amz-checksum-crc32:AAAAAA==");
        writer.WriteLine();
        writer.WriteLine(); // blank line ends the trailer
        writer.Flush();

        return ms.ToArray();
    }

    private async Task<HttpResponseMessage> SendAwsChunkedAsync(
        string bucket,
        string key,
        byte[] body,
        int chunkSize = 64 * 1024)
    {
        var path = $"/{bucket}/{key}";
        var absolute = new Uri(_fx.Client.BaseAddress!, path);
        var req = new HttpRequestMessage(HttpMethod.Put, absolute);

        var stamp = DateTimeOffset.UtcNow.UtcDateTime;
        var amzDate = stamp.ToString(SigV4Constants.AmzDateFormat, CultureInfo.InvariantCulture);
        var shortDate = stamp.ToString(SigV4Constants.AmzShortDateFormat, CultureInfo.InvariantCulture);
        var region = "us-east-1";
        var service = "s3";
        var scope = $"{shortDate}/{region}/{service}/{SigV4Constants.TerminationString}";

        // For streaming, payload hash is the literal string
        var payloadHash = "STREAMING-AWS4-HMAC-SHA256-PAYLOAD";

        req.Headers.TryAddWithoutValidation(SigV4Constants.AmzDateHeader, amzDate);
        req.Headers.TryAddWithoutValidation(SigV4Constants.AmzContentSha256Header, payloadHash);
        req.Headers.TryAddWithoutValidation("x-amz-decoded-content-length", body.Length.ToString());

        // Build and sign the seed signature
        var headers = new List<KeyValuePair<string, string>>
        {
            new("host", absolute.Authority),
            new(SigV4Constants.AmzDateHeader, amzDate),
            new(SigV4Constants.AmzContentSha256Header, payloadHash),
            new("x-amz-decoded-content-length", body.Length.ToString()),
        };
        var signedHeaders = new[] { "host", SigV4Constants.AmzContentSha256Header, SigV4Constants.AmzDateHeader, "x-amz-decoded-content-length" };
        Array.Sort(signedHeaders, StringComparer.Ordinal);

        var canonical = CanonicalRequest.Build(
            "PUT",
            Uri.UnescapeDataString(absolute.AbsolutePath),
            "",
            headers,
            signedHeaders,
            payloadHash,
            s3PathStyle: true);

        var stringToSign = CanonicalRequest.StringToSign(amzDate, scope, canonical);

        var signingKey = SigningKey.Derive(_fx.Secret, shortDate, region, service);
        var sigBytes = HMACSHA256.HashData(signingKey, Encoding.UTF8.GetBytes(stringToSign));
        var seedSignature = SigningKey.ToLowerHex(sigBytes);

        var auth =
            $"{SigV4Constants.Algorithm} " +
            $"Credential={_fx.AccessKeyId}/{scope}, " +
            $"SignedHeaders={string.Join(';', signedHeaders)}, " +
            $"Signature={seedSignature}";
        req.Headers.TryAddWithoutValidation("Authorization", auth);

        // Build aws-chunked body
        var chunkedBody = BuildAwsChunkedBody(body, chunkSize, signingKey, amzDate, scope, seedSignature);

        req.Content = new ByteArrayContent(chunkedBody);
        req.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/octet-stream");
        req.Content.Headers.TryAddWithoutValidation("Content-Encoding", "aws-chunked");
        req.Content.Headers.ContentLength = chunkedBody.Length;

        return await _fx.Client.SendAsync(req);
    }

    private static byte[] BuildAwsChunkedBody(
        byte[] data,
        int chunkSize,
        byte[] signingKey,
        string amzDate,
        string scope,
        string previousSignature)
    {
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, Encoding.ASCII, leaveOpen: true);
        writer.NewLine = "\r\n";

        var prevSig = previousSignature;
        var emptyHash = SigV4Constants.EmptyPayloadSha256;
        var offset = 0;

        while (offset < data.Length)
        {
            var remaining = data.Length - offset;
            var thisChunkSize = Math.Min(remaining, chunkSize);
            var chunkData = data.AsSpan(offset, thisChunkSize);

            // Compute chunk signature
            var chunkHash = SigningKey.Sha256Hex(chunkData.ToArray());
            var chunkStringToSign =
                $"AWS4-HMAC-SHA256-PAYLOAD\n{amzDate}\n{scope}\n{prevSig}\n{emptyHash}\n{chunkHash}";
            var chunkSigBytes = HMACSHA256.HashData(signingKey, Encoding.UTF8.GetBytes(chunkStringToSign));
            var chunkSig = SigningKey.ToLowerHex(chunkSigBytes);

            // Write chunk header: hex-size;chunk-signature=sig\r\n
            writer.Write($"{thisChunkSize:x};chunk-signature={chunkSig}");
            writer.WriteLine();
            writer.Flush();

            // Write chunk data
            ms.Write(chunkData);

            // Write trailing CRLF
            writer.WriteLine();
            writer.Flush();

            prevSig = chunkSig;
            offset += thisChunkSize;
        }

        // Final chunk (size 0)
        var finalHash = emptyHash;
        var finalStringToSign =
            $"AWS4-HMAC-SHA256-PAYLOAD\n{amzDate}\n{scope}\n{prevSig}\n{emptyHash}\n{finalHash}";
        var finalSigBytes = HMACSHA256.HashData(signingKey, Encoding.UTF8.GetBytes(finalStringToSign));
        var finalSig = SigningKey.ToLowerHex(finalSigBytes);

        writer.Write($"0;chunk-signature={finalSig}");
        writer.WriteLine();
        writer.WriteLine(); // empty final chunk body + trailing CRLF
        writer.Flush();

        return ms.ToArray();
    }

    private async Task PutBucket(string bucket)
    {
        var absolute = new Uri(_fx.Client.BaseAddress!, $"/{bucket}");
        var req = new HttpRequestMessage(HttpMethod.Put, absolute);
        req.Content = new ByteArrayContent(Array.Empty<byte>());
        req.Content.Headers.ContentLength = 0;
        TestSigV4Signer.SignHeader(req, Array.Empty<byte>(), _fx.AccessKeyId, _fx.Secret);
        using var resp = await _fx.Client.SendAsync(req);
        Assert.True(resp.IsSuccessStatusCode, $"PUT /{bucket} → {(int)resp.StatusCode}");
    }

    private async Task<HttpResponseMessage> GetObjectAsync(string bucket, string key)
    {
        var absolute = new Uri(_fx.Client.BaseAddress!, $"/{bucket}/{key}");
        var req = new HttpRequestMessage(HttpMethod.Get, absolute);
        TestSigV4Signer.SignHeader(req, Array.Empty<byte>(), _fx.AccessKeyId, _fx.Secret);
        return await _fx.Client.SendAsync(req);
    }
}
