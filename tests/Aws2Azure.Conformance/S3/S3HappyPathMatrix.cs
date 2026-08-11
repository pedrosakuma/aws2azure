using System.Net.Http.Headers;
using System.Text;
using Aws2Azure.Conformance.Cases;

namespace Aws2Azure.Conformance.S3;

/// <summary>
/// Seed S3 happy-path matrix for issue #708. The existing Tier-1 fixture uses a
/// dummy Blob credential and intentionally never reaches a backend, so these
/// cases currently validate only the shared case abstraction and request-planning
/// scaffolding; the live round-trip itself is deferred to a backend-backed tier.
/// Even so, the cases are already expressed in the same multi-step model the
/// future Tier-2/Tier-3 capture/diff runner will execute.
///
/// <para>
/// The real-Azure nightly storage account keeps blob versioning enabled and
/// version-level immutability (<c>immutableStorageWithVersioning</c>) turned
/// on. A plain <c>DeleteObject</c> against that account does not create a new
/// delete-marker version — it only unsets the current-version pointer and
/// demotes the existing version to a non-current one — so teardown here only
/// needs one additional step: a versioned <c>?versionId=</c> hard delete of
/// the original version created by the seed <c>PutObject</c>. With
/// version-level immutability enabled, Azure additionally rejects
/// <c>DeleteBucket</c> (Delete Container) entirely through the data-plane
/// REST API — even once every blob version has been purged — because that
/// container-level operation is only permitted through the ARM management
/// plane. These plans therefore never assert a <c>DeleteBucket</c> step;
/// bucket cleanup is left to the nightly reaper
/// (<c>.github/workflows/real-azure-reaper.yml</c>), matching the existing
/// best-effort teardown convention used by
/// <c>S3RealAzureConformanceTests</c>/<c>S3RealAzureSmokeTests</c>.
/// </para>
/// </summary>
public static partial class S3HappyPathMatrix
{
    private static readonly Uri DefaultBaseAddress = new("http://s3.us-east-1.amazonaws.com/");

    private const string Tier1SkipReason =
        "Tier-1 S3 happy-path replay is deferred by issue #708: ConformanceProxyFixture " +
        "uses dummy Blob credentials and cannot complete a real S3 success round-trip offline.";

    public static IReadOnlyList<IConformanceCase> Cases { get; } =
    [
        CreateRoundTripCase(),
        CreatePaginationCase(),
        CreateConditionalCase(),
        CreateObjectTaggingRoundTripCase(),
        CreateDeleteObjectsBatchRoundTripCase(),
        CreateMultipartCopyCompleteRoundTripCase(),
        CreateMultipartAbortRoundTripCase(),
        CreateCopyObjectRoundTripCase(),
        CreateHeadBucketObjectRoundTripCase(),
        CreateListBucketsRoundTripCase(),
        CreateListObjectsV1PaginationCase(),
        CreatePresignedUrlGetPutRoundTripCase(),
        CreateBucketTaggingRoundTripCase(),
        CreateObjectLegalHoldRoundTripCase(),
        CreateObjectRetentionRoundTripCase(),
    ];
}
