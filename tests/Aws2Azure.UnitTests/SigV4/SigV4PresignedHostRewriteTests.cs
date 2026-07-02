using System.Text;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Core.SigV4;

namespace Aws2Azure.UnitTests.SigV4;

/// <summary>
/// Tests for the opt-in presigned-URL host-rewrite fallback: a presigned URL
/// signed against an AWS S3 endpoint host whose host was later rewritten to the
/// proxy (path-style) is accepted only when the origin host is on the trusted
/// allowlist.
/// </summary>
public class SigV4PresignedHostRewriteTests
{
    private const string AccessKeyId = "AKIDEXAMPLE";
    private const string SecretKey   = "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY";
    private const string Region      = "us-east-1";
    private const string Service     = "s3";
    private const string AmzDate     = "20150830T123600Z";
    private const string ShortDate   = "20150830";
    private const string ProxyHost   = "proxy.internal:8080";
    private const string AwsHost     = "s3.us-east-1.amazonaws.com";

    private static ICredentialResolver Resolver() =>
        new StaticCredentialResolver(new ProxyConfig
        {
            Credentials =
            {
                new CredentialEntry
                {
                    AwsAccessKeyId = AccessKeyId,
                    AwsSecretAccessKey = SecretKey,
                    Azure = new AzureCredentials(),
                },
            },
        });

    /// <summary>
    /// Produces the presigned <c>X-Amz-Signature</c> that a client would generate
    /// signing against <paramref name="signingHost"/> with the canonical path
    /// <paramref name="signedPath"/>.
    /// </summary>
    private static string SignPresigned(string signingHost, string signedPath, string query)
    {
        var headers = new List<KeyValuePair<string, string>> { new("Host", signingHost) };
        var canonical = CanonicalRequest.Build("GET", signedPath, query, headers, ["host"],
            SigV4Constants.UnsignedPayload, s3PathStyle: true);
        var sts = CanonicalRequest.StringToSign(AmzDate, $"{ShortDate}/{Region}/{Service}/aws4_request", canonical);
        var signingKey = SigningKey.Derive(SecretKey, ShortDate, Region, Service);
        return SigningKey.ToLowerHex(SigningKey.HmacSha256(signingKey, Encoding.UTF8.GetBytes(sts)));
    }

    private static string CanonicalQueryForSigning()
    {
        var credential = $"{AccessKeyId}/{ShortDate}/{Region}/{Service}/aws4_request";
        return
            "X-Amz-Algorithm=AWS4-HMAC-SHA256&" +
            $"X-Amz-Credential={Uri.EscapeDataString(credential)}&" +
            $"X-Amz-Date={AmzDate}&" +
            "X-Amz-Expires=900&" +
            "X-Amz-SignedHeaders=host";
    }

    private static SigV4Request ProxyRequest(string proxyPath, string signedQuery, string signature) =>
        new()
        {
            HttpMethod = "GET",
            RawPath = proxyPath,
            RawQueryString = signedQuery + "&X-Amz-Signature=" + signature,
            Headers = new List<KeyValuePair<string, string>> { new("Host", ProxyHost) },
            PayloadHash = SigV4Constants.UnsignedPayload,
            S3PathStyle = true,
            Now = SigningKey.TryParseAmzDate(AmzDate, out var t) ? t : DateTimeOffset.UtcNow,
        };

    [Fact]
    public void PathStyle_origin_signed_against_aws_host_is_accepted_when_host_is_trusted()
    {
        // Origin: s3.us-east-1.amazonaws.com/bucket/key ; proxy receives /bucket/key.
        var query = CanonicalQueryForSigning();
        var sig = SignPresigned(AwsHost, "/bucket/key", query);

        var validator = new SigV4Validator(Resolver(), presignedTrustedSigningHosts: [AwsHost]);
        var result = validator.Validate(ProxyRequest("/bucket/key", query, sig));

        Assert.True(result.IsValid, result.Reason);
    }

