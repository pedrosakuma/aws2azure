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
    bool RequiresExistingBucket);

/// <summary>
/// The S3 backend-error matrix. Every case is a <c>GET object</c> whose target
/// is absent on both backends in the same way (missing container vs missing
/// blob), so the proxy-over-Azurite response and the LocalStack response should
/// be wire-faithful up to the documented gaps in the gap-doc allow-list.
/// </summary>
public static class S3BackendErrorMatrix
{
    public const string MissingKey = "missing-object-key.txt";

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
    };
}
