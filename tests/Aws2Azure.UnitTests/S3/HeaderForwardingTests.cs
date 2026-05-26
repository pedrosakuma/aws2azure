using Aws2Azure.Modules.S3.Internal;
using Xunit;

namespace Aws2Azure.UnitTests.S3;

public class HeaderForwardingTests
{
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
}
