using Aws2Azure.Modules.S3.Internal;
using Xunit;

namespace Aws2Azure.UnitTests.S3;

public class HeaderForwardingTests
{
    [Fact]
    public void CopyFromAzureResponse_maps_version_id_to_x_amz_version_id()
    {
        using var azure = new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK);
        azure.Headers.TryAddWithoutValidation("x-ms-version-id", "2024-05-06T07:08:09.0000000Z");
        var target = new Microsoft.AspNetCore.Http.DefaultHttpContext().Response;

        HeaderForwarding.CopyFromAzureResponse(azure, target);

        Assert.Equal(S3VersionIdCodec.Encode("2024-05-06T07:08:09.0000000Z"), target.Headers["x-amz-version-id"]);
    }

    [Fact]
    public void ApplyCommonS3ResponseHeaders_sets_only_request_id_by_default()
    {
        var ctx = new Microsoft.AspNetCore.Http.DefaultHttpContext { TraceIdentifier = "trace-123" };

        HeaderForwarding.ApplyCommonS3ResponseHeaders(ctx.Response);

        Assert.Equal("trace-123", ctx.Response.Headers["x-amz-request-id"]);
        Assert.False(ctx.Response.Headers.ContainsKey("x-amz-bucket-region"));
        Assert.False(ctx.Response.Headers.ContainsKey("x-amz-bucket-arn"));
    }

    [Fact]
    public void ApplyBucketResponseHeaders_sets_bucket_headers()
    {
        var ctx = new Microsoft.AspNetCore.Http.DefaultHttpContext();

        HeaderForwarding.ApplyBucketResponseHeaders(ctx.Response, "bucket-a");

        Assert.Equal("us-east-1", ctx.Response.Headers["x-amz-bucket-region"]);
        Assert.Equal("arn:aws:s3:::bucket-a", ctx.Response.Headers["x-amz-bucket-arn"]);
    }

    [Fact]
    public void ApplyBucketResponseHeaders_can_skip_region_or_arn_independently()
    {
        var ctx = new Microsoft.AspNetCore.Http.DefaultHttpContext();

        HeaderForwarding.ApplyBucketResponseHeaders(ctx.Response, "bucket-a", includeRegion: false, includeArn: true);

        Assert.False(ctx.Response.Headers.ContainsKey("x-amz-bucket-region"));
        Assert.Equal("arn:aws:s3:::bucket-a", ctx.Response.Headers["x-amz-bucket-arn"]);
    }

    [Fact]
    public void ApplyObjectResponseHeaders_sets_requested_object_headers_only()
    {
        var target = new Microsoft.AspNetCore.Http.DefaultHttpContext().Response;

        HeaderForwarding.ApplyObjectResponseHeaders(
            target,
            defaultContentType: true,
            serverSideEncryption: true,
            checksumType: true);

        Assert.Equal("binary/octet-stream", target.ContentType);
        Assert.Equal("AES256", target.Headers["x-amz-server-side-encryption"]);
        Assert.Equal("FULL_OBJECT", target.Headers["x-amz-checksum-type"]);
    }

    [Fact]
    public void ApplyObjectResponseHeaders_does_not_force_headers_when_not_requested()
    {
        var target = new Microsoft.AspNetCore.Http.DefaultHttpContext().Response;

        HeaderForwarding.ApplyObjectResponseHeaders(target);

        Assert.False(target.Headers.ContainsKey("x-amz-server-side-encryption"));
        Assert.False(target.Headers.ContainsKey("x-amz-checksum-type"));
        Assert.False(target.Headers.ContainsKey("Content-Type"));
    }

    [Fact]
    public void ApplyObjectResponseHeaders_rewrites_azure_default_octet_stream_to_s3_default()
    {
        var target = new Microsoft.AspNetCore.Http.DefaultHttpContext().Response;
        target.ContentType = "application/octet-stream";

        HeaderForwarding.ApplyObjectResponseHeaders(target, defaultContentType: true);

        Assert.Equal("binary/octet-stream", target.ContentType);
    }

    [Fact]
    public void TranslateAzureEtagToS3_uses_content_md5_when_present()
    {
        // 16-byte MD5 of empty string = d41d8cd98f00b204e9800998ecf8427e.
        const string base64 = "1B2M2Y8AsgTpgAmY7PhCfg==";
        var s3Etag = HeaderForwarding.TranslateAzureEtagToS3("\"0x8DCC8B5F1A2B6C0\"", base64);
        Assert.Equal("d41d8cd98f00b204e9800998ecf8427e", s3Etag);
        Assert.Equal(32, s3Etag.Length);
    }

    [Fact]
    public void TranslateAzureEtagToS3_falls_back_to_synthetic_hex_when_md5_missing()
    {
        var s3Etag = HeaderForwarding.TranslateAzureEtagToS3("\"0x8DCC8B5F1A2B6C0\"", contentMd5Base64: null);

        // Must be hex-parseable by the AWS SDK and exactly 32 chars long
        // (otherwise AmazonS3ResponseHandler.HexStringToBytes throws on GET).
        Assert.Equal(32, s3Etag.Length);
        foreach (var ch in s3Etag)
        {
            Assert.True((ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f'), $"non-hex char in S3 ETag: {ch}");
        }
    }

    [Fact]
    public void TranslateAzureEtagToS3_is_deterministic_for_same_input()
    {
        var a = HeaderForwarding.TranslateAzureEtagToS3("\"0xABCDEF\"", null);
        var b = HeaderForwarding.TranslateAzureEtagToS3("\"0xABCDEF\"", null);
        Assert.Equal(a, b);
    }

    [Fact]
    public void TranslateAzureEtagToS3_ignores_invalid_content_md5_and_falls_back()
    {
        var s3Etag = HeaderForwarding.TranslateAzureEtagToS3("\"0x123\"", contentMd5Base64: "not-valid-base64!!");
        Assert.Equal(32, s3Etag.Length);
    }

    [Fact]
    public void TranslateAzureEtagToS3_ignores_content_md5_with_wrong_length()
    {
        // 8 bytes base64-encoded — not an MD5.
        var notMd5 = Convert.ToBase64String(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        var s3Etag = HeaderForwarding.TranslateAzureEtagToS3("\"0xFFFF\"", contentMd5Base64: notMd5);
        // Synthetic-of-azure-etag, not the (rejected) base64 input.
        var expected = HeaderForwarding.TranslateAzureEtagToS3("\"0xFFFF\"", null);
        Assert.Equal(expected, s3Etag);
    }

    [Fact]
    public void ForwardMetadata_strips_reserved_internal_multipart_marker()
    {
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        context.Request.Headers["x-amz-meta-" + HeaderForwarding.InternalMultipartPartCountMetadataName] = "7";
        context.Request.Headers["x-amz-meta-user-visible"] = "yes";
        using var target = new System.Net.Http.HttpRequestMessage();

        HeaderForwarding.ForwardMetadata(context.Request, target);

        Assert.False(target.Headers.Contains("x-ms-meta-" + HeaderForwarding.InternalMultipartPartCountMetadataName));
        Assert.True(target.Headers.TryGetValues("x-ms-meta-user-visible", out var values));
        Assert.Equal("yes", Assert.Single(values));
    }

    [Fact]
    public void CopyFromAzureResponse_preserves_single_part_multipart_etag_shape_and_hides_reserved_metadata()
    {
        using var azure = new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new System.Net.Http.ByteArrayContent("multipart-body"u8.ToArray()),
        };
        azure.Headers.TryAddWithoutValidation("ETag", "\"0x8DCC8B5F1A2B6C0\"");
        azure.Headers.TryAddWithoutValidation("x-ms-meta-" + HeaderForwarding.InternalMultipartPartCountMetadataName, "1");
        var target = new Microsoft.AspNetCore.Http.DefaultHttpContext().Response;

        HeaderForwarding.CopyFromAzureResponse(azure, target);

        Assert.EndsWith("-1\"", target.Headers["ETag"].ToString(), StringComparison.Ordinal);
        Assert.False(target.Headers.ContainsKey("x-amz-meta-" + HeaderForwarding.InternalMultipartPartCountMetadataName));
    }

    [Fact]
    public void CopyFromAzureResponse_uses_internal_multipart_marker_for_etag_and_hides_reserved_metadata()
    {
        using var azure = new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new System.Net.Http.ByteArrayContent("multipart-body"u8.ToArray()),
        };
        azure.Headers.TryAddWithoutValidation("ETag", "\"0x8DCC8B5F1A2B6C0\"");
        azure.Headers.TryAddWithoutValidation("x-ms-meta-" + HeaderForwarding.InternalMultipartPartCountMetadataName, "2");
        azure.Headers.TryAddWithoutValidation("x-ms-meta-user-visible", "yes");
        azure.Content.Headers.TryAddWithoutValidation("Content-MD5", "1B2M2Y8AsgTpgAmY7PhCfg==");
        var target = new Microsoft.AspNetCore.Http.DefaultHttpContext().Response;

        HeaderForwarding.CopyFromAzureResponse(azure, target);

        Assert.EndsWith("-2\"", target.Headers["ETag"].ToString(), StringComparison.Ordinal);
        Assert.Equal("yes", target.Headers["x-amz-meta-user-visible"]);
        Assert.False(target.Headers.ContainsKey("x-amz-meta-" + HeaderForwarding.InternalMultipartPartCountMetadataName));
    }

    [Fact]
    public void CopyFromAzureResponse_prefers_persisted_blob_md5_for_content_md5_and_etag()
    {
        using var azure = new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new System.Net.Http.ByteArrayContent("body"u8.ToArray()),
        };
        azure.Headers.TryAddWithoutValidation("ETag", "\"0x8DCC8B5F1A2B6C0\"");
        azure.Headers.TryAddWithoutValidation("x-ms-blob-content-md5", "1B2M2Y8AsgTpgAmY7PhCfg==");
        azure.Content.Headers.TryAddWithoutValidation("Content-MD5", "kAFQmDzST7DWlj99KOF/cg==");
        var target = new Microsoft.AspNetCore.Http.DefaultHttpContext().Response;

        HeaderForwarding.CopyFromAzureResponse(azure, target);

        Assert.Equal("1B2M2Y8AsgTpgAmY7PhCfg==", target.Headers["Content-MD5"]);
        Assert.Equal("\"d41d8cd98f00b204e9800998ecf8427e\"", target.Headers["ETag"]);
    }

    // --- EvaluateEtagConditionals ---

    private static Microsoft.AspNetCore.Http.HttpRequest MakeRequest(params (string, string)[] headers)
    {
        var ctx = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        foreach (var (k, v) in headers)
        {
            ctx.Request.Headers[k] = v;
        }
        return ctx.Request;
    }

    [Fact]
    public void EvaluateEtagConditionals_returns_304_when_if_none_match_matches_get()
    {
        var req = MakeRequest(("If-None-Match", "\"d41d8cd98f00b204e9800998ecf8427e\""));
        var result = HeaderForwarding.EvaluateEtagConditionals(req, "\"d41d8cd98f00b204e9800998ecf8427e\"", isReadOperation: true);
        Assert.Equal(304, result);
    }

    [Fact]
    public void EvaluateEtagConditionals_returns_412_when_if_none_match_matches_write()
    {
        var req = MakeRequest(("If-None-Match", "\"d41d8cd98f00b204e9800998ecf8427e\""));
        var result = HeaderForwarding.EvaluateEtagConditionals(req, "\"d41d8cd98f00b204e9800998ecf8427e\"", isReadOperation: false);
        Assert.Equal(412, result);
    }

    [Fact]
    public void EvaluateEtagConditionals_returns_null_when_if_none_match_does_not_match()
    {
        var req = MakeRequest(("If-None-Match", "\"deadbeef\""));
        var result = HeaderForwarding.EvaluateEtagConditionals(req, "\"d41d8cd98f00b204e9800998ecf8427e\"", isReadOperation: true);
        Assert.Null(result);
    }

    [Fact]
    public void EvaluateEtagConditionals_returns_412_when_if_match_does_not_match()
    {
        var req = MakeRequest(("If-Match", "\"deadbeef\""));
        var result = HeaderForwarding.EvaluateEtagConditionals(req, "\"d41d8cd98f00b204e9800998ecf8427e\"", isReadOperation: true);
        Assert.Equal(412, result);
    }

    [Fact]
    public void EvaluateEtagConditionals_passes_when_if_match_matches()
    {
        var req = MakeRequest(("If-Match", "\"d41d8cd98f00b204e9800998ecf8427e\""));
        var result = HeaderForwarding.EvaluateEtagConditionals(req, "\"d41d8cd98f00b204e9800998ecf8427e\"", isReadOperation: true);
        Assert.Null(result);
    }

    [Fact]
    public void EvaluateEtagConditionals_passes_when_if_match_star_matches_any()
    {
        var req = MakeRequest(("If-Match", "*"));
        var result = HeaderForwarding.EvaluateEtagConditionals(req, "\"d41d8cd98f00b204e9800998ecf8427e\"", isReadOperation: true);
        Assert.Null(result);
    }

    [Fact]
    public void EvaluateEtagConditionals_handles_comma_separated_list()
    {
        var req = MakeRequest(("If-None-Match", "\"deadbeef\", \"d41d8cd98f00b204e9800998ecf8427e\", \"feedface\""));
        var result = HeaderForwarding.EvaluateEtagConditionals(req, "\"d41d8cd98f00b204e9800998ecf8427e\"", isReadOperation: true);
        Assert.Equal(304, result);
    }

    [Fact]
    public void EvaluateEtagConditionals_treats_weak_validator_as_strong()
    {
        var req = MakeRequest(("If-None-Match", "W/\"d41d8cd98f00b204e9800998ecf8427e\""));
        var result = HeaderForwarding.EvaluateEtagConditionals(req, "\"d41d8cd98f00b204e9800998ecf8427e\"", isReadOperation: true);
        Assert.Equal(304, result);
    }

    [Fact]
    public void EvaluateEtagConditionals_returns_null_when_no_conditionals_present()
    {
        var req = MakeRequest();
        var result = HeaderForwarding.EvaluateEtagConditionals(req, "\"d41d8cd98f00b204e9800998ecf8427e\"", isReadOperation: true);
        Assert.Null(result);
    }
}
