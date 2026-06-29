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

        Assert.Equal("2024-05-06T07:08:09.0000000Z", target.Headers["x-amz-version-id"]);
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
