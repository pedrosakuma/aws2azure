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
/// The real-Azure nightly storage account keeps blob versioning enabled so S3
/// bucket teardown must behave like a versioned bucket. Azure Blob Storage
/// rejects a hard delete of a version that is still the blob's *current*
/// version (403 AuthorizationPermissionMismatch), so a plain
/// <c>DeleteObject</c> must run first to unset the current-version pointer,
/// and only then can that same version be purged with a versioned
/// <c>?versionId=</c> hard delete. <c>DeleteBucket</c> still requires every
/// retained version to be removed, so these plans always pair a plain delete
/// with the matching versioned hard delete before tearing down the bucket.
/// </para>
/// </summary>
public static class S3HappyPathMatrix
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
    ];

    private static PlannedConformanceCase CreateRoundTripCase()
        => new(
            "put-get-delete-object-roundtrip",
            "s3:PutObject/GetObject/DeleteObject",
            ConformanceCaseExpectation.Success(
            [
                new(200),
                new(
                    200,
                    RequiredHeaders: [new("ETag", "Present on the PutObject success response.")],
                    Notes: "Seed object creation for the round-trip."),
                new(
                    200,
                    RequiredHeaders: [new("ETag", "Present on the GetObject response.")],
                    RequiredBodyAssertions: [new("Body", "Equals the exact bytes uploaded by PutObject.")]),
                new(204, Notes: "Unsets the current version pointer; Azure creates a new delete-marker version."),
                new(200, Notes: "Lists every retained version so the delete-marker version created above can be located."),
                new(204, Notes: "Hard-deletes the original object version created by PutObject."),
                new(204, Notes: "Hard-deletes the delete-marker version created by the plain DeleteObject above."),
                new(204, Notes: "DeleteBucket returns an empty success response once every version has been purged."),
            ],
            semanticAssertion:
            "The body returned by GetObject must byte-match the earlier PutObject payload, and teardown must purge every retained version (including the delete-marker) before DeleteBucket."),
            static (context, _) =>
            {
                var bucket = context.GetProperty("bucketName") ?? ("conf-happy-bucket-" + Guid.NewGuid().ToString("N")[..12]);
                var key = "roundtrip/object.txt";
                var body = Encoding.UTF8.GetBytes("aws2azure conformance roundtrip payload");
                return new ValueTask<ConformanceExecutionPlan>(new ConformanceExecutionPlan(
                [
                    new ConformanceRequestStep("create-bucket", _ => BuildBucketRequest(context, HttpMethod.Put, bucket)),
                    new ConformanceRequestStep("put-object", _ => BuildObjectRequest(context, HttpMethod.Put, bucket, key, body)),
                    new ConformanceRequestStep("get-object", _ => BuildObjectRequest(context, HttpMethod.Get, bucket, key, Array.Empty<byte>())),
                    new ConformanceRequestStep("delete-object", _ => BuildObjectRequest(context, HttpMethod.Delete, bucket, key, Array.Empty<byte>())),
                    new ConformanceRequestStep("list-versions", _ => BuildListObjectVersionsRequest(context, bucket)),
                    new ConformanceRequestStep("delete-object-version", state => BuildVersionedDeleteRequest(
                        context,
                        bucket,
                        key,
                        state.RequireHeaderValue("put-object", "x-amz-version-id"))),
                    new ConformanceRequestStep("delete-marker-version", state => BuildVersionedDeleteRequest(
                        context,
                        bucket,
                        key,
                        state.RequireXmlVersionIdExcluding(
                            "list-versions",
                            state.RequireHeaderValue("put-object", "x-amz-version-id")))),
                    new ConformanceRequestStep("delete-bucket", _ => BuildBucketRequest(context, HttpMethod.Delete, bucket)),
                ], Tier1SkipReason));
            });

    private static PlannedConformanceCase CreatePaginationCase()
        => new(
            "list-objects-v2-pagination",
            "s3:ListObjectsV2",
            ConformanceCaseExpectation.Success(
            [
                new(200),
                new(200),
                new(200),
                new(
                    200,
                    RequiredBodyAssertions:
                    [
                        new("ListBucketResult.IsTruncated", "True when the first page is capped to one key."),
                        new("ListBucketResult.NextContinuationToken", "Present when more keys remain."),
                    ]),
                new(
                    200,
                    RequiredBodyAssertions:
                    [
                        new("ListBucketResult.Contents", "Returns the remaining keys on page two."),
                    ]),
                new(204),
                new(200),
                new(204),
                new(204),
                new(204),
                new(200),
                new(204),
                new(204),
                new(204),
            ],
            semanticAssertion:
            "Across both pages the harness should observe each seeded key exactly once, then purge both retained object versions (including their delete-marker versions) before deleting the bucket."),
            static (context, _) =>
            {
                var bucket = context.GetProperty("bucketName") ?? ("conf-happy-bucket-" + Guid.NewGuid().ToString("N")[..12]);
                var firstBody = Encoding.UTF8.GetBytes("page-one-object");
                var secondBody = Encoding.UTF8.GetBytes("page-two-object");
                return new ValueTask<ConformanceExecutionPlan>(new ConformanceExecutionPlan(
                [
                    new ConformanceRequestStep("create-bucket", _ => BuildBucketRequest(context, HttpMethod.Put, bucket)),
                    new ConformanceRequestStep("seed-object-1", _ => BuildObjectRequest(context, HttpMethod.Put, bucket, "page/object-1.txt", firstBody)),
                    new ConformanceRequestStep("seed-object-2", _ => BuildObjectRequest(context, HttpMethod.Put, bucket, "page/object-2.txt", secondBody)),
                    new ConformanceRequestStep("list-page-1", _ => BuildListObjectsRequest(context, bucket, continuationToken: null)),
                    new ConformanceRequestStep("list-page-2", state =>
                    {
                        var token = state.RequireXmlValue("list-page-1", "NextContinuationToken");
                        return BuildListObjectsRequest(context, bucket, token);
                    }),
                    new ConformanceRequestStep("delete-object-1", _ => BuildObjectRequest(context, HttpMethod.Delete, bucket, "page/object-1.txt", Array.Empty<byte>())),
                    new ConformanceRequestStep("list-versions-1", _ => BuildListObjectVersionsRequest(context, bucket, "page/object-1.txt")),
                    new ConformanceRequestStep("delete-object-version-1", state => BuildVersionedDeleteRequest(
                        context,
                        bucket,
                        "page/object-1.txt",
                        state.RequireHeaderValue("seed-object-1", "x-amz-version-id"))),
                    new ConformanceRequestStep("delete-marker-version-1", state => BuildVersionedDeleteRequest(
                        context,
                        bucket,
                        "page/object-1.txt",
                        state.RequireXmlVersionIdExcluding(
                            "list-versions-1",
                            state.RequireHeaderValue("seed-object-1", "x-amz-version-id")))),
                    new ConformanceRequestStep("delete-object-2", _ => BuildObjectRequest(context, HttpMethod.Delete, bucket, "page/object-2.txt", Array.Empty<byte>())),
                    new ConformanceRequestStep("list-versions-2", _ => BuildListObjectVersionsRequest(context, bucket, "page/object-2.txt")),
                    new ConformanceRequestStep("delete-object-version-2", state => BuildVersionedDeleteRequest(
                        context,
                        bucket,
                        "page/object-2.txt",
                        state.RequireHeaderValue("seed-object-2", "x-amz-version-id"))),
                    new ConformanceRequestStep("delete-marker-version-2", state => BuildVersionedDeleteRequest(
                        context,
                        bucket,
                        "page/object-2.txt",
                        state.RequireXmlVersionIdExcluding(
                            "list-versions-2",
                            state.RequireHeaderValue("seed-object-2", "x-amz-version-id")))),
                    new ConformanceRequestStep("delete-bucket", _ => BuildBucketRequest(context, HttpMethod.Delete, bucket)),
                ], Tier1SkipReason));
            });

    private static PlannedConformanceCase CreateConditionalCase()
        => new(
            "get-object-if-match-roundtrip",
            "s3:PutObject/GetObject[If-Match]/DeleteObject",
            ConformanceCaseExpectation.Success(
            [
                new(200),
                new(
                    200,
                    RequiredHeaders: [new("ETag", "Present on the PutObject response and reused by If-Match.")]),
                new(
                    200,
                    RequiredHeaders: [new("ETag", "Matches the entity tag from the seed PUT.")],
                    RequiredBodyAssertions: [new("Body", "Returned because the If-Match precondition succeeded.")]),
                new(204),
                new(200),
                new(204),
                new(204),
                new(204),
            ],
            semanticAssertion:
            "The conditional GET must reuse the ETag emitted by PutObject and still return the full object body, and teardown must purge every retained version (including the delete-marker) before DeleteBucket."),
            static (context, _) =>
            {
                var bucket = context.GetProperty("bucketName") ?? ("conf-happy-bucket-" + Guid.NewGuid().ToString("N")[..12]);
                var key = "conditional/object.txt";
                var body = Encoding.UTF8.GetBytes("aws2azure conditional object");
                return new ValueTask<ConformanceExecutionPlan>(new ConformanceExecutionPlan(
                [
                    new ConformanceRequestStep("create-bucket", _ => BuildBucketRequest(context, HttpMethod.Put, bucket)),
                    new ConformanceRequestStep("seed-put", _ => BuildObjectRequest(context, HttpMethod.Put, bucket, key, body)),
                    new ConformanceRequestStep("conditional-get", state =>
                    {
                        var request = BuildObjectRequest(context, HttpMethod.Get, bucket, key, Array.Empty<byte>());
                        request.Headers.TryAddWithoutValidation("If-Match", state.RequireHeaderValue("seed-put", "ETag"));
                        return request;
                    }),
                    new ConformanceRequestStep("delete-object", _ => BuildObjectRequest(context, HttpMethod.Delete, bucket, key, Array.Empty<byte>())),
                    new ConformanceRequestStep("list-versions", _ => BuildListObjectVersionsRequest(context, bucket, key)),
                    new ConformanceRequestStep("delete-object-version", state => BuildVersionedDeleteRequest(
                        context,
                        bucket,
                        key,
                        state.RequireHeaderValue("seed-put", "x-amz-version-id"))),
                    new ConformanceRequestStep("delete-marker-version", state => BuildVersionedDeleteRequest(
                        context,
                        bucket,
                        key,
                        state.RequireXmlVersionIdExcluding(
                            "list-versions",
                            state.RequireHeaderValue("seed-put", "x-amz-version-id")))),
                    new ConformanceRequestStep("delete-bucket", _ => BuildBucketRequest(context, HttpMethod.Delete, bucket)),
                ], Tier1SkipReason));
            });

    private static HttpRequestMessage BuildBucketRequest(
        ConformanceCaseContext context,
        HttpMethod method,
        string bucket)
    {
        var request = new HttpRequestMessage(method, new Uri(ResolveBaseAddress(context), $"/{bucket}"));
        ConformanceSigV4Signer.SignHeader(
            request,
            Array.Empty<byte>(),
            context.AccessKeyId,
            context.SecretAccessKey,
            region: context.Region,
            sessionToken: context.SessionToken);
        return request;
    }

    private static HttpRequestMessage BuildObjectRequest(
        ConformanceCaseContext context,
        HttpMethod method,
        string bucket,
        string key,
        byte[] body,
        string? versionId = null)
    {
        var path = string.IsNullOrEmpty(versionId)
            ? $"/{bucket}/{key}"
            : $"/{bucket}/{key}?versionId={Uri.EscapeDataString(versionId)}";
        var request = new HttpRequestMessage(
            method,
            new Uri(ResolveBaseAddress(context), path));
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
            region: context.Region,
            sessionToken: context.SessionToken);
        return request;
    }

    private static HttpRequestMessage BuildVersionedDeleteRequest(
        ConformanceCaseContext context,
        string bucket,
        string key,
        string versionId)
        => BuildObjectRequest(
            context,
            HttpMethod.Delete,
            bucket,
            key,
            Array.Empty<byte>(),
            versionId);

    private static HttpRequestMessage BuildListObjectsRequest(
        ConformanceCaseContext context,
        string bucket,
        string? continuationToken)
    {
        var path = continuationToken is null
            ? $"/{bucket}?list-type=2&max-keys=1"
            : $"/{bucket}?list-type=2&max-keys=1&continuation-token={Uri.EscapeDataString(continuationToken)}";

        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(ResolveBaseAddress(context), path));
        ConformanceSigV4Signer.SignHeader(
            request,
            Array.Empty<byte>(),
            context.AccessKeyId,
            context.SecretAccessKey,
            region: context.Region,
            sessionToken: context.SessionToken);
        return request;
    }

    private static HttpRequestMessage BuildListObjectVersionsRequest(
        ConformanceCaseContext context,
        string bucket,
        string? prefix = null)
    {
        var path = string.IsNullOrEmpty(prefix)
            ? $"/{bucket}?versions"
            : $"/{bucket}?versions&prefix={Uri.EscapeDataString(prefix)}";

        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(ResolveBaseAddress(context), path));
        ConformanceSigV4Signer.SignHeader(
            request,
            Array.Empty<byte>(),
            context.AccessKeyId,
            context.SecretAccessKey,
            region: context.Region,
            sessionToken: context.SessionToken);
        return request;
    }

    private static Uri ResolveBaseAddress(ConformanceCaseContext context)
        => context.BaseAddress ?? DefaultBaseAddress;
}
