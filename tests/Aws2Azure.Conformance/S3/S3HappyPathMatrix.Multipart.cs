using System.Net.Http.Headers;
using System.Text;
using Aws2Azure.Conformance.Cases;

namespace Aws2Azure.Conformance.S3;

public static partial class S3HappyPathMatrix
{
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
        // Content-MD5 is a content header, not a request header: setting it via
        // request.Headers.TryAddWithoutValidation silently fails (returns false)
        // and the header is never sent, which real AWS/Azure both reject as a
        // missing required header for DeleteObjects.
        request.Content.Headers.TryAddWithoutValidation("Content-MD5", Convert.ToBase64String(System.Security.Cryptography.MD5.HashData(body)));
        ConformanceSigV4Signer.SignHeader(
            request,
            body,
            context.AccessKeyId,
            context.SecretAccessKey,
            region: context.Region,
            // Real AWS rejects DeleteObjects with "HeadersNotSigned: content-md5"
            // unless the header is included in SignedHeaders, not just present
            // on the request — Azure's own validator does not enforce this.
            extraSignedHeaders: ["content-md5"],
            sessionToken: context.SessionToken);
        return request;
    }

    private static Uri ResolveBaseAddress(ConformanceCaseContext context)
        => context.BaseAddress ?? DefaultBaseAddress;

    private static byte[] CreateDeterministicBuffer(int length, byte fill)
    {
        var buffer = new byte[length];
        Array.Fill(buffer, fill);
        return buffer;
    }
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
                        new("Body", "Equals the 5 MiB raw part-1 buffer concatenated with the copied source bytes."),
                    ]),
                new(204, Notes: "Deletes the copied source object current version."),
                new(204, Notes: "Hard-deletes the copied source object's retained version."),
                new(204, Notes: "Deletes the completed multipart destination current version."),
                new(204, Notes: "Hard-deletes the completed multipart destination's retained version."),
            ],
            semanticAssertion:
            "The completed object must byte-match the uploaded raw part (a 5 MiB buffer, satisfying S3's non-final-part minimum-size requirement) followed by the copied source object bytes, and ListParts pagination must enumerate both staged parts exactly once before completion. DeleteBucket is not asserted here: version-level immutability rejects Delete Container via the data plane even on an empty container, so bucket cleanup is left to the nightly reaper."),
            static (context, _) =>
            {
                var bucket = context.GetProperty("bucketName") ?? ("conf-multipart-bucket-" + Guid.NewGuid().ToString("N")[..12]);
                var sourceKey = "multipart/source.txt";
                var destKey = "multipart/final.txt";
                var sourceBody = Encoding.UTF8.GetBytes("copied-source-segment");
                // AWS/Azure S3 rejects CompleteMultipartUpload with EntityTooSmall when any
                // non-final part is below the 5 MiB (5,242,880-byte) minimum part size. Part 1
                // (this raw UploadPart) precedes part 2 (the UploadPartCopy, which is last and
                // therefore exempt), so it must meet the minimum. Build a deterministic buffer
                // rather than a literal string so the size is easy to reason about and the
                // eventual byte-match assertion stays correct.
                var rawPart = CreateDeterministicBuffer(5 * 1024 * 1024, (byte)'A');
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
                new(404, Notes: "ListParts on the aborted UploadId is rejected with NoSuchUpload: aborting invalidates the upload immediately."),
                new(404, Notes: "GetObject on the never-completed key is rejected with NoSuchKey: no destination object materializes after abort."),
            ],
            semanticAssertion:
            "Aborting the upload must immediately invalidate the UploadId for subsequent multipart lookups (ListParts -> 404 NoSuchUpload) and must not materialize a destination object (GetObject -> 404 NoSuchKey)."),
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
