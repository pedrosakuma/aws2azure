using Aws2Azure.Conformance.Cases;

namespace Aws2Azure.Conformance.S3;

/// <summary>
/// One Tier-2 backend-mapped S3 error scenario. Unlike the Tier-1 auth matrix
/// (rejected in the SigV4 stage), these requests are <em>validly</em> signed and
/// reach the backend; the error is produced by translating the Azure Blob
/// failure (container/blob not found) into its S3 equivalent. LocalStack S3
/// produces the authoritative real-S3 shape for the same request, so the two
/// are diffed.
///
/// <para>
/// Because these requests are validly signed and reach a real backend, they are
/// also meaningful Tier-3 real-AWS capture cases (unlike the Tier-1
/// <see cref="S3ErrorCase"/> matrix, which is rejected before any backend call).
/// <see cref="CreatePlanAsync"/> implements <see cref="IConformanceCase"/> so
/// <c>RealAwsConformanceCaptureTests</c> can execute the same signed request
/// against real S3 and capture its authoritative response as a golden, using the
/// bucket/key provisioning the fixture already performs for the Tier-3 run.
/// </para>
/// </summary>
public sealed record S3BackendErrorCase(
    string Name,
    int ExpectedStatus,
    string ExpectedCode,
    bool RequiresExistingBucket,
    bool RequiresExistingObject = false,
    Action<System.Net.Http.HttpRequestMessage>? ConfigureRequest = null,
    System.Net.Http.HttpMethod? Method = null,
    string? SignRegion = null,
    bool TargetsBucketRoot = false,
    string? LocationConstraint = null,
    string? BucketPropertyName = null) : IConformanceCase
{
    private static readonly Uri DefaultBaseAddress = new("http://s3.us-east-1.amazonaws.com/");

    /// <inheritdoc />
    public string Operation => Name;

    /// <inheritdoc />
    public ConformanceCaseExpectation Expected =>
        ConformanceCaseExpectation.Error(
            ExpectedStatus,
            ExpectedCode,
            "Tier-2 backend-mapped S3 error asserted from the AWS S3 contract; also captured against real AWS (Tier-3).");

    /// <inheritdoc />
    public ValueTask<ConformanceExecutionPlan> CreatePlanAsync(
        ConformanceCaseContext context,
        CancellationToken cancellationToken = default)
    {
        // "nosuchbucket-get-object" is the one case that must target a bucket
        // that does not exist on either backend. "bucketalreadyownedbyyou-
        // recreate" needs its own bucket, provisioned by the harness in
        // SignRegion, because real S3 rejects a SigV4 scope that doesn't match
        // the target bucket's actual region (AuthorizationHeaderMalformed)
        // before it ever reaches ownership-conflict handling — unlike
        // LocalStack, which is lenient about signed-region vs bucket-region.
        // Every other case reuses the shared bucket the fixture provisioned/
        // seeded in the default region for this run.
        var bucket = RequiresExistingBucket
            ? context.GetRequiredProperty(BucketPropertyName ?? "bucketName")
            : context.GetRequiredProperty(BucketPropertyName ?? "bucketName") + "-missing";
        var path = BuildPath(bucket);
        return new ValueTask<ConformanceExecutionPlan>(new ConformanceExecutionPlan(
            [new ConformanceRequestStep(Name, _ => BuildRequest(context, bucket, path))]));
    }

    /// <summary>
    /// The request path for this case: the bucket root for
    /// <see cref="TargetsBucketRoot"/> (CreateBucket-style) cases, the missing
    /// key for the not-found cases, or the pre-seeded existing key for the
    /// conditional-GET case.
    /// </summary>
    public string BuildPath(string bucket)
        => TargetsBucketRoot
            ? $"/{bucket}"
            : RequiresExistingObject
                ? $"/{bucket}/{S3BackendErrorMatrix.ExistingKey}"
                : $"/{bucket}/{S3BackendErrorMatrix.MissingKey}";

    private HttpRequestMessage BuildRequest(ConformanceCaseContext context, string bucket, string path)
    {
        var body = LocationConstraint is null
            ? Array.Empty<byte>()
            : System.Text.Encoding.UTF8.GetBytes(
                "<CreateBucketConfiguration xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\">" +
                $"<LocationConstraint>{LocationConstraint}</LocationConstraint>" +
                "</CreateBucketConfiguration>");

        // Real S3 validates the SigV4 signed scope against the endpoint/bucket
        // region and rejects a mismatch with 400 AuthorizationHeaderMalformed
        // before evaluating the request itself, so a case signed for a region
        // other than the context default must also target that region's
        // regional endpoint (not the shared us-east-1 base address) to reach
        // its intended contract outcome on real AWS.
        var baseAddress = SignRegion is null || string.Equals(SignRegion, context.Region, StringComparison.Ordinal)
            ? context.BaseAddress ?? DefaultBaseAddress
            : new Uri($"https://s3.{SignRegion}.amazonaws.com/");

        var request = new HttpRequestMessage(
            Method ?? HttpMethod.Get,
            new Uri(baseAddress, path));
        if (body.Length > 0)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentLength = body.Length;
        }

        ConformanceSigV4Signer.SignHeader(
            request,
            body,
            context.AccessKeyId,
            context.SecretAccessKey,
            region: SignRegion ?? context.Region,
            sessionToken: context.SessionToken);

        ConfigureRequest?.Invoke(request);
        return request;
    }
}

