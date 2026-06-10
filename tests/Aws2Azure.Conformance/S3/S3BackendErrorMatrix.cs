namespace Aws2Azure.Conformance.S3;

/// <summary>
/// One Tier-2 backend-mapped S3 error scenario. Unlike the Tier-1 auth matrix
/// (rejected in the SigV4 stage), these requests are <em>validly</em> signed and
/// reach the backend; the error is produced by translating the Azure Blob
/// failure (container/blob not found) into its S3 equivalent. LocalStack S3
/// produces the authoritative real-S3 shape for the same request, so the two
/// are diffed.
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
    bool TargetsBucketRoot = false);

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
        // bucket is provisioned first via RequiresExistingBucket.
        new S3BackendErrorCase(
            "bucketalreadyownedbyyou-recreate",
            409,
            "BucketAlreadyOwnedByYou",
            RequiresExistingBucket: true,
            Method: System.Net.Http.HttpMethod.Put,
            SignRegion: "eu-west-1",
            TargetsBucketRoot: true),
    };
}
