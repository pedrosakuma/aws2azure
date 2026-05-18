using Aws2Azure.Core.SigV4;

namespace Aws2Azure.UnitTests.SigV4;

public class CanonicalRequestTests
{
    [Fact]
    public void UriEncode_leaves_unreserved_and_optionally_slash_alone()
    {
        Assert.Equal("ABCabc012-_.~", CanonicalRequest.UriEncode("ABCabc012-_.~", encodeSlash: false));
        Assert.Equal("foo/bar", CanonicalRequest.UriEncode("foo/bar", encodeSlash: false));
        Assert.Equal("foo%2Fbar", CanonicalRequest.UriEncode("foo/bar", encodeSlash: true));
        Assert.Equal("a%20b", CanonicalRequest.UriEncode("a b", encodeSlash: false));
    }

    [Fact]
    public void Canonical_query_is_sorted_and_signature_is_excluded()
    {
        var q = "b=2&a=1&X-Amz-Signature=abc&c=3";
        Assert.Equal("a=1&b=2&c=3", CanonicalRequest.CanonicalQuery(q));
    }

    [Fact]
    public void Canonical_query_sorts_collisions_by_value_lexicographically()
    {
        // Per SigV4, keys with identical names are sorted by *byte* value of
        // the URI-encoded value — i.e. lexicographic, not numeric.
        Assert.Equal("a=1&a=10&a=2", CanonicalRequest.CanonicalQuery("a=2&a=10&a=1"));
    }

    [Fact]
    public void Canonical_query_with_empty_input_returns_empty()
    {
        Assert.Equal(string.Empty, CanonicalRequest.CanonicalQuery(string.Empty));
        Assert.Equal(string.Empty, CanonicalRequest.CanonicalQuery("?"));
    }

    [Fact]
    public void TrimAndCollapseWhitespace_collapses_runs_outside_quotes()
    {
        Assert.Equal("a b c", CanonicalRequest.TrimAndCollapseWhitespace("  a   b\tc  "));
        Assert.Equal("\"a   b\"", CanonicalRequest.TrimAndCollapseWhitespace("  \"a   b\"  "));
    }

    [Fact]
    public void S3_path_style_encodes_once_others_encode_twice()
    {
        Assert.Equal("/my-bucket/my%20key", CanonicalRequest.CanonicalUri("/my-bucket/my key", s3PathStyle: true));
        Assert.Equal("/my-bucket/my%2520key", CanonicalRequest.CanonicalUri("/my-bucket/my key", s3PathStyle: false));
    }

    [Fact]
    public void Canonical_query_preserves_slashes_in_credential_as_percent_2F()
    {
        // X-Amz-Credential always contains '/' separators that SigV4 requires
        // to round-trip as %2F in the canonical query.
        var q = "X-Amz-Credential=AKID%2F20150830%2Fus-east-1%2Fs3%2Faws4_request";
        Assert.Equal(q, CanonicalRequest.CanonicalQuery(q));
    }

    [Fact]
    public void Canonical_query_does_not_decode_plus_as_space()
    {
        // SigV4 query is RFC3986, not form-url-encoded: a literal '+' must
        // canonicalize as %2B (and an already-encoded %2B must round-trip).
        Assert.Equal("k=%2B", CanonicalRequest.CanonicalQuery("k=+"));
        Assert.Equal("k=%2B", CanonicalRequest.CanonicalQuery("k=%2B"));
        // %20 in -> %20 out; literal space (decoded from %20) re-encodes to %20.
        Assert.Equal("k=%20", CanonicalRequest.CanonicalQuery("k=%20"));
    }
}

public class CredentialScopeTests
{
    [Fact]
    public void Parses_valid_credential_string()
    {
        Assert.True(CredentialScope.TryParse("AKIDEXAMPLE/20150830/us-east-1/service/aws4_request", out var scope));
        Assert.Equal("AKIDEXAMPLE", scope.AccessKeyId);
        Assert.Equal("20150830", scope.Date);
        Assert.Equal("us-east-1", scope.Region);
        Assert.Equal("service", scope.Service);
        Assert.Equal("20150830/us-east-1/service/aws4_request", scope.ToScopeString());
    }

    [Theory]
    [InlineData("AKID/20150830/us-east-1/service")]
    [InlineData("AKID/20150830/us-east-1/service/aws4_request/extra")]
    [InlineData("AKID/2015083/us-east-1/service/aws4_request")]
    [InlineData("AKID/20150830/us-east-1/service/AWS4_REQUEST")]
    [InlineData("")]
    public void Rejects_malformed_inputs(string input)
    {
        Assert.False(CredentialScope.TryParse(input, out _));
    }
}

public class AuthorizationHeaderTests
{
    private const string Valid =
        "AWS4-HMAC-SHA256 " +
        "Credential=AKIDEXAMPLE/20150830/us-east-1/service/aws4_request, " +
        "SignedHeaders=host;x-amz-date, " +
        "Signature=5fa00fa31553b73ebf1942676e86291e8372ff2a2260956d9b8aae1d763fbf31";

    [Fact]
    public void Parses_well_formed_header()
    {
        Assert.True(AuthorizationHeader.TryParse(Valid, out var parsed));
        Assert.Equal("AKIDEXAMPLE", parsed.Credential.AccessKeyId);
        Assert.Equal(["host", "x-amz-date"], parsed.SignedHeaders);
        Assert.Equal("5fa00fa31553b73ebf1942676e86291e8372ff2a2260956d9b8aae1d763fbf31", parsed.Signature);
    }

    [Fact]
    public void Rejects_uppercase_or_unsorted_signed_headers()
    {
        var bad = Valid.Replace("host;x-amz-date", "x-amz-date;host", StringComparison.Ordinal);
        Assert.False(AuthorizationHeader.TryParse(bad, out _));

        var upper = Valid.Replace("host;x-amz-date", "Host;x-amz-date", StringComparison.Ordinal);
        Assert.False(AuthorizationHeader.TryParse(upper, out _));
    }

    [Fact]
    public void Rejects_wrong_algorithm_prefix()
    {
        Assert.False(AuthorizationHeader.TryParse("AWS2-HMAC-SHA1 ...", out _));
        Assert.False(AuthorizationHeader.TryParse(string.Empty, out _));
    }
}