/// <summary>
/// The S3 backend-error matrix. Most cases are <c>GET object</c> requests whose
/// outcome should be wire-faithful between the proxy-over-Azurite response and
/// the LocalStack response, up to the documented gaps in the gap-doc allow-list
/// (missing container vs missing blob, or a conditional GET against a real
/// object). One case is a <c>PUT</c> against the bucket root (CreateBucket) that
/// re-creates an owned bucket outside us-east-1, exercising the region-sensitive
/// <c>BucketAlreadyOwnedByYou</c> branch.
/// </summary>
public static class S3BackendErrorMatrix
{
    public const string MissingKey = "missing-object-key.txt";
    public const string ExistingKey = "conditional-object.txt";

    public static IReadOnlyList<S3BackendErrorCase> Cases { get; } = new[]
    {
        // GET on a bucket that exists on neither backend → Azure ContainerNotFound
        // → NoSuchBucket; real S3 likewise returns NoSuchBucket.
        new S3BackendErrorCase(
            "nosuchbucket-get-object",
            404,
            "NoSuchBucket",
            RequiresExistingBucket: false),

        // GET a missing key in a bucket that exists on both → Azure BlobNotFound
        // → NoSuchKey; real S3 likewise returns NoSuchKey.
        new S3BackendErrorCase(
            "nosuchkey-get-object",
            404,
            "NoSuchKey",
            RequiresExistingBucket: true),

        // GET an existing object with an If-Match the object's ETag cannot
        // satisfy → 412 PreconditionFailed on both. The proxy evaluates the
        // condition locally (it translates ETags, so Azure can't), and must
        // emit a full <Error> envelope — not an empty 412 — to match S3.
        new S3BackendErrorCase(
            "precondition-failed-get",
            412,
            "PreconditionFailed",
            RequiresExistingBucket: true,
            RequiresExistingObject: true,
            ConfigureRequest: req => req.Headers.TryAddWithoutValidation(
                "If-Match", "\"00000000000000000000000000000000\"")),

        // PUT a bucket the caller already owns, signed for a region OTHER than
        // us-east-1. Real S3 (and LocalStack) answer 409 BucketAlreadyOwnedByYou;
        // only us-east-1 collapses the re-create to an idempotent 200 OK (issue
        // #236), so eu-west-1 exercises the 409 branch. The proxy reaches Azure,
        // gets ContainerAlreadyExists, and — because the signed scope region is
        // not us-east-1 — maps it to the same 409. Unlike the GET-object cases
        // above this targets the bucket root with a PUT (CreateBucket); the
        // bucket is provisioned first via RequiresExistingBucket. The
        // CreateBucketConfiguration/LocationConstraint body is required so that
        // LocalStack actually records the bucket's region as eu-west-1 — without
        // it a non-us-east-1 CreateBucket is rejected with
        // IllegalLocationConstraintException before it can reach
        // BucketAlreadyOwnedByYou. The proxy ignores the body (region comes from
        // the signed scope, and LocationConstraint is unsupported), so its 409 is
        // unaffected; both backends receive the same signed request.
        new S3BackendErrorCase(
            "bucketalreadyownedbyyou-recreate",
            409,
            "BucketAlreadyOwnedByYou",
            RequiresExistingBucket: true,
            Method: System.Net.Http.HttpMethod.Put,
            SignRegion: "eu-west-1",
            TargetsBucketRoot: true,
            LocationConstraint: "eu-west-1",
            BucketPropertyName: "euWestBucketName"),
    };
}
