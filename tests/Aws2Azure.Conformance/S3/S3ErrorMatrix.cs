using Aws2Azure.Conformance.Cases;

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
    Action<HttpRequestMessage, ConformanceCaseContext> Sign,
    HttpMethod? Method = null,
    string? Path = null) : IConformanceCase
{
    /// <summary>Default request line shared by the auth-error cases.</summary>
    public const string DefaultPath = "/conformance-bucket/key.txt";

    /// <inheritdoc />
    public ConformanceCaseExpectation Expected =>
        ConformanceCaseExpectation.Error(
            ExpectedStatus,
            ExpectedCode,
            "Proxy-side S3 rejection asserted from the AWS REST-XML contract.");

    public HttpRequestMessage BuildRequest(ConformanceCaseContext context)
    {
        var request = new HttpRequestMessage(
            Method ?? HttpMethod.Get,
            new Uri("http://s3.us-east-1.amazonaws.com" + (Path ?? DefaultPath)));
        Sign(request, context);
        return request;
    }

    /// <inheritdoc />
    public ValueTask<ConformanceExecutionPlan> CreatePlanAsync(
        ConformanceCaseContext context,
        CancellationToken cancellationToken = default)
        => new(new ConformanceExecutionPlan(
            [new ConformanceRequestStep(Name, _ => BuildRequest(context))]));
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
            (req, context) => ConformanceSigV4Signer.SignHeader(
                req, EmptyBody,
                context.AccessKeyId,
                // Wrong secret → valid key, bad signature.
                context.SecretAccessKey + "TAMPERED",
                sessionToken: context.SessionToken)),

        new S3ErrorCase(
            "invalid-access-key-id",
            "s3:InvalidAccessKeyId",
            403,
            "InvalidAccessKeyId",
            (req, context) => ConformanceSigV4Signer.SignHeader(
                req, EmptyBody,
                // Unknown access key.
                "AKIAUNKNOWNKEY000001",
                context.SecretAccessKey,
                sessionToken: context.SessionToken)),

        new S3ErrorCase(
            "request-time-too-skewed",
            "s3:RequestTimeTooSkewed",
            403,
            "RequestTimeTooSkewed",
            (req, context) => ConformanceSigV4Signer.SignHeader(
                req, EmptyBody,
                context.AccessKeyId,
                context.SecretAccessKey,
                // Correctly signed but a day in the past → clock-skew rejection.
                now: DateTimeOffset.UtcNow.AddDays(-1),
                sessionToken: context.SessionToken)),

        // Validly signed (passes SigV4) but targets a syntactically invalid
        // bucket name. The proxy rejects it in the request-validation stage
        // before any Azure call. A 2-char name violates the 3-63 length rule
        // shared by S3 and Azure container names, so both the proxy and real S3
        // answer 400 InvalidBucketName. (Real S3's path-style lookup only
        // classifies the length rule as InvalidBucketName; other Azure-illegal
        // shapes resolve to 404 NoSuchBucket — see the next case.)
        new S3ErrorCase(
            "invalid-bucket-name",
            "s3:InvalidBucketName",
            400,
            "InvalidBucketName",
            (req, context) => ConformanceSigV4Signer.SignHeader(
                req, EmptyBody,
                context.AccessKeyId,
                context.SecretAccessKey,
                sessionToken: context.SessionToken),
            Path: "/ab/key.txt"),

        // Validly signed GET against a bucket name that is length-legal (3-63)
        // but not an Azure container name (underscore). Real us-east-1 path-style
        // S3 treats this as a perfectly addressable *legacy* bucket name that
        // simply doesn't exist → 404 NoSuchBucket, NOT 400 InvalidBucketName.
        // The proxy short-circuits to NoSuchBucket before any Azure call because
        // no such container can exist (issue #237). Verified against live
        // s3.us-east-1.amazonaws.com.
        new S3ErrorCase(
            "azure-illegal-bucket-name-is-nosuchbucket",
            "s3:NoSuchBucket",
            404,
            "NoSuchBucket",
            (req, context) => ConformanceSigV4Signer.SignHeader(
                req, EmptyBody,
                context.AccessKeyId,
                context.SecretAccessKey,
                sessionToken: context.SessionToken),
            Path: "/conformance_invalid_bucket/key.txt"),

        // Multipart lookup paths classify the destination bucket BEFORE decoding
        // the uploadId, so the same path-style rules apply: a length-illegal
        // bucket is 400 InvalidBucketName even with an uploadId present, rather
        // than leaking a 404 NoSuchUpload (issue #237 review follow-up). A GET
        // carrying ?uploadId routes to ListParts.
        new S3ErrorCase(
            "multipart-invalid-bucket-name",
            "s3:InvalidBucketName",
            400,
            "InvalidBucketName",
            (req, context) => ConformanceSigV4Signer.SignHeader(
                req, EmptyBody,
                context.AccessKeyId,
                context.SecretAccessKey,
                sessionToken: context.SessionToken),
            Path: "/ab/key.txt?uploadId=nonexistent"),

        // Same multipart lookup path, Azure-illegal (length-legal) bucket name:
        // resolves to 404 NoSuchBucket before the uploadId is examined, not
        // NoSuchUpload.
        new S3ErrorCase(
            "multipart-azure-illegal-bucket-name-is-nosuchbucket",
            "s3:NoSuchBucket",
            404,
            "NoSuchBucket",
            (req, context) => ConformanceSigV4Signer.SignHeader(
                req, EmptyBody,
                context.AccessKeyId,
                context.SecretAccessKey,
                sessionToken: context.SessionToken),
            Path: "/conformance_invalid_bucket/key.txt?uploadId=nonexistent"),
    };
}
