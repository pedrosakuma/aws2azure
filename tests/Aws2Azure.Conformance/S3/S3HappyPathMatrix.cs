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
        CreateObjectTaggingRoundTripCase(),
        CreateDeleteObjectsBatchRoundTripCase(),
        CreateMultipartCopyCompleteRoundTripCase(),
        CreateMultipartAbortRoundTripCase(),
        CreateCopyObjectRoundTripCase(),
        CreateObjectRetentionRoundTripCase(),
        CreateObjectLegalHoldRoundTripCase(),
    ];

    private static PlannedConformanceCase CreateRoundTripCase()
        => new(
            "put-get-delete-object-roundtrip",
            "s3:PutObject/GetObject/DeleteObject",
            ConformanceCaseExpectation.Success(
            [
                new(200, Notes: "CreateBucket."),
                new(200, Notes: "Enables bucket versioning to match the real-Azure nightly storage account's always-on blob versioning."),
                new(
                    200,
                    RequiredHeaders: [new("ETag", "Present on the PutObject success response.")],
                    Notes: "Seed object creation for the round-trip."),
                new(
                    200,
                    RequiredHeaders: [new("ETag", "Present on the GetObject response.")],
                    RequiredBodyAssertions: [new("Body", "Equals the exact bytes uploaded by PutObject.")]),
                new(204, Notes: "Unsets the current version pointer; Azure does not create a delete-marker version."),
                new(204, Notes: "Hard-deletes the original object version created by PutObject."),
            ],
            semanticAssertion:
            "The body returned by GetObject must byte-match the earlier PutObject payload, and teardown must purge the retained object version. DeleteBucket is not asserted here: version-level immutability rejects Delete Container via the data plane even on an empty container, so bucket cleanup is left to the nightly reaper."),
            static (context, _) =>
            {
                var bucket = context.GetProperty("bucketName") ?? ("conf-happy-bucket-" + Guid.NewGuid().ToString("N")[..12]);
                var key = "roundtrip/object.txt";
                var body = Encoding.UTF8.GetBytes("aws2azure conformance roundtrip payload");
                return new ValueTask<ConformanceExecutionPlan>(new ConformanceExecutionPlan(
                [
                    new ConformanceRequestStep("create-bucket", _ => BuildBucketRequest(context, HttpMethod.Put, bucket)),
                    new ConformanceRequestStep("enable-versioning", _ => BuildEnableVersioningRequest(context, bucket)),
                    new ConformanceRequestStep("put-object", _ => BuildObjectRequest(context, HttpMethod.Put, bucket, key, body)),
                    new ConformanceRequestStep("get-object", _ => BuildObjectRequest(context, HttpMethod.Get, bucket, key, Array.Empty<byte>())),
                    new ConformanceRequestStep("delete-object", _ => BuildObjectRequest(context, HttpMethod.Delete, bucket, key, Array.Empty<byte>())),
                    new ConformanceRequestStep("delete-object-version", state => BuildVersionedDeleteRequest(
                        context,
                        bucket,
                        key,
                        state.RequireHeaderValue("put-object", "x-amz-version-id"))),
                ], Tier1SkipReason));
            });

    private static PlannedConformanceCase CreatePaginationCase()
        => new(
            "list-objects-v2-pagination",
            "s3:ListObjectsV2",
            ConformanceCaseExpectation.Success(
            [
                new(200, Notes: "CreateBucket."),
                new(200, Notes: "Enables bucket versioning to match the real-Azure nightly storage account's always-on blob versioning."),
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
                new(204),
                new(204),
                new(204),
            ],
            semanticAssertion:
            "Across both pages the harness should observe each seeded key exactly once, then purge both retained object versions. DeleteBucket is not asserted here: version-level immutability rejects Delete Container via the data plane even on an empty container, so bucket cleanup is left to the nightly reaper."),
            static (context, _) =>
            {
                var bucket = context.GetProperty("bucketName") ?? ("conf-happy-bucket-" + Guid.NewGuid().ToString("N")[..12]);
                var firstBody = Encoding.UTF8.GetBytes("page-one-object");
                var secondBody = Encoding.UTF8.GetBytes("page-two-object");
                return new ValueTask<ConformanceExecutionPlan>(new ConformanceExecutionPlan(
                [
                    new ConformanceRequestStep("create-bucket", _ => BuildBucketRequest(context, HttpMethod.Put, bucket)),
                    new ConformanceRequestStep("enable-versioning", _ => BuildEnableVersioningRequest(context, bucket)),
                    new ConformanceRequestStep("seed-object-1", _ => BuildObjectRequest(context, HttpMethod.Put, bucket, "page/object-1.txt", firstBody)),
                    new ConformanceRequestStep("seed-object-2", _ => BuildObjectRequest(context, HttpMethod.Put, bucket, "page/object-2.txt", secondBody)),
                    new ConformanceRequestStep("list-page-1", _ => BuildListObjectsRequest(context, bucket, continuationToken: null)),
                    new ConformanceRequestStep("list-page-2", state =>
                    {
                        var token = state.RequireXmlValue("list-page-1", "NextContinuationToken");
                        return BuildListObjectsRequest(context, bucket, token);
                    }),
                    new ConformanceRequestStep("delete-object-1", _ => BuildObjectRequest(context, HttpMethod.Delete, bucket, "page/object-1.txt", Array.Empty<byte>())),
                    new ConformanceRequestStep("delete-object-version-1", state => BuildVersionedDeleteRequest(
                        context,
                        bucket,
                        "page/object-1.txt",
                        state.RequireHeaderValue("seed-object-1", "x-amz-version-id"))),
                    new ConformanceRequestStep("delete-object-2", _ => BuildObjectRequest(context, HttpMethod.Delete, bucket, "page/object-2.txt", Array.Empty<byte>())),
                    new ConformanceRequestStep("delete-object-version-2", state => BuildVersionedDeleteRequest(
                        context,
                        bucket,
                        "page/object-2.txt",
                        state.RequireHeaderValue("seed-object-2", "x-amz-version-id"))),
                ], Tier1SkipReason));
            });

    private static PlannedConformanceCase CreateConditionalCase()
        => new(
            "get-object-if-match-roundtrip",
            "s3:PutObject/GetObject[If-Match]/DeleteObject",
            ConformanceCaseExpectation.Success(
            [
                new(200, Notes: "CreateBucket."),
                new(200, Notes: "Enables bucket versioning to match the real-Azure nightly storage account's always-on blob versioning."),
                new(
                    200,
                    RequiredHeaders: [new("ETag", "Present on the PutObject response and reused by If-Match.")]),
                new(
                    200,
                    RequiredHeaders: [new("ETag", "Matches the entity tag from the seed PUT.")],
                    RequiredBodyAssertions: [new("Body", "Returned because the If-Match precondition succeeded.")]),
                new(204),
                new(204),
            ],
            semanticAssertion:
            "The conditional GET must reuse the ETag emitted by PutObject and still return the full object body, and teardown must purge the retained object version. DeleteBucket is not asserted here: version-level immutability rejects Delete Container via the data plane even on an empty container, so bucket cleanup is left to the nightly reaper."),
            static (context, _) =>
            {
                var bucket = context.GetProperty("bucketName") ?? ("conf-happy-bucket-" + Guid.NewGuid().ToString("N")[..12]);
                var key = "conditional/object.txt";
                var body = Encoding.UTF8.GetBytes("aws2azure conditional object");
                return new ValueTask<ConformanceExecutionPlan>(new ConformanceExecutionPlan(
                [
                    new ConformanceRequestStep("create-bucket", _ => BuildBucketRequest(context, HttpMethod.Put, bucket)),
                    new ConformanceRequestStep("enable-versioning", _ => BuildEnableVersioningRequest(context, bucket)),
                    new ConformanceRequestStep("seed-put", _ => BuildObjectRequest(context, HttpMethod.Put, bucket, key, body)),
                    new ConformanceRequestStep("conditional-get", state =>
                    {
                        var request = BuildObjectRequest(context, HttpMethod.Get, bucket, key, Array.Empty<byte>());
                        request.Headers.TryAddWithoutValidation("If-Match", state.RequireHeaderValue("seed-put", "ETag"));
                        return request;
                    }),
                    new ConformanceRequestStep("delete-object", _ => BuildObjectRequest(context, HttpMethod.Delete, bucket, key, Array.Empty<byte>())),
                    new ConformanceRequestStep("delete-object-version", state => BuildVersionedDeleteRequest(
                        context,
                        bucket,
                        key,
                        state.RequireHeaderValue("seed-put", "x-amz-version-id"))),
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

    private static HttpRequestMessage BuildEnableVersioningRequest(
        ConformanceCaseContext context,
        string bucket)
    {
        var body = Encoding.UTF8.GetBytes(
            "<VersioningConfiguration><Status>Enabled</Status></VersioningConfiguration>");
        var request = new HttpRequestMessage(
            HttpMethod.Put,
            new Uri(ResolveBaseAddress(context), $"/{bucket}?versioning"))
        {
            Content = new ByteArrayContent(body),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
        ConformanceSigV4Signer.SignHeader(
            request,
            body,
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


    private static PlannedConformanceCase CreateObjectTaggingRoundTripCase()
        => new(
            "object-tagging-roundtrip",
            "s3:PutObject/PutObjectTagging/GetObjectTagging/DeleteObjectTagging",
            ConformanceCaseExpectation.Success(
            [
                new(200, Notes: "CreateBucket."),
                new(200, Notes: "Enables bucket versioning to match the real-Azure nightly storage account's always-on blob versioning."),
                new(200, RequiredHeaders: [new("ETag", "Present on the seed PutObject response.")]),
                new(200, RequiredHeaders: [new("x-amz-version-id", "Emitted by the proxy for object-tagging writes after resolving the blob version.")]),
                new(200,
                    RequiredHeaders: [new("x-amz-version-id", "Echoes the resolved version id for the tagged object.")],
                    RequiredBodyAssertions:
                    [
                        new("Tagging.TagSet.Tag", "Returns both tags written by PutObjectTagging."),
                    ]),
                new(204, RequiredHeaders: [new("x-amz-version-id", "Emitted by the proxy for DeleteObjectTagging after resolving the blob version.")]),
                new(200,
                    RequiredHeaders: [new("x-amz-version-id", "Still present when the tag set is empty.")],
                    RequiredBodyAssertions:
                    [
                        new("Tagging.TagSet", "Present and empty after DeleteObjectTagging."),
                    ]),
                new(204, Notes: "Deletes the tagged object current version."),
                new(204, Notes: "Hard-deletes the tagged object's retained version."),
            ],
            semanticAssertion:
            "GetObjectTagging must echo the exact tags written by PutObjectTagging, DeleteObjectTagging must clear them back to an empty TagSet, and teardown must purge the retained object version. DeleteBucket is not asserted here: version-level immutability rejects Delete Container via the data plane even on an empty container, so bucket cleanup is left to the nightly reaper."),
            static (context, _) =>
            {
                var bucket = context.GetProperty("bucketName") ?? ("conf-tagging-bucket-" + Guid.NewGuid().ToString("N")[..12]);
                var key = "tagging/object.txt";
                var body = Encoding.UTF8.GetBytes("aws2azure object tagging payload");
                var taggingBody = Encoding.UTF8.GetBytes(
                    "<Tagging><TagSet><Tag><Key>project</Key><Value>aws2azure</Value></Tag><Tag><Key>tier</Key><Value>conformance</Value></Tag></TagSet></Tagging>");
                return new ValueTask<ConformanceExecutionPlan>(new ConformanceExecutionPlan(
                [
                    new ConformanceRequestStep("create-bucket", _ => BuildBucketRequest(context, HttpMethod.Put, bucket)),
                    new ConformanceRequestStep("enable-versioning", _ => BuildEnableVersioningRequest(context, bucket)),
                    new ConformanceRequestStep("seed-object", _ => BuildObjectRequest(context, HttpMethod.Put, bucket, key, body)),
                    new ConformanceRequestStep("put-object-tagging", _ => BuildObjectSubresourceRequest(context, HttpMethod.Put, bucket, key, "tagging", taggingBody)),
                    new ConformanceRequestStep("get-object-tagging", _ => BuildObjectSubresourceRequest(context, HttpMethod.Get, bucket, key, "tagging", Array.Empty<byte>())),
                    new ConformanceRequestStep("delete-object-tagging", _ => BuildObjectSubresourceRequest(context, HttpMethod.Delete, bucket, key, "tagging", Array.Empty<byte>())),
                    new ConformanceRequestStep("get-object-tagging-after-delete", _ => BuildObjectSubresourceRequest(context, HttpMethod.Get, bucket, key, "tagging", Array.Empty<byte>())),
                    new ConformanceRequestStep("delete-object", _ => BuildObjectRequest(context, HttpMethod.Delete, bucket, key, Array.Empty<byte>())),
                    new ConformanceRequestStep("delete-object-version", state => BuildVersionedDeleteRequest(
                        context,
                        bucket,
                        key,
                        state.RequireHeaderValue("seed-object", "x-amz-version-id"))),
                ], Tier1SkipReason));
            });

    private static PlannedConformanceCase CreateDeleteObjectsBatchRoundTripCase()
        => new(
            "delete-objects-batch-roundtrip",
            "s3:PutObject/DeleteObjects/GetObject",
            ConformanceCaseExpectation.Success(
            [
                new(200, Notes: "CreateBucket."),
                new(200, Notes: "Enables bucket versioning to match the real-Azure nightly storage account's always-on blob versioning."),
                new(200, RequiredHeaders: [new("ETag", "Present on the first seed PutObject response.")]),
                new(200, RequiredHeaders: [new("ETag", "Present on the second seed PutObject response.")]),
                new(200, RequiredHeaders: [new("ETag", "Present on the third seed PutObject response.")]),
                new(200, RequiredBodyAssertions:
                    [
                        new("DeleteResult.Deleted", "Contains each seeded key exactly once in request order."),
                    ]),
                new(204, Notes: "Hard-deletes the first retained object version after DeleteObjects clears the current pointer."),
                new(204, Notes: "Hard-deletes the second retained object version after DeleteObjects clears the current pointer."),
                new(204, Notes: "Hard-deletes the third retained object version after DeleteObjects clears the current pointer."),
            ],
            semanticAssertion:
            "DeleteObjects must report every requested key as Deleted in request order, and teardown must purge the retained blob versions left behind by versioning. Follow-up NoSuchKey verification belongs to the differential capture tier, not the Tier-1 happy-path seed contract. DeleteBucket is not asserted here: version-level immutability rejects Delete Container via the data plane even on an empty container, so bucket cleanup is left to the nightly reaper."),
            static (context, _) =>
            {
                var bucket = context.GetProperty("bucketName") ?? ("conf-delete-batch-" + Guid.NewGuid().ToString("N")[..12]);
                var keys = new[] { "batch/key-1.txt", "batch/key-2.txt", "batch/key-3.txt" };
                return new ValueTask<ConformanceExecutionPlan>(new ConformanceExecutionPlan(
                [
                    new ConformanceRequestStep("create-bucket", _ => BuildBucketRequest(context, HttpMethod.Put, bucket)),
                    new ConformanceRequestStep("enable-versioning", _ => BuildEnableVersioningRequest(context, bucket)),
                    new ConformanceRequestStep("seed-object-1", _ => BuildObjectRequest(context, HttpMethod.Put, bucket, keys[0], Encoding.UTF8.GetBytes("delete-batch-1"))),
                    new ConformanceRequestStep("seed-object-2", _ => BuildObjectRequest(context, HttpMethod.Put, bucket, keys[1], Encoding.UTF8.GetBytes("delete-batch-2"))),
                    new ConformanceRequestStep("seed-object-3", _ => BuildObjectRequest(context, HttpMethod.Put, bucket, keys[2], Encoding.UTF8.GetBytes("delete-batch-3"))),
                    new ConformanceRequestStep("delete-objects", _ => BuildDeleteObjectsRequest(context, bucket, keys)),
                    new ConformanceRequestStep("delete-object-version-1", state => BuildVersionedDeleteRequest(context, bucket, keys[0], state.RequireHeaderValue("seed-object-1", "x-amz-version-id"))),
                    new ConformanceRequestStep("delete-object-version-2", state => BuildVersionedDeleteRequest(context, bucket, keys[1], state.RequireHeaderValue("seed-object-2", "x-amz-version-id"))),
                    new ConformanceRequestStep("delete-object-version-3", state => BuildVersionedDeleteRequest(context, bucket, keys[2], state.RequireHeaderValue("seed-object-3", "x-amz-version-id"))),
                ], Tier1SkipReason));
            });

    private static PlannedConformanceCase CreateObjectRetentionRoundTripCase()
        => new(
            "object-retention-roundtrip",
            "s3:PutObject/PutObjectRetention/GetObjectRetention",
            ConformanceCaseExpectation.Success(
            [
                new(200, Notes: "CreateBucket."),
                new(200, Notes: "Enables bucket versioning to match the real-Azure nightly storage account's always-on blob versioning."),
                new(200, RequiredHeaders: [new("ETag", "Present on the seed PutObject response.")]),
                new(200, Notes: "Applies a COMPLIANCE retention policy to the current object version."),
                new(200,
                    RequiredBodyAssertions:
                    [
                        new("Retention.Mode", "Returns COMPLIANCE for the locked Azure immutability policy."),
                        new("Retention.RetainUntilDate", "Returns the retain-until timestamp written by PutObjectRetention."),
                    ]),
            ],
            semanticAssertion:
            "PutObjectRetention must round-trip a COMPLIANCE retain-until policy through GetObjectRetention when the backend account has version-level immutability enabled. This seed omits teardown because retained objects cannot be shortened or removed through the S3 surface once COMPLIANCE is applied; the nightly reaper handles the bucket lifecycle externally."),
            static (context, _) =>
            {
                var bucket = context.GetProperty("bucketName") ?? ("conf-retention-bucket-" + Guid.NewGuid().ToString("N")[..12]);
                var key = "retention/object.txt";
                var body = Encoding.UTF8.GetBytes("retention-payload");
                var retentionBody = Encoding.UTF8.GetBytes(
                    "<Retention><Mode>COMPLIANCE</Mode><RetainUntilDate>2035-01-02T03:04:05Z</RetainUntilDate></Retention>");
                return new ValueTask<ConformanceExecutionPlan>(new ConformanceExecutionPlan(
                [
                    new ConformanceRequestStep("create-bucket", _ => BuildBucketRequest(context, HttpMethod.Put, bucket)),
                    new ConformanceRequestStep("enable-versioning", _ => BuildEnableVersioningRequest(context, bucket)),
                    new ConformanceRequestStep("seed-object", _ => BuildObjectRequest(context, HttpMethod.Put, bucket, key, body)),
                    new ConformanceRequestStep("put-object-retention", _ => BuildObjectSubresourceRequest(context, HttpMethod.Put, bucket, key, "retention", retentionBody)),
                    new ConformanceRequestStep("get-object-retention", _ => BuildObjectSubresourceRequest(context, HttpMethod.Get, bucket, key, "retention", Array.Empty<byte>())),
                ], Tier1SkipReason));
            });

    private static PlannedConformanceCase CreateObjectLegalHoldRoundTripCase()
        => new(
            "object-legal-hold-roundtrip",
            "s3:PutObject/PutObjectLegalHold/GetObjectLegalHold",
            ConformanceCaseExpectation.Success(
            [
                new(200, Notes: "CreateBucket."),
                new(200, Notes: "Enables bucket versioning to match the real-Azure nightly storage account's always-on blob versioning."),
                new(200, RequiredHeaders: [new("ETag", "Present on the seed PutObject response.")]),
                new(200, Notes: "Applies an ON legal hold to the current object version."),
                new(200,
                    RequiredBodyAssertions:
                    [
                        new("LegalHold.Status", "Returns ON after PutObjectLegalHold sets the Azure blob legal-hold flag."),
                    ]),
            ],
            semanticAssertion:
            "PutObjectLegalHold must flip the blob legal-hold flag on and GetObjectLegalHold must report ON when the backend account supports real Azure legal hold. This seed omits teardown because clearing the hold would require an additional scenario variant and the nightly environment treats these cases as evidence capture rather than bucket cleanup ownership."),
            static (context, _) =>
            {
                var bucket = context.GetProperty("bucketName") ?? ("conf-legal-hold-bucket-" + Guid.NewGuid().ToString("N")[..12]);
                var key = "legal-hold/object.txt";
                var body = Encoding.UTF8.GetBytes("legal-hold-payload");
                var legalHoldBody = Encoding.UTF8.GetBytes("<LegalHold><Status>ON</Status></LegalHold>");
                return new ValueTask<ConformanceExecutionPlan>(new ConformanceExecutionPlan(
                [
                    new ConformanceRequestStep("create-bucket", _ => BuildBucketRequest(context, HttpMethod.Put, bucket)),
                    new ConformanceRequestStep("enable-versioning", _ => BuildEnableVersioningRequest(context, bucket)),
                    new ConformanceRequestStep("seed-object", _ => BuildObjectRequest(context, HttpMethod.Put, bucket, key, body)),
                    new ConformanceRequestStep("put-object-legal-hold", _ => BuildObjectSubresourceRequest(context, HttpMethod.Put, bucket, key, "legal-hold", legalHoldBody)),
                    new ConformanceRequestStep("get-object-legal-hold", _ => BuildObjectSubresourceRequest(context, HttpMethod.Get, bucket, key, "legal-hold", Array.Empty<byte>())),
                ], Tier1SkipReason));
            });


    private static HttpRequestMessage BuildObjectSubresourceRequest(
        ConformanceCaseContext context,
        HttpMethod method,
        string bucket,
        string key,
        string subresource,
        byte[] body)
    {
        var request = new HttpRequestMessage(
            method,
            new Uri(ResolveBaseAddress(context), $"/{bucket}/{key}?{subresource}"));
        if (body.Length > 0)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentLength = body.Length;
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
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

    private static HttpRequestMessage BuildDeleteObjectsRequest(
        ConformanceCaseContext context,
        string bucket,
        IReadOnlyList<string> keys)
    {
        var xml = "<Delete>" + string.Concat(keys.Select(key => $"<Object><Key>{key}</Key></Object>")) + "</Delete>";
        var body = Encoding.UTF8.GetBytes(xml);
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(ResolveBaseAddress(context), $"/{bucket}?delete"))
        {
            Content = new ByteArrayContent(body),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
        request.Content.Headers.ContentLength = body.Length;
        request.Headers.TryAddWithoutValidation("Content-MD5", Convert.ToBase64String(System.Security.Cryptography.MD5.HashData(body)));
        ConformanceSigV4Signer.SignHeader(
            request,
            body,
            context.AccessKeyId,
            context.SecretAccessKey,
            region: context.Region,
            sessionToken: context.SessionToken);
        return request;
    }

    private static Uri ResolveBaseAddress(ConformanceCaseContext context)
        => context.BaseAddress ?? DefaultBaseAddress;

    private static PlannedConformanceCase CreateMultipartCopyCompleteRoundTripCase()
        => new(
            "multipart-upload-copy-complete-roundtrip",
            "s3:CreateMultipartUpload/UploadPart/UploadPartCopy/ListParts/CompleteMultipartUpload/GetObject",
            ConformanceCaseExpectation.Success(
            [
                new(200, Notes: "CreateBucket."),
                new(200, Notes: "Enables bucket versioning to match the real-Azure nightly storage account's always-on blob versioning."),
                new(200, RequiredHeaders: [new("ETag", "Present on the seed PutObject response used as the UploadPartCopy source.")]),
                new(
                    200,
                    RequiredBodyAssertions:
                    [
                        new("InitiateMultipartUploadResult.UploadId", "Present and reused by all multipart follow-up requests."),
                    ]),
                new(200, RequiredHeaders: [new("ETag", "Present on UploadPart and echoed into CompleteMultipartUpload.")]),
                new(
                    200,
                    RequiredBodyAssertions:
                    [
                        new("CopyPartResult.ETag", "Present on UploadPartCopy and echoed into CompleteMultipartUpload."),
                        new("CopyPartResult.LastModified", "Present on UploadPartCopy responses."),
                    ]),
                new(
                    200,
                    RequiredBodyAssertions:
                    [
                        new("ListPartsResult.IsTruncated", "True when max-parts=1 and additional parts remain."),
                        new("ListPartsResult.NextPartNumberMarker", "Present on the first paged ListParts response."),
                    ]),
                new(
                    200,
                    RequiredBodyAssertions:
                    [
                        new("ListPartsResult.Part", "Returns the remaining uploaded parts on the second page."),
                    ]),
                new(
                    200,
                    RequiredBodyAssertions:
                    [
                        new("CompleteMultipartUploadResult.ETag", "Present and shaped like an S3 multipart ETag."),
                    ]),
                new(
                    200,
                    RequiredHeaders: [new("ETag", "Present on the final GetObject response.")],
                    RequiredBodyAssertions:
                    [
                        new("Body", "Equals raw-part-1 concatenated with the copied source bytes."),
                    ]),
                new(204, Notes: "Deletes the copied source object current version."),
                new(204, Notes: "Hard-deletes the copied source object's retained version."),
                new(204, Notes: "Deletes the completed multipart destination current version."),
                new(204, Notes: "Hard-deletes the completed multipart destination's retained version."),
            ],
            semanticAssertion:
            "The completed object must byte-match the uploaded raw part followed by the copied source object bytes, and ListParts pagination must enumerate both staged parts exactly once before completion. DeleteBucket is not asserted here: version-level immutability rejects Delete Container via the data plane even on an empty container, so bucket cleanup is left to the nightly reaper."),
            static (context, _) =>
            {
                var bucket = context.GetProperty("bucketName") ?? ("conf-multipart-bucket-" + Guid.NewGuid().ToString("N")[..12]);
                var sourceKey = "multipart/source.txt";
                var destKey = "multipart/final.txt";
                var sourceBody = Encoding.UTF8.GetBytes("copied-source-segment");
                var rawPart = Encoding.UTF8.GetBytes("raw-part-1|");
                return new ValueTask<ConformanceExecutionPlan>(new ConformanceExecutionPlan(
                [
                    new ConformanceRequestStep("create-bucket", _ => BuildBucketRequest(context, HttpMethod.Put, bucket)),
                    new ConformanceRequestStep("enable-versioning", _ => BuildEnableVersioningRequest(context, bucket)),
                    new ConformanceRequestStep("seed-copy-source", _ => BuildObjectRequest(context, HttpMethod.Put, bucket, sourceKey, sourceBody)),
                    new ConformanceRequestStep("create-multipart-upload", _ => BuildCreateMultipartUploadRequest(context, bucket, destKey)),
                    new ConformanceRequestStep("upload-part-1", state => BuildUploadPartRequest(
                        context,
                        bucket,
                        destKey,
                        state.RequireXmlValue("create-multipart-upload", "UploadId"),
                        1,
                        rawPart)),
                    new ConformanceRequestStep("upload-part-copy-2", state => BuildUploadPartCopyRequest(
                        context,
                        bucket,
                        destKey,
                        state.RequireXmlValue("create-multipart-upload", "UploadId"),
                        2,
                        bucket,
                        sourceKey)),
                    new ConformanceRequestStep("list-parts-page-1", state => BuildListPartsRequest(
                        context,
                        bucket,
                        destKey,
                        state.RequireXmlValue("create-multipart-upload", "UploadId"),
                        maxParts: 1,
                        partNumberMarker: null)),
                    new ConformanceRequestStep("list-parts-page-2", state => BuildListPartsRequest(
                        context,
                        bucket,
                        destKey,
                        state.RequireXmlValue("create-multipart-upload", "UploadId"),
                        maxParts: 1000,
                        partNumberMarker: int.Parse(state.RequireXmlValue("list-parts-page-1", "NextPartNumberMarker"), System.Globalization.CultureInfo.InvariantCulture))),
                    new ConformanceRequestStep("complete-multipart-upload", state => BuildCompleteMultipartUploadRequest(
                        context,
                        bucket,
                        destKey,
                        state.RequireXmlValue("create-multipart-upload", "UploadId"),
                        [
                            (1, state.RequireHeaderValue("upload-part-1", "ETag")),
                            (2, state.RequireXmlValue("upload-part-copy-2", "ETag")),
                        ])),
                    new ConformanceRequestStep("get-completed-object", _ => BuildObjectRequest(context, HttpMethod.Get, bucket, destKey, Array.Empty<byte>())),
                    new ConformanceRequestStep("delete-copy-source", _ => BuildObjectRequest(context, HttpMethod.Delete, bucket, sourceKey, Array.Empty<byte>())),
                    new ConformanceRequestStep("delete-copy-source-version", state => BuildVersionedDeleteRequest(
                        context,
                        bucket,
                        sourceKey,
                        state.RequireHeaderValue("seed-copy-source", "x-amz-version-id"))),
                    new ConformanceRequestStep("delete-completed-object", _ => BuildObjectRequest(context, HttpMethod.Delete, bucket, destKey, Array.Empty<byte>())),
                    new ConformanceRequestStep("delete-completed-object-version", state => BuildVersionedDeleteRequest(
                        context,
                        bucket,
                        destKey,
                        state.RequireHeaderValue("get-completed-object", "x-amz-version-id"))),
                ], Tier1SkipReason));
            });

    private static PlannedConformanceCase CreateMultipartAbortRoundTripCase()
        => new(
            "multipart-upload-abort-roundtrip",
            "s3:CreateMultipartUpload/UploadPart/AbortMultipartUpload/ListParts/GetObject",
            ConformanceCaseExpectation.Success(
            [
                new(200, Notes: "CreateBucket."),
                new(200, Notes: "Enables bucket versioning to match the real-Azure nightly storage account's always-on blob versioning."),
                new(
                    200,
                    RequiredBodyAssertions:
                    [
                        new("InitiateMultipartUploadResult.UploadId", "Present and reused by UploadPart and AbortMultipartUpload."),
                    ]),
                new(200, RequiredHeaders: [new("ETag", "Present on the staged UploadPart response.")]),
                new(204, Notes: "AbortMultipartUpload succeeds and invalidates the UploadId immediately."),
                new(200, Notes: "Tier-1 seed placeholder: Tier-3 capture validates the post-abort ListParts rejection semantics."),
                new(200, Notes: "Tier-1 seed placeholder: Tier-3 capture validates that no completed object materializes after abort."),
            ],
            semanticAssertion:
            "Aborting the upload must immediately invalidate the UploadId for subsequent multipart lookups and must not materialize a destination object; the seed matrix leaves those negative post-abort checks as live Tier-3 assertions because Tier-1 only validates the happy-path planning scaffold."),
            static (context, _) =>
            {
                var bucket = context.GetProperty("bucketName") ?? ("conf-multipart-bucket-" + Guid.NewGuid().ToString("N")[..12]);
                var key = "multipart/aborted.txt";
                var body = Encoding.UTF8.GetBytes("staged-but-aborted");
                return new ValueTask<ConformanceExecutionPlan>(new ConformanceExecutionPlan(
                [
                    new ConformanceRequestStep("create-bucket", _ => BuildBucketRequest(context, HttpMethod.Put, bucket)),
                    new ConformanceRequestStep("enable-versioning", _ => BuildEnableVersioningRequest(context, bucket)),
                    new ConformanceRequestStep("create-multipart-upload", _ => BuildCreateMultipartUploadRequest(context, bucket, key)),
                    new ConformanceRequestStep("upload-part-1", state => BuildUploadPartRequest(
                        context,
                        bucket,
                        key,
                        state.RequireXmlValue("create-multipart-upload", "UploadId"),
                        1,
                        body)),
                    new ConformanceRequestStep("abort-multipart-upload", state => BuildAbortMultipartUploadRequest(
                        context,
                        bucket,
                        key,
                        state.RequireXmlValue("create-multipart-upload", "UploadId"))),
                    new ConformanceRequestStep("list-parts-after-abort", state => BuildListPartsRequest(
                        context,
                        bucket,
                        key,
                        state.RequireXmlValue("create-multipart-upload", "UploadId"),
                        maxParts: 1000,
                        partNumberMarker: null)),
                    new ConformanceRequestStep("get-object-after-abort", _ => BuildObjectRequest(context, HttpMethod.Get, bucket, key, Array.Empty<byte>())),
                ], Tier1SkipReason));
            });

    private static PlannedConformanceCase CreateCopyObjectRoundTripCase()
        => new(
            "copy-object-roundtrip",
            "s3:PutObject/CopyObject/GetObject",
            ConformanceCaseExpectation.Success(
            [
                new(200, Notes: "CreateBucket."),
                new(200, Notes: "Enables bucket versioning to match the real-Azure nightly storage account's always-on blob versioning."),
                new(200, RequiredHeaders: [new("ETag", "Present on the seed PutObject response.")]),
                new(
                    200,
                    RequiredBodyAssertions:
                    [
                        new("CopyObjectResult.ETag", "Present on the CopyObject response body."),
                        new("CopyObjectResult.LastModified", "Present on the CopyObject response body."),
                    ]),
                new(
                    200,
                    RequiredHeaders: [new("ETag", "Present on the destination GetObject response.")],
                    RequiredBodyAssertions:
                    [
                        new("Body", "Equals the source object bytes uploaded before CopyObject."),
                    ]),
                new(204, Notes: "Deletes the source object current version."),
                new(204, Notes: "Hard-deletes the source object's retained version."),
                new(204, Notes: "Deletes the copied destination current version."),
                new(204, Notes: "Hard-deletes the copied destination's retained version."),
            ],
            semanticAssertion:
            "The destination GetObject body must byte-match the earlier source PutObject payload, and teardown must purge both retained object versions. DeleteBucket is not asserted here: version-level immutability rejects Delete Container via the data plane even on an empty container, so bucket cleanup is left to the nightly reaper."),
            static (context, _) =>
            {
                var bucket = context.GetProperty("bucketName") ?? ("conf-copy-bucket-" + Guid.NewGuid().ToString("N")[..12]);
                var sourceKey = "copy/source.txt";
                var destKey = "copy/destination.txt";
                var body = Encoding.UTF8.GetBytes("copy-object-roundtrip-payload");
                return new ValueTask<ConformanceExecutionPlan>(new ConformanceExecutionPlan(
                [
                    new ConformanceRequestStep("create-bucket", _ => BuildBucketRequest(context, HttpMethod.Put, bucket)),
                    new ConformanceRequestStep("enable-versioning", _ => BuildEnableVersioningRequest(context, bucket)),
                    new ConformanceRequestStep("seed-source-object", _ => BuildObjectRequest(context, HttpMethod.Put, bucket, sourceKey, body)),
                    new ConformanceRequestStep("copy-object", _ => BuildCopyObjectRequest(context, bucket, sourceKey, bucket, destKey)),
                    new ConformanceRequestStep("get-copied-object", _ => BuildObjectRequest(context, HttpMethod.Get, bucket, destKey, Array.Empty<byte>())),
                    new ConformanceRequestStep("delete-source-object", _ => BuildObjectRequest(context, HttpMethod.Delete, bucket, sourceKey, Array.Empty<byte>())),
                    new ConformanceRequestStep("delete-source-object-version", state => BuildVersionedDeleteRequest(
                        context,
                        bucket,
                        sourceKey,
                        state.RequireHeaderValue("seed-source-object", "x-amz-version-id"))),
                    new ConformanceRequestStep("delete-copied-object", _ => BuildObjectRequest(context, HttpMethod.Delete, bucket, destKey, Array.Empty<byte>())),
                    new ConformanceRequestStep("delete-copied-object-version", state => BuildVersionedDeleteRequest(
                        context,
                        bucket,
                        destKey,
                        state.RequireHeaderValue("get-copied-object", "x-amz-version-id"))),
                ], Tier1SkipReason));
            });

    private static HttpRequestMessage BuildCreateMultipartUploadRequest(
        ConformanceCaseContext context,
        string bucket,
        string key)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(ResolveBaseAddress(context), $"/{bucket}/{key}?uploads"));
        ConformanceSigV4Signer.SignHeader(
            request,
            Array.Empty<byte>(),
            context.AccessKeyId,
            context.SecretAccessKey,
            region: context.Region,
            sessionToken: context.SessionToken);
        return request;
    }

    private static HttpRequestMessage BuildUploadPartRequest(
        ConformanceCaseContext context,
        string bucket,
        string key,
        string uploadId,
        int partNumber,
        byte[] body)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Put,
            new Uri(ResolveBaseAddress(context), $"/{bucket}/{key}?uploadId={Uri.EscapeDataString(uploadId)}&partNumber={partNumber}"))
        {
            Content = new ByteArrayContent(body),
        };
        request.Content.Headers.ContentLength = body.Length;
        ConformanceSigV4Signer.SignHeader(
            request,
            body,
            context.AccessKeyId,
            context.SecretAccessKey,
            region: context.Region,
            sessionToken: context.SessionToken);
        return request;
    }

    private static HttpRequestMessage BuildUploadPartCopyRequest(
        ConformanceCaseContext context,
        string destBucket,
        string destKey,
        string uploadId,
        int partNumber,
        string sourceBucket,
        string sourceKey)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Put,
            new Uri(ResolveBaseAddress(context), $"/{destBucket}/{destKey}?uploadId={Uri.EscapeDataString(uploadId)}&partNumber={partNumber}"))
        {
            Content = new ByteArrayContent(Array.Empty<byte>()),
        };
        request.Content.Headers.ContentLength = 0;
        request.Headers.TryAddWithoutValidation(
            "x-amz-copy-source",
            "/" + sourceBucket + "/" + Uri.EscapeDataString(sourceKey).Replace("%2F", "/", StringComparison.Ordinal));
        ConformanceSigV4Signer.SignHeader(
            request,
            Array.Empty<byte>(),
            context.AccessKeyId,
            context.SecretAccessKey,
            region: context.Region,
            sessionToken: context.SessionToken);
        return request;
    }

    private static HttpRequestMessage BuildListPartsRequest(
        ConformanceCaseContext context,
        string bucket,
        string key,
        string uploadId,
        int maxParts,
        int? partNumberMarker)
    {
        var path = $"/{bucket}/{key}?uploadId={Uri.EscapeDataString(uploadId)}&max-parts={maxParts}";
        if (partNumberMarker is { } marker)
        {
            path += "&part-number-marker=" + marker.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

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

    private static HttpRequestMessage BuildCompleteMultipartUploadRequest(
        ConformanceCaseContext context,
        string bucket,
        string key,
        string uploadId,
        IReadOnlyList<(int PartNumber, string ETag)> parts)
    {
        var xml = new StringBuilder("<CompleteMultipartUpload>");
        foreach (var (partNumber, etag) in parts)
        {
            xml.Append("<Part><PartNumber>")
               .Append(partNumber.ToString(System.Globalization.CultureInfo.InvariantCulture))
               .Append("</PartNumber><ETag>")
               .Append(System.Security.SecurityElement.Escape(etag))
               .Append("</ETag></Part>");
        }
        xml.Append("</CompleteMultipartUpload>");

        var body = Encoding.UTF8.GetBytes(xml.ToString());
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(ResolveBaseAddress(context), $"/{bucket}/{key}?uploadId={Uri.EscapeDataString(uploadId)}"))
        {
            Content = new ByteArrayContent(body),
        };
        request.Content.Headers.ContentLength = body.Length;
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
        ConformanceSigV4Signer.SignHeader(
            request,
            body,
            context.AccessKeyId,
            context.SecretAccessKey,
            region: context.Region,
            sessionToken: context.SessionToken);
        return request;
    }

    private static HttpRequestMessage BuildAbortMultipartUploadRequest(
        ConformanceCaseContext context,
        string bucket,
        string key,
        string uploadId)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Delete,
            new Uri(ResolveBaseAddress(context), $"/{bucket}/{key}?uploadId={Uri.EscapeDataString(uploadId)}"));
        ConformanceSigV4Signer.SignHeader(
            request,
            Array.Empty<byte>(),
            context.AccessKeyId,
            context.SecretAccessKey,
            region: context.Region,
            sessionToken: context.SessionToken);
        return request;
    }

    private static HttpRequestMessage BuildCopyObjectRequest(
        ConformanceCaseContext context,
        string sourceBucket,
        string sourceKey,
        string destBucket,
        string destKey)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Put,
            new Uri(ResolveBaseAddress(context), $"/{destBucket}/{destKey}"))
        {
            Content = new ByteArrayContent(Array.Empty<byte>()),
        };
        request.Content.Headers.ContentLength = 0;
        request.Headers.TryAddWithoutValidation(
            "x-amz-copy-source",
            "/" + sourceBucket + "/" + Uri.EscapeDataString(sourceKey).Replace("%2F", "/", StringComparison.Ordinal));
        ConformanceSigV4Signer.SignHeader(
            request,
            Array.Empty<byte>(),
            context.AccessKeyId,
            context.SecretAccessKey,
            region: context.Region,
            sessionToken: context.SessionToken);
        return request;
    }
}
