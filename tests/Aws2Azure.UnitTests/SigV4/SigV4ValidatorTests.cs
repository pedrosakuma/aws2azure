using Aws2Azure.Core.Configuration;
using Aws2Azure.Core.SigV4;

namespace Aws2Azure.UnitTests.SigV4;

public class SigV4ValidatorTests
{
    private const string AccessKeyId = "AKIDEXAMPLE";
    private const string SecretKey   = "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY";
    private const string Region      = "us-east-1";
    private const string Service     = "service";
    private const string AmzDate     = "20150830T123600Z";
    private const string ShortDate   = "20150830";

    private const string GetVanillaAuthorization =
        "AWS4-HMAC-SHA256 " +
        "Credential=AKIDEXAMPLE/20150830/us-east-1/service/aws4_request, " +
        "SignedHeaders=host;x-amz-date, " +
        "Signature=5fa00fa31553b73ebf1942676e86291e8372ff2a2260956d9b8aae1d763fbf31";

    private static ICredentialResolver Resolver(string secret = SecretKey) =>
        new StaticCredentialResolver(new ProxyConfig
        {
            Credentials =
            {
                new CredentialEntry
                {
                    AwsAccessKeyId = AccessKeyId,
                    AwsSecretAccessKey = secret,
                    Azure = new AzureCredentials(),
                },
            },
        });

    private static SigV4Request VanillaRequest(string? authorization = GetVanillaAuthorization)
    {
        var headers = new List<KeyValuePair<string, string>>
        {
            new("Host", "example.amazonaws.com"),
            new("X-Amz-Date", AmzDate),
        };
        if (authorization is not null)
        {
            headers.Add(new("Authorization", authorization));
        }

        return new SigV4Request
        {
            HttpMethod = "GET",
            RawPath = "/",
            RawQueryString = string.Empty,
            Headers = headers,
            PayloadHash = SigV4Constants.EmptyPayloadSha256,
            S3PathStyle = true,
            // Pin "now" to the signing time so clock-skew checks pass.
            Now = SigningKey.TryParseAmzDate(AmzDate, out var t) ? t : DateTimeOffset.UtcNow,
        };
    }

    [Fact]
    public void Valid_signature_returns_ok()
    {
        var validator = new SigV4Validator(Resolver());
        var result = validator.Validate(VanillaRequest());

        Assert.True(result.IsValid);
        Assert.Equal(AccessKeyId, result.AccessKeyId);
    }

    [Fact]
    public void Wrong_secret_yields_invalid_signature()
    {
        var validator = new SigV4Validator(Resolver(secret: "wrong-secret"));
        var result = validator.Validate(VanillaRequest());

        Assert.Equal(SigV4ValidationStatus.InvalidSignature, result.Status);
    }

    [Fact]
    public void Unknown_access_key_is_reported_as_such()
    {
        var validator = new SigV4Validator(Resolver());
        var auth = GetVanillaAuthorization.Replace(
            "Credential=AKIDEXAMPLE/", "Credential=AKIDUNKNOWN/", StringComparison.Ordinal);

        var result = validator.Validate(VanillaRequest(auth));
        Assert.Equal(SigV4ValidationStatus.UnknownAccessKey, result.Status);
    }

    [Fact]
    public void Missing_authorization_header_is_malformed()
    {
        var validator = new SigV4Validator(Resolver());
        var result = validator.Validate(VanillaRequest(authorization: null));

        Assert.Equal(SigV4ValidationStatus.Malformed, result.Status);
    }

    [Fact]
    public void Tampered_signature_yields_invalid_signature()
    {
        var validator = new SigV4Validator(Resolver());
        var tampered = GetVanillaAuthorization
            .Substring(0, GetVanillaAuthorization.Length - 1) + "0";

        var result = validator.Validate(VanillaRequest(tampered));
        Assert.Equal(SigV4ValidationStatus.InvalidSignature, result.Status);
    }

    [Fact]
    public void Clock_skew_beyond_window_is_rejected()
    {
        var validator = new SigV4Validator(Resolver(), maxClockSkew: TimeSpan.FromMinutes(5));
        var req = VanillaRequest();
        // Force "now" 1 hour after the signing time.
        var futureRequest = new SigV4Request
        {
            HttpMethod = req.HttpMethod,
            RawPath = req.RawPath,
            RawQueryString = req.RawQueryString,
            Headers = req.Headers,
            PayloadHash = req.PayloadHash,
            S3PathStyle = req.S3PathStyle,
            Now = req.Now!.Value.AddHours(1),
        };

        var result = validator.Validate(futureRequest);
        Assert.Equal(SigV4ValidationStatus.ClockSkewTooLarge, result.Status);
    }

