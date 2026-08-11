using System.Net.Http.Headers;
using System.Text;
using Aws2Azure.Conformance.Cases;

namespace Aws2Azure.Conformance.S3;

public static partial class S3HappyPathMatrix
{
    private static PlannedConformanceCase CreateHeadBucketObjectRoundTripCase()
        => new(
            "head-bucket-object-roundtrip",
            "s3:HeadBucket/HeadObject",
            ConformanceCaseExpectation.Success(
            [
                new(200, Notes: "CreateBucket."),
                new(200, Notes: "Enables bucket versioning to match the real-Azure nightly storage account's always-on blob versioning."),
                new(200, RequiredHeaders: [new("ETag", "Present on the seed PutObject success response.")]),
                new(200, Notes: "HeadBucket returns 200 with an empty body."),
                new(
                    200,
                    RequiredHeaders:
                    [
                        new("ETag", "Present on HeadObject responses."),
                        new("Last-Modified", "Present on HeadObject responses."),
                        new("Content-Length", "Present on HeadObject responses."),
                    ],
                    Notes: "HeadObject returns metadata headers with an empty body."),
                new(204, Notes: "Unsets the current version pointer; Azure does not create a delete-marker version."),
                new(204, Notes: "Hard-deletes the original object version created by PutObject."),
            ],
            semanticAssertion:
            "HeadBucket must stay bodyless while proving the bucket exists, and HeadObject must stay bodyless while returning object metadata headers only. Teardown purges the retained object version; DeleteBucket remains intentionally unasserted because immutable-storage-with-versioning rejects data-plane bucket deletion."),
            static (context, _) =>
            {
                var bucket = context.GetProperty("bucketName") ?? ("conf-head-bucket-" + Guid.NewGuid().ToString("N")[..12]);
                var key = "head/object.txt";
                var body = Encoding.UTF8.GetBytes("aws2azure head object payload");
                return new ValueTask<ConformanceExecutionPlan>(new ConformanceExecutionPlan(
                [
                    new ConformanceRequestStep("create-bucket", _ => BuildBucketRequest(context, HttpMethod.Put, bucket)),
                    new ConformanceRequestStep("enable-versioning", _ => BuildEnableVersioningRequest(context, bucket)),
                    new ConformanceRequestStep("seed-object", _ => BuildObjectRequest(context, HttpMethod.Put, bucket, key, body)),
                    new ConformanceRequestStep("head-bucket", _ => BuildBucketRequest(context, HttpMethod.Head, bucket)),
                    new ConformanceRequestStep("head-object", _ => BuildObjectRequest(context, HttpMethod.Head, bucket, key, Array.Empty<byte>())),
                    new ConformanceRequestStep("delete-object", _ => BuildObjectRequest(context, HttpMethod.Delete, bucket, key, Array.Empty<byte>())),
                    new ConformanceRequestStep("delete-object-version", state => BuildVersionedDeleteRequest(
                        context,
                        bucket,
                        key,
                        state.RequireHeaderValue("seed-object", "x-amz-version-id"))),
                ], Tier1SkipReason));
            });
    private static PlannedConformanceCase CreateListBucketsRoundTripCase()
        => new(
            "list-buckets-roundtrip",
            "s3:ListBuckets",
            ConformanceCaseExpectation.Success(
            [
                new(200, Notes: "Create the first uniquely-prefixed bucket."),
                new(200, Notes: "Create the second uniquely-prefixed bucket."),
                new(
                    200,
                    RequiredBodyAssertions:
                    [
                        new("ListAllMyBucketsResult.Owner.ID", "Present and derived from the authenticated access key."),
                        new("ListAllMyBucketsResult.Buckets.Bucket.Name", "Contains both uniquely-prefixed buckets created by this case."),
                        new("ListAllMyBucketsResult.Buckets.Bucket.CreationDate", "Present for each listed bucket."),
                    ]),
            ],
            semanticAssertion:
            "ListBuckets is account-global and therefore intentionally asserted as membership-only: the response may contain unrelated buckets from parallel runs, but it must contain every uniquely-prefixed bucket created by this case with CreationDate populated. No teardown bucket deletion is asserted because the nightly real-Azure account forbids data-plane DeleteBucket under immutable-storage-with-versioning."),
            static (context, _) =>
            {
                var prefix = context.GetProperty("bucketPrefix") ?? ("conf-list-buckets-" + Guid.NewGuid().ToString("N")[..10]);
                var bucketOne = prefix + "-a";
                var bucketTwo = prefix + "-b";
                return new ValueTask<ConformanceExecutionPlan>(new ConformanceExecutionPlan(
                [
                    new ConformanceRequestStep("create-bucket-1", _ => BuildBucketRequest(context, HttpMethod.Put, bucketOne)),
                    new ConformanceRequestStep("create-bucket-2", _ => BuildBucketRequest(context, HttpMethod.Put, bucketTwo)),
                    new ConformanceRequestStep("list-buckets", _ => BuildListBucketsRequest(context)),
                ], Tier1SkipReason));
            });
    private static PlannedConformanceCase CreateListObjectsV1PaginationCase()
        => new(
            "list-objects-v1-pagination",
            "s3:ListObjects",
            ConformanceCaseExpectation.Success(
            [
                new(200, Notes: "CreateBucket."),
                new(200, Notes: "Enables bucket versioning to match the real-Azure nightly storage account's always-on blob versioning."),
                new(200, RequiredHeaders: [new("ETag", "Present on the first seed PutObject response.")]),
                new(200, RequiredHeaders: [new("ETag", "Present on the second seed PutObject response.")]),
                new(200, RequiredHeaders: [new("ETag", "Present on the third seed PutObject response.")]),
                new(
                    200,
                    RequiredBodyAssertions:
                    [
                        new("ListBucketResult.IsTruncated", "True when the first page is capped to one prefix-sized entry."),
                        new("ListBucketResult.NextMarker", "Present on the first page because delimiter-aware V1 pagination has more results."),
                        new("ListBucketResult.CommonPrefixes.Prefix", "Returns the first prefix bucketed by the delimiter."),
                    ]),
                new(
                    200,
                    RequiredBodyAssertions:
                    [
                        new("ListBucketResult.IsTruncated", "True on page two because one more prefix still remains after max-keys=1."),
                        new("ListBucketResult.NextMarker", "Present on page two so the final prefix can be fetched."),
                        new("ListBucketResult.CommonPrefixes.Prefix", "Returns the middle seeded prefix on page two."),
                    ]),
                new(
                    200,
                    RequiredBodyAssertions:
                    [
                        new("ListBucketResult.IsTruncated", "False on the terminal page once the final prefix is returned."),
                        new("ListBucketResult.CommonPrefixes.Prefix", "Returns the final seeded prefix on page three."),
                    ]),
                new(204),
                new(204),
                new(204),
                new(204),
                new(204),
                new(204),
            ],
            semanticAssertion:
            "The V1 marker-based listing must page via NextMarker only because a delimiter is set, and across all three pages the harness should observe each seeded prefix exactly once without asserting exact XML ordering beyond that semantic contract. Teardown purges all retained object versions; DeleteBucket remains intentionally unasserted because immutable-storage-with-versioning rejects data-plane bucket deletion."),
            static (context, _) =>
            {
                var bucket = context.GetProperty("bucketName") ?? ("conf-listv1-bucket-" + Guid.NewGuid().ToString("N")[..12]);
                var keys = new[]
                {
                    "page/a/item.txt",
                    "page/b/item.txt",
                    "page/c/item.txt",
                };
                return new ValueTask<ConformanceExecutionPlan>(new ConformanceExecutionPlan(
                [
                    new ConformanceRequestStep("create-bucket", _ => BuildBucketRequest(context, HttpMethod.Put, bucket)),
                    new ConformanceRequestStep("enable-versioning", _ => BuildEnableVersioningRequest(context, bucket)),
                    new ConformanceRequestStep("seed-object-1", _ => BuildObjectRequest(context, HttpMethod.Put, bucket, keys[0], Encoding.UTF8.GetBytes("v1-page-1"))),
                    new ConformanceRequestStep("seed-object-2", _ => BuildObjectRequest(context, HttpMethod.Put, bucket, keys[1], Encoding.UTF8.GetBytes("v1-page-2"))),
                    new ConformanceRequestStep("seed-object-3", _ => BuildObjectRequest(context, HttpMethod.Put, bucket, keys[2], Encoding.UTF8.GetBytes("v1-page-3"))),
                    new ConformanceRequestStep("list-page-1", _ => BuildListObjectsV1Request(context, bucket, marker: null)),
                    new ConformanceRequestStep("list-page-2", state => BuildListObjectsV1Request(
                        context,
                        bucket,
                        state.RequireXmlValue("list-page-1", "NextMarker"))),
                    new ConformanceRequestStep("list-page-3", state => BuildListObjectsV1Request(
                        context,
                        bucket,
                        state.RequireXmlValue("list-page-2", "NextMarker"))),
                    new ConformanceRequestStep("delete-object-1", _ => BuildObjectRequest(context, HttpMethod.Delete, bucket, keys[0], Array.Empty<byte>())),
                    new ConformanceRequestStep("delete-object-version-1", state => BuildVersionedDeleteRequest(context, bucket, keys[0], state.RequireHeaderValue("seed-object-1", "x-amz-version-id"))),
                    new ConformanceRequestStep("delete-object-2", _ => BuildObjectRequest(context, HttpMethod.Delete, bucket, keys[1], Array.Empty<byte>())),
                    new ConformanceRequestStep("delete-object-version-2", state => BuildVersionedDeleteRequest(context, bucket, keys[1], state.RequireHeaderValue("seed-object-2", "x-amz-version-id"))),
                    new ConformanceRequestStep("delete-object-3", _ => BuildObjectRequest(context, HttpMethod.Delete, bucket, keys[2], Array.Empty<byte>())),
                    new ConformanceRequestStep("delete-object-version-3", state => BuildVersionedDeleteRequest(context, bucket, keys[2], state.RequireHeaderValue("seed-object-3", "x-amz-version-id"))),
                ], Tier1SkipReason));
            });
    private static HttpRequestMessage BuildListBucketsRequest(ConformanceCaseContext context)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, ResolveBaseAddress(context));
        ConformanceSigV4Signer.SignHeader(
            request,
            Array.Empty<byte>(),
            context.AccessKeyId,
            context.SecretAccessKey,
            region: context.Region,
            sessionToken: context.SessionToken);
        return request;
    }
    private static HttpRequestMessage BuildListObjectsV1Request(
        ConformanceCaseContext context,
        string bucket,
        string? marker)
    {
        var path = marker is null
            ? $"/{bucket}?delimiter=%2F&max-keys=1&prefix=page%2F"
            : $"/{bucket}?delimiter=%2F&max-keys=1&prefix=page%2F&marker={Uri.EscapeDataString(marker)}";

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
}
