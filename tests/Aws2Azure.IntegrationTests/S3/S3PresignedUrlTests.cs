using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Aws2Azure.Core.SigV4;
using Aws2Azure.IntegrationTests.Fixtures;
using Xunit;

namespace Aws2Azure.IntegrationTests.S3;

/// <summary>
/// Phase-7 issue #170: presigned URL support. The proxy validates the AWS
/// SigV4 presigned signature against its configured AWS credentials and
/// executes the operation against Azure Blob using the per-tenant Azure
/// credentials — no Azure SAS is generated or returned to the client.
/// </summary>
[Collection(S3IntegrationCollection.Name)]
public class S3PresignedUrlTests
{
    private readonly S3IntegrationFixture _fx;
    public S3PresignedUrlTests(S3IntegrationFixture fx) => _fx = fx;

    [SkippableFact]
    public async Task Presigned_get_put_head_delete_round_trip()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping S3 integration test.");

        var bucket = "it-" + Guid.NewGuid().ToString("N")[..10];
        var key = "presigned/object.txt";
        var body = Encoding.UTF8.GetBytes("presigned hello aws2azure");

        await CreateBucket(bucket);

        // Presigned PUT → upload object
        using (var resp = await SendPresignedAsync(HttpMethod.Put, $"/{bucket}/{key}",
                   TimeSpan.FromMinutes(5), body, contentType: "text/plain"))
        {
            Assert.True(resp.IsSuccessStatusCode,
                $"presigned PUT → {(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync()}");
        }

        // Presigned HEAD → metadata
        using (var resp = await SendPresignedAsync(HttpMethod.Head, $"/{bucket}/{key}",
                   TimeSpan.FromMinutes(5), Array.Empty<byte>()))
        {
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal(body.Length, resp.Content.Headers.ContentLength);
        }

        // Presigned GET → full body
        using (var resp = await SendPresignedAsync(HttpMethod.Get, $"/{bucket}/{key}",
                   TimeSpan.FromMinutes(5), Array.Empty<byte>()))
        {
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var bytes = await resp.Content.ReadAsByteArrayAsync();
            Assert.Equal(body, bytes);
        }

