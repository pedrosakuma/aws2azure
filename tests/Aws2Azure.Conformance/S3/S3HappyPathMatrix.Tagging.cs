using System.Net.Http.Headers;
using System.Text;
using Aws2Azure.Conformance.Cases;

namespace Aws2Azure.Conformance.S3;

public static partial class S3HappyPathMatrix
{
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
    private static PlannedConformanceCase CreatePresignedUrlGetPutRoundTripCase()
        => new(
            "presigned-url-get-put-roundtrip",
            "s3:PresignedPutObject/PresignedGetObject",
            ConformanceCaseExpectation.Success(
            [
                new(200, Notes: "CreateBucket."),
                new(200, Notes: "Enables bucket versioning to match the real-Azure nightly storage account's always-on blob versioning."),
                new(200, RequiredHeaders: [new("ETag", "Present on the presigned PUT response even though only the request envelope (not the body) is signature-protected.")]),
                new(
                    200,
                    RequiredHeaders: [new("ETag", "Present on the presigned GET response.")],
                    RequiredBodyAssertions: [new("Body", "Equals the exact bytes uploaded via the presigned PUT URL.")]),
                new(204, Notes: "Unsets the current version pointer; Azure does not create a delete-marker version."),
                new(204, Notes: "Hard-deletes the original object version created by the presigned PUT."),
            ],
            semanticAssertion:
            "A presigned PUT URL generated with no SigV4 Authorization header (query-string-only auth) must be accepted by the proxy and produce a byte-identical object retrievable via a presigned GET URL. Both URLs are generated exactly as boto3.generate_presigned_url/AWSSDK.S3 GetPreSignedURL would, and are replayed as bare HTTP calls carrying only the presigned query string — never a header-based Authorization. DeleteBucket is not asserted here: version-level immutability rejects Delete Container via the data plane even on an empty container, so bucket cleanup is left to the nightly reaper."),
            static (context, _) =>
            {
                var bucket = context.GetProperty("bucketName") ?? ("conf-presigned-bucket-" + Guid.NewGuid().ToString("N")[..12]);
                var key = "presigned/object.txt";
                var body = Encoding.UTF8.GetBytes("aws2azure presigned url roundtrip payload");
                return new ValueTask<ConformanceExecutionPlan>(new ConformanceExecutionPlan(
                [
                    new ConformanceRequestStep("create-bucket", _ => BuildBucketRequest(context, HttpMethod.Put, bucket)),
                    new ConformanceRequestStep("enable-versioning", _ => BuildEnableVersioningRequest(context, bucket)),
                    new ConformanceRequestStep("presigned-put-object", _ => BuildPresignedObjectRequest(context, HttpMethod.Put, bucket, key, body)),
                    new ConformanceRequestStep("presigned-get-object", _ => BuildPresignedObjectRequest(context, HttpMethod.Get, bucket, key, Array.Empty<byte>())),
                    new ConformanceRequestStep("delete-object", _ => BuildObjectRequest(context, HttpMethod.Delete, bucket, key, Array.Empty<byte>())),
                    new ConformanceRequestStep("delete-object-version", state => BuildVersionedDeleteRequest(
                        context,
                        bucket,
                        key,
                        state.RequireHeaderValue("presigned-put-object", "x-amz-version-id"))),
                ], Tier1SkipReason));
            });
    private static PlannedConformanceCase CreateBucketTaggingRoundTripCase()
        => new(
            "bucket-tagging-roundtrip",
            "s3:PutBucketTagging/GetBucketTagging/DeleteBucketTagging",
            ConformanceCaseExpectation.Success(
            [
                new(200, Notes: "CreateBucket."),
                new(204, Notes: "PutBucketTagging replaces the whole bucket tag set."),
                new(
                    200,
                    RequiredBodyAssertions:
                    [
                        new("Tagging.TagSet.Tag", "Returns both tags written by PutBucketTagging."),
                    ]),
                new(204, Notes: "DeleteBucketTagging clears the proxy-owned bucket-tagging metadata key."),
            ],
            semanticAssertion:
            "GetBucketTagging must echo the exact tags written by PutBucketTagging, and DeleteBucketTagging must clear them so a follow-up GetBucketTagging reports NoSuchTagSet again. DeleteBucket is not asserted here to match the other bucket-scoped happy-path cases in this matrix; bucket cleanup is left to the nightly reaper."),
            static (context, _) =>
            {
                var bucket = context.GetProperty("bucketName") ?? ("conf-bucket-tagging-" + Guid.NewGuid().ToString("N")[..12]);
                var taggingBody = Encoding.UTF8.GetBytes(
                    "<Tagging><TagSet><Tag><Key>project</Key><Value>aws2azure</Value></Tag><Tag><Key>tier</Key><Value>conformance</Value></Tag></TagSet></Tagging>");
                return new ValueTask<ConformanceExecutionPlan>(new ConformanceExecutionPlan(
                [
                    new ConformanceRequestStep("create-bucket", _ => BuildBucketRequest(context, HttpMethod.Put, bucket)),
                    new ConformanceRequestStep("put-bucket-tagging", _ => BuildBucketSubresourceRequest(context, HttpMethod.Put, bucket, "tagging", taggingBody)),
                    new ConformanceRequestStep("get-bucket-tagging", _ => BuildBucketSubresourceRequest(context, HttpMethod.Get, bucket, "tagging", Array.Empty<byte>())),
                    new ConformanceRequestStep("delete-bucket-tagging", _ => BuildBucketSubresourceRequest(context, HttpMethod.Delete, bucket, "tagging", Array.Empty<byte>())),
                ], Tier1SkipReason));
            });
    private static PlannedConformanceCase CreateObjectLegalHoldRoundTripCase()
        => new(
            "object-legal-hold-roundtrip",
            "s3:PutObjectLegalHold/GetObjectLegalHold",
            ConformanceCaseExpectation.Success(
            [
                new(200, Notes: "CreateBucket."),
                new(200, RequiredHeaders: [new("ETag", "Present on the seed PutObject response.")]),
                new(200, Notes: "PutObjectLegalHold ON maps to Azure Set Blob Legal Hold (x-ms-legal-hold: true)."),
                new(
                    200,
                    RequiredBodyAssertions: [new("LegalHold.Status", "Reports ON after PutObjectLegalHold(ON).")]),
                new(200, Notes: "PutObjectLegalHold OFF releases the Azure blob legal hold so teardown can delete the object."),
                new(
                    200,
                    RequiredBodyAssertions: [new("LegalHold.Status", "Reports OFF after PutObjectLegalHold(OFF).")]),
                new(204, Notes: "Deletes the object now that the legal hold has been released."),
            ],
            semanticAssertion:
            "GetObjectLegalHold must reflect ON immediately after PutObjectLegalHold(ON) and OFF immediately after PutObjectLegalHold(OFF). Per docs/gaps/s3/PutObjectLegalHold.yaml and GetObjectLegalHold.yaml, this is verified_real_azure only (Azurite does not support blob legal hold), so this case carries the same Tier1SkipReason as every other happy-path case in this matrix and is intended for the real-AWS/real-Azure Tier-3 differential once wired. DeleteBucket is not asserted: bucket cleanup is left to the nightly reaper."),
            static (context, _) =>
            {
                var bucket = context.GetProperty("bucketName") ?? ("conf-legal-hold-bucket-" + Guid.NewGuid().ToString("N")[..12]);
                var key = "legal-hold/object.txt";
                var body = Encoding.UTF8.GetBytes("aws2azure legal hold roundtrip payload");
                var legalHoldOn = Encoding.UTF8.GetBytes("<LegalHold><Status>ON</Status></LegalHold>");
                var legalHoldOff = Encoding.UTF8.GetBytes("<LegalHold><Status>OFF</Status></LegalHold>");
                return new ValueTask<ConformanceExecutionPlan>(new ConformanceExecutionPlan(
                [
                    new ConformanceRequestStep("create-bucket", _ => BuildBucketRequest(context, HttpMethod.Put, bucket, objectLockEnabled: true)),
                    new ConformanceRequestStep("seed-object", _ => BuildObjectRequest(context, HttpMethod.Put, bucket, key, body)),
                    new ConformanceRequestStep("put-object-legal-hold-on", _ => BuildObjectSubresourceRequest(context, HttpMethod.Put, bucket, key, "legal-hold", legalHoldOn)),
                    new ConformanceRequestStep("get-object-legal-hold-on", _ => BuildObjectSubresourceRequest(context, HttpMethod.Get, bucket, key, "legal-hold", Array.Empty<byte>())),
                    new ConformanceRequestStep("put-object-legal-hold-off", _ => BuildObjectSubresourceRequest(context, HttpMethod.Put, bucket, key, "legal-hold", legalHoldOff)),
                    new ConformanceRequestStep("get-object-legal-hold-off", _ => BuildObjectSubresourceRequest(context, HttpMethod.Get, bucket, key, "legal-hold", Array.Empty<byte>())),
                    new ConformanceRequestStep("delete-object", _ => BuildObjectRequest(context, HttpMethod.Delete, bucket, key, Array.Empty<byte>())),
                ], Tier1SkipReason));
            });
    private static PlannedConformanceCase CreateObjectRetentionRoundTripCase()
        => new(
            "object-retention-roundtrip",
            "s3:PutObjectRetention/GetObjectRetention",
            ConformanceCaseExpectation.Success(
            [
                new(200, Notes: "CreateBucket."),
                new(200, RequiredHeaders: [new("ETag", "Present on the seed PutObject response.")]),
                new(200, Notes: "PutObjectRetention(GOVERNANCE) maps to Azure Set Blob Immutability Policy (Unlocked)."),
                new(
                    200,
                    RequiredBodyAssertions:
                    [
                        new("Retention.Mode", "Reports GOVERNANCE after PutObjectRetention(GOVERNANCE)."),
                        new("Retention.RetainUntilDate", "Echoes the retain-until timestamp set by PutObjectRetention."),
                    ]),
            ],
            semanticAssertion:
            "GetObjectRetention must echo the mode and retain-until timestamp written by PutObjectRetention. Per docs/gaps/s3/PutObjectRetention.yaml and GetObjectRetention.yaml, this is verified_real_azure only (Azurite does not support blob immutability policies) and GOVERNANCE-mode Azure unlocked policies are extend-only for the lifetime of the retention window, so this case intentionally does not attempt to delete the object or bucket — that is left entirely to the nightly reaper once the retention window elapses. This case carries the same Tier1SkipReason as every other happy-path case in this matrix and is intended for the real-AWS/real-Azure Tier-3 differential once wired."),
            static (context, _) =>
            {
                var bucket = context.GetProperty("bucketName") ?? ("conf-retention-bucket-" + Guid.NewGuid().ToString("N")[..12]);
                var key = "retention/object.txt";
                var body = Encoding.UTF8.GetBytes("aws2azure retention roundtrip payload");
                var retainUntil = DateTimeOffset.UtcNow.AddMinutes(1).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture);
                var retentionBody = Encoding.UTF8.GetBytes(
                    $"<Retention><Mode>GOVERNANCE</Mode><RetainUntilDate>{retainUntil}</RetainUntilDate></Retention>");
                return new ValueTask<ConformanceExecutionPlan>(new ConformanceExecutionPlan(
                [
                    new ConformanceRequestStep("create-bucket", _ => BuildBucketRequest(context, HttpMethod.Put, bucket, objectLockEnabled: true)),
                    new ConformanceRequestStep("seed-object", _ => BuildObjectRequest(context, HttpMethod.Put, bucket, key, body)),
                    new ConformanceRequestStep("put-object-retention", _ => BuildObjectSubresourceRequest(context, HttpMethod.Put, bucket, key, "retention", retentionBody)),
                    new ConformanceRequestStep("get-object-retention", _ => BuildObjectSubresourceRequest(context, HttpMethod.Get, bucket, key, "retention", Array.Empty<byte>())),
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
            // Content-MD5 is a content header, not a request header: setting it
            // via request.Headers.TryAddWithoutValidation silently fails (returns
            // false) and the header is never sent. Real AWS rejects object
            // subresource PUTs with a body (e.g. PutObjectRetention) that omit
            // it: "Missing required header for this request: Content-MD5 OR
            // x-amz-checksum-*".
            request.Content.Headers.TryAddWithoutValidation("Content-MD5", Convert.ToBase64String(System.Security.Cryptography.MD5.HashData(body)));
        }

        ConformanceSigV4Signer.SignHeader(
            request,
            body,
            context.AccessKeyId,
            context.SecretAccessKey,
            region: context.Region,
            extraSignedHeaders: body.Length > 0 ? ["content-md5"] : null,
            sessionToken: context.SessionToken);
        return request;
    }
    private static HttpRequestMessage BuildBucketSubresourceRequest(
        ConformanceCaseContext context,
        HttpMethod method,
        string bucket,
        string subresource,
        byte[] body)
    {
        var request = new HttpRequestMessage(
            method,
            new Uri(ResolveBaseAddress(context), $"/{bucket}?{subresource}"));
        if (body.Length > 0)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentLength = body.Length;
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
            // Content-MD5 is a content header, not a request header: setting it
            // via request.Headers.TryAddWithoutValidation silently fails (returns
            // false) and the header is never sent. Real AWS rejects bucket
            // subresource PUTs with a body (e.g. PutBucketTagging) that omit it:
            // "Missing required header for this request: Content-MD5 OR
            // x-amz-checksum-*".
            request.Content.Headers.TryAddWithoutValidation("Content-MD5", Convert.ToBase64String(System.Security.Cryptography.MD5.HashData(body)));
        }

        ConformanceSigV4Signer.SignHeader(
            request,
            body,
            context.AccessKeyId,
            context.SecretAccessKey,
            region: context.Region,
            extraSignedHeaders: body.Length > 0 ? ["content-md5"] : null,
            sessionToken: context.SessionToken);
        return request;
    }

    /// <summary>
    /// Builds a presigned-query-authenticated request with no header-based
    /// SigV4 Authorization — the entire auth envelope lives in the query
    /// string, matching what a bare HTTP client would send after receiving a
    /// presigned URL from boto3/AWSSDK.S3.
    /// </summary>
    private static HttpRequestMessage BuildPresignedObjectRequest(
        ConformanceCaseContext context,
        HttpMethod method,
        string bucket,
        string key,
        byte[] body)
    {
        var baseAddress = ResolveBaseAddress(context);
        var uri = ConformancePresignedUrlBuilder.BuildPresignedUri(
            method,
            baseAddress,
            $"/{bucket}/{key}",
            TimeSpan.FromMinutes(15),
            context.AccessKeyId,
            context.SecretAccessKey,
            region: context.Region,
            sessionToken: context.SessionToken);

        var request = new HttpRequestMessage(method, uri);
        if (body.Length > 0)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentLength = body.Length;
        }

        return request;
    }
}
