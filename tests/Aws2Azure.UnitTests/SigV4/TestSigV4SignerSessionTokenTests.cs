using Aws2Azure.Core.SigV4;
using Aws2Azure.TestSupport.SigV4;
using Xunit;

namespace Aws2Azure.UnitTests.SigV4;

/// <summary>
/// Regression coverage for the real-AWS capture harness's SigV4 signer: it must
/// include x-amz-security-token in both the request headers AND the signed
/// header set whenever a session token (e.g. from short-lived OIDC-derived
/// credentials) is supplied. Omitting it from SignedHeaders produces a
/// signature AWS rejects with "There were headers present in the request
/// which were not signed" (see #708/#716 real-AWS capture failures).
/// </summary>
public sealed class TestSigV4SignerSessionTokenTests
{
    [Fact]
    public void SignHeader_with_session_token_signs_the_security_token_header()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://s3.us-east-1.amazonaws.com/bucket");

        TestSigV4Signer.SignHeader(
            request,
            body: [],
            accessKey: "AKIAEXAMPLE",
            secret: "secret",
            region: "us-east-1",
            service: "s3",
            now: new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
            sessionToken: "example-session-token");

        Assert.True(request.Headers.TryGetValues(SigV4Constants.AmzSecurityTokenHeader, out var tokenValues));
        Assert.Equal("example-session-token", Assert.Single(tokenValues));

        Assert.True(request.Headers.TryGetValues("Authorization", out var authValues));
        var authHeader = Assert.Single(authValues);
        Assert.Contains(
            $"SignedHeaders=host;{SigV4Constants.AmzContentSha256Header};{SigV4Constants.AmzDateHeader};{SigV4Constants.AmzSecurityTokenHeader}",
            authHeader,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SignHeader_without_session_token_omits_the_security_token_header()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://s3.us-east-1.amazonaws.com/bucket");

        TestSigV4Signer.SignHeader(
            request,
            body: [],
            accessKey: "AKIAEXAMPLE",
            secret: "secret",
            region: "us-east-1",
            service: "s3",
            now: new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.False(request.Headers.Contains(SigV4Constants.AmzSecurityTokenHeader));

        Assert.True(request.Headers.TryGetValues("Authorization", out var authValues));
        var authHeader = Assert.Single(authValues);
        Assert.DoesNotContain(SigV4Constants.AmzSecurityTokenHeader, authHeader, StringComparison.Ordinal);
    }
}
