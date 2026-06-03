using System.Text;
using Aws2Azure.Core.SigV4;

namespace Aws2Azure.UnitTests.SigV4;

/// <summary>
/// Proves the allocation-light byte pipe (<see cref="CanonicalRequest.HashCanonicalRequest"/>
/// + the span <see cref="CanonicalRequest.StringToSign(string,string,ReadOnlySpan{byte})"/>
/// overload) is byte-identical to the string oracle
/// (<see cref="CanonicalRequest.Build"/> + <see cref="CanonicalRequest.StringToSign(string,string,string)"/>)
/// over an adversarial corpus. The string path stays the correctness oracle;
/// this guards that production validation routed through the byte pipe never
/// diverges from it.
/// </summary>
public class CanonicalRequestBytePipeGoldenTests
{
    public static IEnumerable<object[]> Corpus()
    {
        // case = (method, rawPath, rawQuery, headers, signedHeaders, payloadHash, s3PathStyle)

        // AWS get-vanilla vector.
        yield return Case("GET", "/", "",
            [Kv("Host", "example.amazonaws.com"), Kv("X-Amz-Date", "20150830T123600Z")],
            ["host", "x-amz-date"], SigV4Constants.EmptyPayloadSha256, true);

        // Representative DynamoDB GetItem-style header set (empty query, "/" path).
        yield return Case("POST", "/", "",
            [
                Kv("Host", "dynamodb.us-east-1.amazonaws.com"),
                Kv("X-Amz-Date", "20240115T101112Z"),
                Kv("X-Amz-Target", "DynamoDB_20120810.GetItem"),
                Kv("Content-Type", "application/x-amz-json-1.0"),
                Kv("X-Amz-Content-Sha256", "9b6c1e2f3a4d5b6c7e8f90112233445566778899aabbccddeeff001122334455"),
                Kv("Content-Length", "128"),
                Kv("User-Agent", "aws-sdk-dotnet/3.7"),
                Kv("Accept-Encoding", "identity"),
            ],
            ["content-type", "host", "x-amz-content-sha256", "x-amz-date", "x-amz-target"],
            "9b6c1e2f3a4d5b6c7e8f90112233445566778899aabbccddeeff001122334455", false);

        // Multi-value header comma-join in ORIGINAL order; lowercase fold of mixed-case name.
        yield return Case("PUT", "/obj", "",
            [
                Kv("Host", "s3.amazonaws.com"),
                Kv("X-Amz-Meta-Tag", "first"),
                Kv("x-amz-meta-tag", "second"),
                Kv("X-AMZ-META-TAG", "third"),
                Kv("X-Amz-Date", "20240101T000000Z"),
            ],
            ["host", "x-amz-date", "x-amz-meta-tag"], SigV4Constants.UnsignedPayload, true);

        // Whitespace trimming + collapse, including runs inside double quotes (preserved).
        yield return Case("POST", "/", "",
            [
                Kv("Host", "svc.amazonaws.com"),
                Kv("X-Amz-Date", "20240101T000000Z"),
                Kv("X-Custom", "  a   b\tc  "),
                Kv("X-Quoted", "  \"a   b\"  c  "),
                Kv("X-Tabs", "\tx\t\ty\t"),
            ],
            ["host", "x-amz-date", "x-custom", "x-quoted", "x-tabs"],
            SigV4Constants.EmptyPayloadSha256, false);

        // Non-ASCII UTF-8 header values incl. a non-BMP (surrogate-pair) code point.
        yield return Case("POST", "/", "",
            [
                Kv("Host", "svc.amazonaws.com"),
                Kv("X-Amz-Date", "20240101T000000Z"),
                Kv("X-Unicode", "caf\u00e9 \u00fcber \u4e2d\u6587"),
                Kv("X-Emoji", "smile \U0001F600 grin"),
                Kv("X-Quoted-Unicode", "\"\U0001F600   spaced\""),
            ],
            ["host", "x-amz-date", "x-emoji", "x-quoted-unicode", "x-unicode"],
            SigV4Constants.EmptyPayloadSha256, false);

        // Empty / absent signed header values -> "name:\n".
        yield return Case("GET", "/", "",
            [
                Kv("Host", "svc.amazonaws.com"),
                Kv("X-Amz-Date", "20240101T000000Z"),
                Kv("X-Empty", ""),
                Kv("X-Spaces", "     "),
            ],
            // "x-absent" is signed but never present in the headers list.
            ["host", "x-absent", "x-amz-date", "x-empty", "x-spaces"],
            SigV4Constants.EmptyPayloadSha256, true);

        // S3 single-encode path with a space.
        yield return Case("GET", "/my-bucket/my key", "",
            [Kv("Host", "s3.amazonaws.com"), Kv("X-Amz-Date", "20240101T000000Z")],
            ["host", "x-amz-date"], SigV4Constants.EmptyPayloadSha256, true);

        // Non-S3 double-encode path with a space.
        yield return Case("GET", "/my-bucket/my key", "",
            [Kv("Host", "svc.amazonaws.com"), Kv("X-Amz-Date", "20240101T000000Z")],
            ["host", "x-amz-date"], SigV4Constants.EmptyPayloadSha256, false);

        // Query sorting / %2F round-trip / + -> %2B / %20 round-trip / signature excluded.
        yield return Case("GET", "/",
            "b=2&a=10&a=1&a=2&k=+&p=%20&X-Amz-Signature=deadbeef&X-Amz-Credential=AKID%2F20150830%2Fus-east-1%2Fs3%2Faws4_request",
            [Kv("Host", "s3.amazonaws.com"), Kv("X-Amz-Date", "20240101T000000Z")],
            ["host", "x-amz-date"], SigV4Constants.EmptyPayloadSha256, true);

        // Presigned-style: SignedHeaders may be unsorted / contain uppercase (not
        // normalized on the presigned path). Uppercase signed header matches
        // nothing -> empty value, exactly like the dictionary lookup.
        yield return Case("GET", "/",
            "X-Amz-Algorithm=AWS4-HMAC-SHA256&X-Amz-Expires=900",
            [Kv("Host", "svc.amazonaws.com"), Kv("X-Amz-Date", "20240101T000000Z")],
            ["x-amz-date", "host", "X-Amz-Date"], SigV4Constants.UnsignedPayload, false);

        // Lowercase method should be uppercased identically.
        yield return Case("get", "/", "",
            [Kv("Host", "svc.amazonaws.com"), Kv("X-Amz-Date", "20240101T000000Z")],
            ["host", "x-amz-date"], SigV4Constants.EmptyPayloadSha256, true);
    }

