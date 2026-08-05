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
                new(
                    204,
                    Notes: "DeleteObject returns an empty success response."),
                new(204, Notes: "DeleteBucket returns an empty success response once the bucket is empty."),
            ],
            semanticAssertion:
            "The body returned by GetObject must byte-match the earlier PutObject payload."),
            static (context, _) =>
            {
                var bucket = "conf-happy-bucket-" + Guid.NewGuid().ToString("N")[..12];
                var key = "roundtrip/object.txt";
                var body = Encoding.UTF8.GetBytes("aws2azure conformance roundtrip payload");
                return new ValueTask<ConformanceExecutionPlan>(new ConformanceExecutionPlan(
                [
                    new ConformanceRequestStep("create-bucket", _ => BuildBucketRequest(context, HttpMethod.Put, bucket)),
                    new ConformanceRequestStep("put-object", _ => BuildObjectRequest(context, HttpMethod.Put, bucket, key, body)),
                    new ConformanceRequestStep("get-object", _ => BuildObjectRequest(context, HttpMethod.Get, bucket, key, Array.Empty<byte>())),
                    new ConformanceRequestStep("delete-object", _ => BuildObjectRequest(context, HttpMethod.Delete, bucket, key, Array.Empty<byte>())),
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
                new(204),
                new(204),
            ],
            semanticAssertion:
            "Across both pages the harness should observe each seeded key exactly once."),
            static (context, _) =>
            {
                var bucket = "conf-happy-bucket-" + Guid.NewGuid().ToString("N")[..12];
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
                    new ConformanceRequestStep("delete-object-2", _ => BuildObjectRequest(context, HttpMethod.Delete, bucket, "page/object-2.txt", Array.Empty<byte>())),
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
                new(204),
            ],
            semanticAssertion:
            "The conditional GET must reuse the ETag emitted by PutObject and still return the full object body."),
            static (context, _) =>
            {
                var bucket = "conf-happy-bucket-" + Guid.NewGuid().ToString("N")[..12];
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
                    new ConformanceRequestStep("cleanup-delete", _ => BuildObjectRequest(context, HttpMethod.Delete, bucket, key, Array.Empty<byte>())),
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
            region: context.Region);
        return request;
    }

    private static HttpRequestMessage BuildObjectRequest(
        ConformanceCaseContext context,
        HttpMethod method,
        string bucket,
        string key,
        byte[] body)
    {
        var request = new HttpRequestMessage(
            method,
            new Uri(ResolveBaseAddress(context), $"/{bucket}/{key}"));
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
            region: context.Region);
        return request;
    }

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
            region: context.Region);
        return request;
    }

    private static Uri ResolveBaseAddress(ConformanceCaseContext context)
        => context.BaseAddress ?? DefaultBaseAddress;
}
