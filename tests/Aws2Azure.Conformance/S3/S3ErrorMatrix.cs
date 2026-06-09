namespace Aws2Azure.Conformance.S3;

/// <summary>
/// One proxy-side S3 error scenario: a recipe that crafts a signed request the
/// proxy must reject before any Azure call — either during the SigV4 stage
/// (bad/unknown/skewed signature) or the request-validation stage (e.g. an
/// invalid bucket name) — paired with the AWS-contract outcome (HTTP status +
/// error <c>Code</c>) that real S3 documents for that rejection. These outcomes
/// are derived from the AWS S3 API contract, not from the proxy's own output,
/// so the assertions are an independent oracle.
/// </summary>
public sealed record S3ErrorCase(
    string Name,
    string Operation,
    int ExpectedStatus,
    string ExpectedCode,
    Action<HttpRequestMessage> Sign,
    HttpMethod? Method = null,
    string? Path = null)
{
    /// <summary>Default request line shared by the auth-error cases.</summary>
    public const string DefaultPath = "/conformance-bucket/key.txt";

    public HttpRequestMessage BuildRequest()
    {
        var request = new HttpRequestMessage(
            Method ?? HttpMethod.Get,
            new Uri("http://s3.us-east-1.amazonaws.com" + (Path ?? DefaultPath)));
        Sign(request);
        return request;
    }
}

/// <summary>
/// The S3 proxy-side error matrix. Every case rejects before any Azure call —
/// in the SigV4 stage (auth errors) or the request-validation stage (e.g.
/// <c>InvalidBucketName</c>) — so the whole matrix runs offline on every PR.
/// </summary>
public static class S3ErrorMatrix
{
    private static readonly byte[] EmptyBody = Array.Empty<byte>();

    public static IReadOnlyList<S3ErrorCase> Cases { get; } = new[]
    {
        new S3ErrorCase(
            "signature-does-not-match",
            "s3:SignatureDoesNotMatch",
            403,
            "SignatureDoesNotMatch",
            req => ConformanceSigV4Signer.SignHeader(
                req, EmptyBody,
                ConformanceProxyFixture.AccessKeyId,
                // Wrong secret → valid key, bad signature.
                ConformanceProxyFixture.Secret + "TAMPERED")),

        new S3ErrorCase(
            "invalid-access-key-id",
            "s3:InvalidAccessKeyId",
            403,
            "InvalidAccessKeyId",
            req => ConformanceSigV4Signer.SignHeader(
                req, EmptyBody,
                // Unknown access key.
                "AKIAUNKNOWNKEY000001",
                ConformanceProxyFixture.Secret)),

        new S3ErrorCase(
            "request-time-too-skewed",
            "s3:RequestTimeTooSkewed",
            403,
            "RequestTimeTooSkewed",
            req => ConformanceSigV4Signer.SignHeader(
                req, EmptyBody,
                ConformanceProxyFixture.AccessKeyId,
                ConformanceProxyFixture.Secret,
                // Correctly signed but a day in the past → clock-skew rejection.
                now: DateTimeOffset.UtcNow.AddDays(-1))),

        // Validly signed (passes SigV4) but targets a syntactically invalid
        // bucket name. The proxy rejects it in the request-validation stage
        // (BlobClient.IsValidContainerName) before any Azure call. A 2-char
        // name violates the 3-63 length rule shared by S3 and Azure container
        // names, so both the proxy and real S3 answer 400 InvalidBucketName.
        // (Note: real S3 only classifies length / a narrow set of syntax
        // violations as InvalidBucketName for a path-style GET — underscore /
        // uppercase / dotted names return 404 NoSuchBucket — so the oracle must
        // use a name S3 itself rejects, verified against live s3.us-east-1.)
        new S3ErrorCase(
            "invalid-bucket-name",
            "s3:InvalidBucketName",
            400,
            "InvalidBucketName",
            req => ConformanceSigV4Signer.SignHeader(
                req, EmptyBody,
                ConformanceProxyFixture.AccessKeyId,
                ConformanceProxyFixture.Secret),
            Path: "/ab/key.txt"),
    };
}