    [Fact]
    public void VirtualHosted_origin_signed_against_aws_host_is_accepted_when_host_is_trusted()
    {
        // Origin: bucket.s3.us-east-1.amazonaws.com/key (path "/key") ; proxy
        // receives path-style /bucket/key.
        var query = CanonicalQueryForSigning();
        var sig = SignPresigned($"bucket.{AwsHost}", "/key", query);

        var validator = new SigV4Validator(Resolver(), presignedTrustedSigningHosts: [AwsHost]);
        var result = validator.Validate(ProxyRequest("/bucket/key", query, sig));

        Assert.True(result.IsValid, result.Reason);
    }

    [Fact]
    public void Rewritten_presigned_url_is_rejected_when_rewrite_mode_is_disabled()
    {
        var query = CanonicalQueryForSigning();
        var sig = SignPresigned(AwsHost, "/bucket/key", query);

        // No trusted hosts configured (default) → strict host binding.
        var validator = new SigV4Validator(Resolver());
        var result = validator.Validate(ProxyRequest("/bucket/key", query, sig));

        Assert.Equal(SigV4ValidationStatus.InvalidSignature, result.Status);
    }

    [Fact]
    public void Rewritten_presigned_url_is_rejected_when_origin_host_is_not_trusted()
    {
        var query = CanonicalQueryForSigning();
        var sig = SignPresigned(AwsHost, "/bucket/key", query);

        // A different host is trusted; the actual origin host is not.
        var validator = new SigV4Validator(Resolver(), presignedTrustedSigningHosts: ["s3.eu-west-1.amazonaws.com"]);
        var result = validator.Validate(ProxyRequest("/bucket/key", query, sig));

        Assert.Equal(SigV4ValidationStatus.InvalidSignature, result.Status);
    }

    [Fact]
    public void Tampered_signature_is_rejected_even_with_rewrite_mode_enabled()
    {
        var query = CanonicalQueryForSigning();
        var sig = SignPresigned(AwsHost, "/bucket/key", query);
        var tampered = sig[..^1] + (sig[^1] == 'a' ? 'b' : 'a');

        var validator = new SigV4Validator(Resolver(), presignedTrustedSigningHosts: [AwsHost]);
        var result = validator.Validate(ProxyRequest("/bucket/key", query, tampered));

        Assert.Equal(SigV4ValidationStatus.InvalidSignature, result.Status);
    }

    [Fact]
    public void Native_presigned_url_signed_against_proxy_host_still_validates_with_rewrite_enabled()
    {
        // Signed directly against the proxy host — must keep working (no regression)
        // even when rewrite hosts are configured.
        var query = CanonicalQueryForSigning();
        var sig = SignPresigned(ProxyHost, "/bucket/key", query);

        var validator = new SigV4Validator(Resolver(), presignedTrustedSigningHosts: [AwsHost]);
        var result = validator.Validate(ProxyRequest("/bucket/key", query, sig));

        Assert.True(result.IsValid, result.Reason);
    }

    [Fact]
    public void Case_insensitive_trusted_host_is_normalized()
    {
        var query = CanonicalQueryForSigning();
        var sig = SignPresigned(AwsHost, "/bucket/key", query);

        var validator = new SigV4Validator(Resolver(), presignedTrustedSigningHosts: ["S3.US-EAST-1.AMAZONAWS.COM"]);
        var result = validator.Validate(ProxyRequest("/bucket/key", query, sig));

        Assert.True(result.IsValid, result.Reason);
    }

    [Theory]
    [InlineData("/bucket/key", "bucket", "/key")]
    [InlineData("/bucket/dir/obj.txt", "bucket", "/dir/obj.txt")]
    [InlineData("/key", "", "/key")]
    [InlineData("/", "", "/")]
    [InlineData("", "", "")]
    [InlineData("//key", "", "//key")]
    public void SplitLeadingSegment_parses_bucket_and_remainder(string path, string expectedBucket, string expectedRemainder)
    {
        SigV4Validator.SplitLeadingSegment(path, out var bucket, out var remainder);
        Assert.Equal(expectedBucket, bucket);
        Assert.Equal(expectedRemainder, remainder);
    }
}