    [Fact]
    public void Presigned_url_signature_is_validated_and_excluded_from_canonical_query()
    {
        // Build a presigned GET. Steps mirror the spec so we know the
        // expected signature comes from our own logic — this is a
        // self-consistency test for the presigned codepath.
        var resolver = Resolver();
        var scope = $"{ShortDate}/{Region}/{Service}/aws4_request";
        var credential = $"{AccessKeyId}/{scope}";

        var headers = new List<KeyValuePair<string, string>>
        {
            new("Host", "example.amazonaws.com"),
        };

        var query =
            $"X-Amz-Algorithm=AWS4-HMAC-SHA256&" +
            $"X-Amz-Credential={Uri.EscapeDataString(credential)}&" +
            $"X-Amz-Date={AmzDate}&" +
            $"X-Amz-Expires=900&" +
            $"X-Amz-SignedHeaders=host";

        var canonical = CanonicalRequest.Build("GET", "/", query, headers, ["host"],
            SigV4Constants.UnsignedPayload, s3PathStyle: true);
        var sts = CanonicalRequest.StringToSign(AmzDate, scope, canonical);
        var signingKey = SigningKey.Derive(SecretKey, ShortDate, Region, Service);
        var sig = SigningKey.ToLowerHex(
            SigningKey.HmacSha256(signingKey, System.Text.Encoding.UTF8.GetBytes(sts)));

        var signedQuery = query + "&X-Amz-Signature=" + sig;

        var validator = new SigV4Validator(resolver);
        var result = validator.Validate(new SigV4Request
        {
            HttpMethod = "GET",
            RawPath = "/",
            RawQueryString = signedQuery,
            Headers = headers,
            PayloadHash = SigV4Constants.UnsignedPayload,
            S3PathStyle = true,
            Now = SigningKey.TryParseAmzDate(AmzDate, out var t) ? t : DateTimeOffset.UtcNow,
        });

        Assert.True(result.IsValid, result.Reason);
    }

    [Fact]
    public void Presigned_url_past_expiry_is_rejected()
    {
        var resolver = Resolver();
        var scope = $"{ShortDate}/{Region}/{Service}/aws4_request";
        var credential = $"{AccessKeyId}/{scope}";

        var headers = new List<KeyValuePair<string, string>>
        {
            new("Host", "example.amazonaws.com"),
        };

        var query =
            $"X-Amz-Algorithm=AWS4-HMAC-SHA256&" +
            $"X-Amz-Credential={Uri.EscapeDataString(credential)}&" +
            $"X-Amz-Date={AmzDate}&" +
            $"X-Amz-Expires=60&" +
            $"X-Amz-SignedHeaders=host&" +
            $"X-Amz-Signature=deadbeef";

        var validator = new SigV4Validator(resolver);
        var result = validator.Validate(new SigV4Request
        {
            HttpMethod = "GET",
            RawPath = "/",
            RawQueryString = query,
            Headers = headers,
            PayloadHash = SigV4Constants.UnsignedPayload,
            S3PathStyle = true,
            Now = SigningKey.TryParseAmzDate(AmzDate, out var t)
                ? t.AddHours(1)
                : DateTimeOffset.UtcNow,
        });

        Assert.Equal(SigV4ValidationStatus.Expired, result.Status);
    }

    [Fact]
    public void Presigned_url_without_X_Amz_Expires_is_rejected_as_malformed()
    {
        var resolver = Resolver();
        var credential = $"{AccessKeyId}/{ShortDate}/{Region}/{Service}/aws4_request";
        var query =
            $"X-Amz-Algorithm=AWS4-HMAC-SHA256&" +
            $"X-Amz-Credential={Uri.EscapeDataString(credential)}&" +
            $"X-Amz-Date={AmzDate}&" +
            $"X-Amz-SignedHeaders=host&" +
            $"X-Amz-Signature=deadbeef";
        var result = new SigV4Validator(resolver).Validate(new SigV4Request
        {
            HttpMethod = "GET",
            RawPath = "/",
            RawQueryString = query,
            Headers = new[] { new KeyValuePair<string, string>("Host", "example.amazonaws.com") },
            PayloadHash = SigV4Constants.UnsignedPayload,
        });
        Assert.Equal(SigV4ValidationStatus.Malformed, result.Status);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("604801")]
    [InlineData("not-an-int")]
    public void Presigned_url_with_invalid_X_Amz_Expires_is_rejected_as_malformed(string expires)
    {
        var resolver = Resolver();
        var credential = $"{AccessKeyId}/{ShortDate}/{Region}/{Service}/aws4_request";
        var query =
            $"X-Amz-Algorithm=AWS4-HMAC-SHA256&" +
            $"X-Amz-Credential={Uri.EscapeDataString(credential)}&" +
            $"X-Amz-Date={AmzDate}&" +
            $"X-Amz-Expires={expires}&" +
            $"X-Amz-SignedHeaders=host&" +
            $"X-Amz-Signature=deadbeef";
        var result = new SigV4Validator(resolver).Validate(new SigV4Request
        {
            HttpMethod = "GET",
            RawPath = "/",
            RawQueryString = query,
            Headers = new[] { new KeyValuePair<string, string>("Host", "example.amazonaws.com") },
            PayloadHash = SigV4Constants.UnsignedPayload,
        });
        Assert.Equal(SigV4ValidationStatus.Malformed, result.Status);
    }
}