        // Presigned DELETE → 204
        using (var resp = await SendPresignedAsync(HttpMethod.Delete, $"/{bucket}/{key}",
                   TimeSpan.FromMinutes(5), Array.Empty<byte>()))
        {
            Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
        }
    }

    [SkippableFact]
    public async Task Presigned_get_honours_response_content_overrides()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping S3 integration test.");

        var bucket = "it-" + Guid.NewGuid().ToString("N")[..10];
        var key = "presigned/headers.txt";
        var body = Encoding.UTF8.GetBytes("override body");

        await CreateBucket(bucket);
        using (var put = await SendPresignedAsync(HttpMethod.Put, $"/{bucket}/{key}", TimeSpan.FromMinutes(5), body, contentType: "application/octet-stream"))
        {
            Assert.True(put.IsSuccessStatusCode, $"presigned PUT → {(int)put.StatusCode} {await put.Content.ReadAsStringAsync()}");
        }

        using var resp = await SendPresignedAsync(
            HttpMethod.Get,
            $"/{bucket}/{key}?response-content-type=text/plain&response-content-disposition=attachment%3B%20filename%3Ddownload.txt",
            TimeSpan.FromMinutes(5),
            Array.Empty<byte>());
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("text/plain", resp.Content.Headers.ContentType?.ToString());
        Assert.Equal("attachment; filename=download.txt", resp.Content.Headers.ContentDisposition?.ToString());
        Assert.Equal(body, await resp.Content.ReadAsByteArrayAsync());
    }

    [SkippableFact]
    public async Task Presigned_multipart_round_trip_succeeds()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping S3 integration test.");

        var bucket = "it-" + Guid.NewGuid().ToString("N")[..10];
        var key = "presigned/multipart.txt";
        var body = Encoding.UTF8.GetBytes("multipart via presign");

        await CreateBucket(bucket);

        string uploadId;
        using (var initiate = await SendPresignedAsync(HttpMethod.Post, $"/{bucket}/{key}?uploads", TimeSpan.FromMinutes(5), Array.Empty<byte>()))
        {
            Assert.Equal(HttpStatusCode.OK, initiate.StatusCode);
            uploadId = XDocument.Parse(await initiate.Content.ReadAsStringAsync()).Root!.Element(XName.Get("UploadId", "http://s3.amazonaws.com/doc/2006-03-01/"))!.Value;
        }

        string partEtag;
        using (var uploadPart = await SendPresignedAsync(HttpMethod.Put, $"/{bucket}/{key}?uploadId={Uri.EscapeDataString(uploadId)}&partNumber=1", TimeSpan.FromMinutes(5), body, contentType: "application/octet-stream"))
        {
            Assert.Equal(HttpStatusCode.OK, uploadPart.StatusCode);
            partEtag = uploadPart.Headers.ETag!.Tag;
        }

        var completeBody = Encoding.UTF8.GetBytes(
            $"<CompleteMultipartUpload><Part><PartNumber>1</PartNumber><ETag>{partEtag}</ETag></Part></CompleteMultipartUpload>");
        using (var complete = await SendPresignedAsync(HttpMethod.Post, $"/{bucket}/{key}?uploadId={Uri.EscapeDataString(uploadId)}", TimeSpan.FromMinutes(5), completeBody, contentType: "application/xml"))
        {
            Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        }

        using var get = await SendPresignedAsync(HttpMethod.Get, $"/{bucket}/{key}", TimeSpan.FromMinutes(5), Array.Empty<byte>());
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal(body, await get.Content.ReadAsByteArrayAsync());
    }

    [SkippableFact]
    public async Task Presigned_post_policy_upload_succeeds()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping S3 integration test.");

        var bucket = "it-" + Guid.NewGuid().ToString("N")[..10];
        await CreateBucket(bucket);

        var (requestBody, contentType) = BuildPresignedPostRequest(bucket, "browser-upload.txt", Encoding.UTF8.GetBytes("post body"));
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_fx.Client.BaseAddress!, $"/{bucket}"))
        {
            Content = new ByteArrayContent(requestBody),
        };
        request.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(contentType);
        request.Content.Headers.ContentLength = requestBody.Length;

        using (var post = await _fx.Client.SendAsync(request))
        {
            Assert.Equal(HttpStatusCode.NoContent, post.StatusCode);
        }

        var getUri = new Uri(_fx.Client.BaseAddress!, $"/{bucket}/browser-upload.txt");
        using var getRequest = new HttpRequestMessage(HttpMethod.Get, getUri);
        TestSigV4Signer.SignHeader(getRequest, Array.Empty<byte>(), _fx.AccessKeyId, _fx.Secret);
        using var get = await _fx.Client.SendAsync(getRequest);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal("post body", await get.Content.ReadAsStringAsync());
    }

    [SkippableFact]
    public async Task Presigned_url_past_expiry_is_rejected_with_access_denied()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping S3 integration test.");

        var bucket = "it-" + Guid.NewGuid().ToString("N")[..10];
        await CreateBucket(bucket);

        // Sign with a Date in the past and a 1-second expiry → already expired.
        var pastDate = DateTimeOffset.UtcNow.AddMinutes(-30);
        var uri = TestPresignedUrlBuilder.BuildPresignedUri(
            HttpMethod.Get, _fx.Client.BaseAddress!, $"/{bucket}/missing.txt",
            expiresIn: TimeSpan.FromSeconds(1),
            accessKey: _fx.AccessKeyId, secret: _fx.Secret, now: pastDate);

        using var resp = await _fx.Client.GetAsync(uri);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [SkippableFact]
    public async Task Presigned_url_with_tampered_query_param_is_rejected_with_signature_mismatch()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping S3 integration test.");

        var bucket = "it-" + Guid.NewGuid().ToString("N")[..10];
        await CreateBucket(bucket);

        var uri = TestPresignedUrlBuilder.BuildPresignedUri(
            HttpMethod.Get, _fx.Client.BaseAddress!, $"/{bucket}/missing.txt",
            expiresIn: TimeSpan.FromMinutes(5),
            accessKey: _fx.AccessKeyId, secret: _fx.Secret);

        // Flip a single character in X-Amz-Signature so the validator
        // rejects with SignatureDoesNotMatch.
        var tampered = uri.ToString();
        var sigIdx = tampered.IndexOf("X-Amz-Signature=", StringComparison.Ordinal);
        Assert.True(sigIdx > 0, "expected X-Amz-Signature in presigned URL");
        var charIdx = sigIdx + "X-Amz-Signature=".Length;
        var swapped = tampered[charIdx] == 'a' ? 'b' : 'a';
        tampered = tampered[..charIdx] + swapped + tampered[(charIdx + 1)..];

        using var resp = await _fx.Client.GetAsync(new Uri(tampered));
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    private async Task CreateBucket(string bucket)
    {
        var absolute = new Uri(_fx.Client.BaseAddress!, $"/{bucket}");
        using var req = new HttpRequestMessage(HttpMethod.Put, absolute);
        TestSigV4Signer.SignHeader(req, Array.Empty<byte>(), _fx.AccessKeyId, _fx.Secret);
        using var resp = await _fx.Client.SendAsync(req);
        Assert.True(resp.IsSuccessStatusCode,
            $"create bucket → {(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync()}");
    }

    private async Task<HttpResponseMessage> SendPresignedAsync(
        HttpMethod method,
        string pathAndQuery,
        TimeSpan expiresIn,
        byte[] body,
        string? contentType = null)
    {
        var uri = TestPresignedUrlBuilder.BuildPresignedUri(
            method, _fx.Client.BaseAddress!, pathAndQuery, expiresIn,
            _fx.AccessKeyId, _fx.Secret);

        var req = new HttpRequestMessage(method, uri);
        if (body.Length > 0 || method == HttpMethod.Put || method == HttpMethod.Post)
        {
            req.Content = new ByteArrayContent(body);
            req.Content.Headers.ContentLength = body.Length;
            if (contentType is not null)
            {
                req.Content.Headers.ContentType =
                    System.Net.Http.Headers.MediaTypeHeaderValue.Parse(contentType);
            }
        }
        return await _fx.Client.SendAsync(req);
    }

    private (byte[] Body, string ContentType) BuildPresignedPostRequest(string bucket, string key, byte[] fileBytes)
    {
        const string boundary = "----aws2azure-int-boundary";
        var now = DateTimeOffset.UtcNow;
        var shortDate = now.UtcDateTime.ToString(SigV4Constants.AmzShortDateFormat, System.Globalization.CultureInfo.InvariantCulture);
        var amzDate = now.UtcDateTime.ToString(SigV4Constants.AmzDateFormat, System.Globalization.CultureInfo.InvariantCulture);
        var credential = $"{_fx.AccessKeyId}/{shortDate}/us-east-1/s3/aws4_request";
        var expiration = now.AddMinutes(5).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
        var policyJson = "{\"expiration\":\"" + expiration + "\",\"conditions\":[" +
                         "{\"bucket\":\"" + bucket + "\"}," +
                         "{\"key\":\"" + key + "\"}," +
                         "{\"x-amz-algorithm\":\"AWS4-HMAC-SHA256\"}," +
                         "{\"x-amz-credential\":\"" + credential + "\"}," +
                         "{\"x-amz-date\":\"" + amzDate + "\"}" +
                         "]}";
        var policyBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(policyJson));
        var signingKey = SigningKey.Derive(_fx.Secret, shortDate, "us-east-1", "s3");
        var signature = SigningKey.ToLowerHex(HMACSHA256.HashData(signingKey, Encoding.UTF8.GetBytes(policyBase64)));

        var fields = new (string Name, string Value)[]
        {
            ("key", key),
            ("policy", policyBase64),
            ("x-amz-algorithm", "AWS4-HMAC-SHA256"),
            ("x-amz-credential", credential),
            ("x-amz-date", amzDate),
            ("x-amz-signature", signature),
        };

        using var ms = new MemoryStream();
        foreach (var (name, value) in fields)
        {
            WriteFormPart(ms, boundary, name, value);
        }
        WriteFilePart(ms, boundary, "file", "upload.bin", "application/octet-stream", fileBytes);
        ms.Write(Encoding.UTF8.GetBytes($"--{boundary}--\r\n"));
        return (ms.ToArray(), $"multipart/form-data; boundary={boundary}");
    }

    private static void WriteFormPart(Stream stream, string boundary, string name, string value)
    {
        stream.Write(Encoding.UTF8.GetBytes($"--{boundary}\r\nContent-Disposition: form-data; name=\"{name}\"\r\n\r\n{value}\r\n"));
    }

    private static void WriteFilePart(Stream stream, string boundary, string name, string fileName, string contentType, byte[] bytes)
    {
        stream.Write(Encoding.UTF8.GetBytes(
            $"--{boundary}\r\nContent-Disposition: form-data; name=\"{name}\"; filename=\"{fileName}\"\r\nContent-Type: {contentType}\r\n\r\n"));
        stream.Write(bytes);
        stream.Write(Encoding.UTF8.GetBytes("\r\n"));
    }
}