    [Theory]
    [MemberData(nameof(Corpus))]
    public void Byte_pipe_canonical_hash_matches_string_oracle(
        string method, string rawPath, string rawQuery,
        KeyValuePair<string, string>[] headers, string[] signedHeaders,
        string payloadHash, bool s3PathStyle)
    {
        var oracleCanonical = CanonicalRequest.Build(
            method, rawPath, rawQuery, headers, signedHeaders, payloadHash, s3PathStyle);
        var oracleHashHex = SigningKey.Sha256Hex(Encoding.UTF8.GetBytes(oracleCanonical));

        Span<byte> pipeHash = stackalloc byte[32];
        CanonicalRequest.HashCanonicalRequest(
            method, rawPath, rawQuery, headers, signedHeaders, payloadHash, s3PathStyle, pipeHash);
        var pipeHashHex = SigningKey.ToLowerHex(pipeHash);

        Assert.Equal(oracleHashHex, pipeHashHex);
    }

    [Theory]
    [MemberData(nameof(Corpus))]
    public void Byte_pipe_string_to_sign_matches_string_oracle(
        string method, string rawPath, string rawQuery,
        KeyValuePair<string, string>[] headers, string[] signedHeaders,
        string payloadHash, bool s3PathStyle)
    {
        const string amzDate = "20240101T000000Z";
        const string scope = "20240101/us-east-1/service/aws4_request";

        var oracleCanonical = CanonicalRequest.Build(
            method, rawPath, rawQuery, headers, signedHeaders, payloadHash, s3PathStyle);
        var oracleSts = CanonicalRequest.StringToSign(amzDate, scope, oracleCanonical);

        Span<byte> pipeHash = stackalloc byte[32];
        CanonicalRequest.HashCanonicalRequest(
            method, rawPath, rawQuery, headers, signedHeaders, payloadHash, s3PathStyle, pipeHash);
        var pipeSts = CanonicalRequest.StringToSign(amzDate, scope, pipeHash);

        Assert.Equal(oracleSts, pipeSts);
    }

    private static object[] Case(
        string method, string rawPath, string rawQuery,
        KeyValuePair<string, string>[] headers, string[] signedHeaders,
        string payloadHash, bool s3PathStyle)
        => [method, rawPath, rawQuery, headers, signedHeaders, payloadHash, s3PathStyle];

    private static KeyValuePair<string, string> Kv(string k, string v) => new(k, v);
}
