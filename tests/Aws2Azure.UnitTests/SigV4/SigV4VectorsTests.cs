using System.Text;
using Aws2Azure.Core.SigV4;

namespace Aws2Azure.UnitTests.SigV4;

/// <summary>
/// Canonical-request, string-to-sign, signing-key, and signature checks
/// against the published "get-vanilla" example in the AWS SigV4 docs.
/// </summary>
public class SigV4VectorsTests
{
    // Test vector reproduced from
    // https://docs.aws.amazon.com/IAM/UserGuide/signing-elements.html
    // (AWS-published example test suite).
    private const string AccessKeyId = "AKIDEXAMPLE";
    private const string SecretKey   = "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY";
    private const string Region      = "us-east-1";
    private const string Service     = "service";
    private const string AmzDate     = "20150830T123600Z";
    private const string ShortDate   = "20150830";

    private static IReadOnlyList<KeyValuePair<string, string>> GetVanillaHeaders() => new[]
    {
        new KeyValuePair<string, string>("Host", "example.amazonaws.com"),
        new KeyValuePair<string, string>("X-Amz-Date", AmzDate),
    };

    private const string ExpectedCanonicalRequest =
        "GET\n" +
        "/\n" +
        "\n" +
        "host:example.amazonaws.com\n" +
        "x-amz-date:" + AmzDate + "\n" +
        "\n" +
        "host;x-amz-date\n" +
        "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    private const string ExpectedStringToSign =
        "AWS4-HMAC-SHA256\n" +
        AmzDate + "\n" +
        ShortDate + "/" + Region + "/" + Service + "/aws4_request\n" +
        "bb579772317eb040ac9ed261061d46c1f17a8133879d6129b6e1c25292927e63";

    private const string ExpectedSignature =
        "5fa00fa31553b73ebf1942676e86291e8372ff2a2260956d9b8aae1d763fbf31";

    [Fact]
    public void Get_vanilla_canonical_request()
    {
        var canonical = CanonicalRequest.Build(
            httpMethod: "GET",
            rawPath: "/",
            rawQueryString: string.Empty,
            headers: GetVanillaHeaders(),
            signedHeaders: ["host", "x-amz-date"],
            payloadHash: SigV4Constants.EmptyPayloadSha256,
            s3PathStyle: true);

        Assert.Equal(ExpectedCanonicalRequest, canonical);
    }

    [Fact]
    public void Get_vanilla_string_to_sign()
    {
        var canonical = CanonicalRequest.Build("GET", "/", string.Empty,
            GetVanillaHeaders(), ["host", "x-amz-date"], SigV4Constants.EmptyPayloadSha256, true);

        var sts = CanonicalRequest.StringToSign(AmzDate, $"{ShortDate}/{Region}/{Service}/aws4_request", canonical);
        Assert.Equal(ExpectedStringToSign, sts);
    }

    [Fact]
    public void Get_vanilla_signing_key_chain_produces_32_byte_key()
    {
        var key = SigningKey.Derive(SecretKey, ShortDate, Region, Service);
        // Correctness of the derivation is verified end-to-end by
        // Get_vanilla_signature; here we just guard the shape.
        Assert.Equal(32, key.Length);
        Assert.Equal(64, SigningKey.ToLowerHex(key).Length);
    }

    [Fact]
    public void Get_vanilla_signature()
    {
        var canonical = CanonicalRequest.Build("GET", "/", string.Empty,
            GetVanillaHeaders(), ["host", "x-amz-date"], SigV4Constants.EmptyPayloadSha256, true);

        var sts = CanonicalRequest.StringToSign(AmzDate, $"{ShortDate}/{Region}/{Service}/aws4_request", canonical);
        var signingKey = SigningKey.Derive(SecretKey, ShortDate, Region, Service);
        var sig = SigningKey.HmacSha256(signingKey, Encoding.UTF8.GetBytes(sts));

        Assert.Equal(ExpectedSignature, SigningKey.ToLowerHex(sig));
    }
}
