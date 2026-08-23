using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Core.Modules;
using Aws2Azure.Modules.S3.Errors;
using Aws2Azure.Modules.S3.Internal;
using Aws2Azure.Modules.S3.Streaming;
using Aws2Azure.Modules.S3.Xml;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.S3.Operations;

/// <summary>
/// Handlers for the S3 multipart-upload happy path: <c>CreateMultipartUpload</c>,
/// <c>UploadPart</c>, <c>CompleteMultipartUpload</c>, <c>AbortMultipartUpload</c>.
/// Mapping to Azure Blob Storage:
/// <list type="bullet">
///   <item><c>CreateMultipartUpload</c> issues a stateless 32-byte
///   <see cref="UploadIdCodec"/> token; no Azure call is made yet.</item>
///   <item><c>UploadPart</c> → Azure <c>Put Block</c>; we compute the
///   part's MD5 on the fly to derive an S3-compatible ETag.</item>
///   <item><c>CompleteMultipartUpload</c> → Azure <c>Put Block List</c>
///   with the supplied part list (validated ascending + unique).</item>
///   <item><c>AbortMultipartUpload</c> is a no-op against Azure (uncommitted
///   blocks GC after 7 days, matching the UploadId TTL). The bucket is
///   probed so missing-container surfaces as <c>NoSuchBucket</c>.</item>
/// </list>
/// </summary>
internal static class MultipartHandlers
{
    /// <summary>Hard cap on <c>CompleteMultipartUpload</c> body size (4 MiB).
    /// 10,000 parts × ~256 bytes per &lt;Part&gt; entry leaves plenty of
    /// headroom while keeping memory bounded.</summary>
    private const int MaxCompleteBodyBytes = 4 * 1024 * 1024;
    private const long MinNonFinalPartBytes = 5L * 1024 * 1024;
    private static readonly TimeSpan LeaseOperationTimeout = TimeSpan.FromSeconds(45);

    public static Task HandleAsync(HttpContext context, S3RouteResult route, BlobClient blob, CancellationToken ct, ICredentialResolver? credentials = null) =>
        route.Operation switch
        {
            S3Operation.CreateMultipartUpload => CreateAsync(context, blob, route.Bucket!, route.Key!, ct),
            S3Operation.UploadPart            => UploadPartAsync(context, blob, route.Bucket!, route.Key!, credentials, ct),
            S3Operation.UploadPartCopy        => UploadPartCopyAsync(context, blob, route.Bucket!, route.Key!, ct),
            S3Operation.CompleteMultipartUpload => CompleteAsync(context, blob, route.Bucket!, route.Key!, route.VirtualHosted, ct),
            S3Operation.AbortMultipartUpload  => AbortAsync(context, blob, route.Bucket!, route.Key!, ct),
            S3Operation.ListParts             => ListPartsAsync(context, blob, route.Bucket!, route.Key!, ct),
            S3Operation.ListMultipartUploads  => ListMultipartUploadsAsync(context, blob, route.Bucket!, ct),
            _ => S3ErrorMapping.WriteAsync(context, S3ErrorMapping.NotImplemented(route.Operation)),
        };

    // ---------- CreateMultipartUpload ----------

    private static async Task CreateAsync(HttpContext ctx, BlobClient blob, string bucket, string key, CancellationToken ct)
    {
        if (S3ErrorMapping.ClassifyLookupBucketName(bucket) is { } bucketError)
        {
            await S3ErrorMapping.WriteAsync(ctx, bucketError).ConfigureAwait(false);
            return;
        }
        if (!S3ObjectKey.IsValid(key))
        {
            await S3ErrorMapping.WriteAsync(ctx, S3ErrorMapping.InvalidObjectKey()).ConfigureAwait(false);
            return;
        }

        var taggingHeader = HeaderForwarding.ReadFirstHeader(ctx.Request, "x-amz-tagging");
        var (tags, taggingError) = TaggingHeaderParser.Parse(taggingHeader);
        if (taggingError is { } parsedTaggingError)
        {
            await S3ErrorMapping.WriteAsync(ctx, parsedTaggingError).ConfigureAwait(false);
            return;
        }

        var token = UploadIdCodec.Issue(blob.AccountName, bucket, key, blob.AccountKeyBytes);
        var stateStore = new MultipartUploadStateStore(blob);
        var currentGeneration = await stateStore.ReadOrCreateContainerGenerationAsync(bucket, ct).ConfigureAwait(false);
        if (currentGeneration.Kind == MultipartUploadStateStore.ResultKind.BucketMissing)
        {
            await S3ErrorMapping.WriteAsync(ctx, S3ErrorMapping.NoSuchBucket()).ConfigureAwait(false);
            return;
        }
        if (currentGeneration.Kind != MultipartUploadStateStore.ResultKind.Success || string.IsNullOrEmpty(currentGeneration.Generation))
        {
            await S3ErrorMapping.WriteAsync(ctx, currentGeneration.Error ?? new S3ErrorMapping.Mapping(
                StatusCodes.Status500InternalServerError,
                "InternalError",
                "We encountered an internal error. Please try again.")).ConfigureAwait(false);
            return;
        }
        try
        {
            if (await stateStore.CreateAsync(bucket, key, token.Encoded, token.CreatedAt, currentGeneration.Generation!, ctx.Request, tags!, ct).ConfigureAwait(false) is { } stateError)
            {
                await S3ErrorMapping.WriteAsync(ctx, stateError).ConfigureAwait(false);
                return;
            }
        }
        catch (InvalidOperationException ex)
        {
            await S3ErrorMapping.WriteAsync(ctx, S3ErrorMapping.InvalidArgument(ex.Message)).ConfigureAwait(false);
            return;
        }

        var stateGeneration = await stateStore.VerifyContainerGenerationAsync(bucket, currentGeneration.Generation!, ct).ConfigureAwait(false);
        if (stateGeneration.Kind != MultipartUploadStateStore.ResultKind.Success)
        {
            _ = await stateStore.DeleteAsync(bucket, token.Encoded, ct).ConfigureAwait(false);
            await S3ErrorMapping.WriteAsync(
                ctx,
                stateGeneration.Kind == MultipartUploadStateStore.ResultKind.Error
                    ? stateGeneration.Error ?? OperationAborted()
                    : OperationAborted()).ConfigureAwait(false);
            return;
        }

        var xml = S3XmlWriter.InitiateMultipartUploadResult(bucket, key, token.Encoded);
        await WriteXmlAsync(ctx, StatusCodes.Status200OK, xml, ct, includeContentType: false).ConfigureAwait(false);
    }

    // ---------- UploadPart ----------

    private static async Task UploadPartAsync(HttpContext ctx, BlobClient blob, string bucket, string key, ICredentialResolver? credentials, CancellationToken ct)
    {
        if (S3ErrorMapping.ClassifyLookupBucketName(bucket) is { } bucketError)
        {
            await S3ErrorMapping.WriteAsync(ctx, bucketError).ConfigureAwait(false);
            return;
        }
        var uploadId = ctx.Request.Query["uploadId"].ToString();
        var partRaw  = ctx.Request.Query["partNumber"].ToString();
        if (!int.TryParse(partRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var partNumber) ||
            partNumber is < 1 or > 10000)
        {
            await S3ErrorMapping.WriteAsync(ctx, S3ErrorMapping.InvalidArgument(
                "partNumber must be an integer in [1, 10000].")).ConfigureAwait(false);
            return;
        }

        var token = UploadIdCodec.TryDecode(uploadId, blob.AccountName, bucket, key, blob.AccountKeyBytes);
        if (token is null)
        {
            await S3ErrorMapping.WriteAsync(ctx, NoSuchUpload()).ConfigureAwait(false);
            return;
        }
        var stateStore = new MultipartUploadStateStore(blob);
        var stateRead = await stateStore.ReadSummaryAsync(bucket, uploadId, ct).ConfigureAwait(false);
        var legacyMultipart = false;
        if (stateRead.Kind == MultipartUploadStateStore.ResultKind.NotFound)
        {
            if (!await TryHandleLegacyMultipartFallbackAsync(ctx, stateStore, bucket, token.Value, ct).ConfigureAwait(false))
            {
                return;
            }

            legacyMultipart = true;
        }
        else if (!await TryHandleStateSummaryResultAsync(ctx, stateRead).ConfigureAwait(false))
        {
            return;
        }

        var blockId = UploadIdCodec.BlockId(token.Value.NonceHex, partNumber);
        var query   = "?comp=block&blockid=" + Uri.EscapeDataString(blockId);

        var body = AwsChunkedRequest.PrepareBodyStream(ctx.Request, credentials);

        try
        {
            // Wrap the request body so we incrementally MD5-hash the part while
            // it streams to Azure. The hex MD5 becomes the S3 part ETag the
            // client echoes back in CompleteMultipartUpload.
            using var hashing = new HashingStream(body.Body, HashAlgorithmName.MD5);

            using var azureReq = new HttpRequestMessage(HttpMethod.Put, blob.BuildBlobUri(bucket, key, query))
            {
                Content = new StreamContent(hashing),
            };
            azureReq.Options.Set(Aws2Azure.Core.Azure.AzureHttpClient.NoRetryOption, true);
            if (body.ContentLength is { } len)
            {
                azureReq.Content.Headers.ContentLength = len;
            }

            using var azureResp = await blob.SendBlobRequestAsync(azureReq, ct).ConfigureAwait(false);
            if (!azureResp.IsSuccessStatusCode)
            {
                await S3ErrorMapping.WriteAsync(ctx, S3ErrorMapping.FromAzure(azureResp, S3Operation.UploadPart)).ConfigureAwait(false);
                return;
            }

            if (!legacyMultipart &&
                !await VerifyMultipartBucketGenerationAfterWriteAsync(ctx, stateStore, bucket, uploadId, stateRead.Summary!.Value.ContainerGeneration, ct).ConfigureAwait(false))
            {
                return;
            }

            var etag = "\"" + Convert.ToHexString(hashing.GetFinalHash()).ToLowerInvariant() + "\"";
            ctx.Response.StatusCode = StatusCodes.Status200OK;
            HeaderForwarding.ApplyCommonS3ResponseHeaders(ctx.Response);
            ctx.Response.Headers["ETag"] = etag;
            ctx.Response.ContentLength = 0;
        }
        finally
        {
            body.Decoder?.Dispose();
        }
    }

    // ---------- UploadPartCopy ----------

    /// <summary>
    /// PUT /{b}/{k}?uploadId=X&amp;partNumber=N with <c>x-amz-copy-source</c>
    /// header → Azure <c>Put Block From URL</c>. The source must live in the
    /// same Azure storage account; we authenticate Azure's source fetch via
    /// <c>x-ms-copy-source-authorization</c> SharedKey so the source blob
    /// does not need to be public or pre-signed. <c>x-amz-copy-source-range</c>
    /// (S3) is forwarded as <c>x-ms-source-range</c> (Azure) and folded into
    /// the source SharedKey signature.
    /// </summary>
    private static async Task UploadPartCopyAsync(HttpContext ctx, BlobClient blob, string destBucket, string destKey, CancellationToken ct)
    {
        if (S3ErrorMapping.ClassifyLookupBucketName(destBucket) is { } destBucketError)
        {
            await S3ErrorMapping.WriteAsync(ctx, destBucketError).ConfigureAwait(false);
            return;
        }
        var uploadId = ctx.Request.Query["uploadId"].ToString();
        var partRaw  = ctx.Request.Query["partNumber"].ToString();
        if (!int.TryParse(partRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var partNumber) ||
            partNumber is < 1 or > 10000)
        {
            await S3ErrorMapping.WriteAsync(ctx, S3ErrorMapping.InvalidArgument(
                "partNumber must be an integer in [1, 10000].")).ConfigureAwait(false);
            return;
        }

        var token = UploadIdCodec.TryDecode(uploadId, blob.AccountName, destBucket, destKey, blob.AccountKeyBytes);
        if (token is null)
        {
            await S3ErrorMapping.WriteAsync(ctx, NoSuchUpload()).ConfigureAwait(false);
            return;
        }
        var parsed = CopySourceParser.ParseAndValidate(ctx.Request);
        if (!parsed.Success)
        {
            await S3ErrorMapping.WriteAsync(ctx, parsed.Error).ConfigureAwait(false);
            return;
        }
        var sourceBucket = parsed.Bucket!;
        var sourceKey = parsed.Key!;
        var sourceVersionId = parsed.VersionId;
        if (blob.IsInternalContainer(sourceBucket))
        {
            await S3ErrorMapping.WriteAsync(ctx, S3ErrorMapping.NoSuchBucket()).ConfigureAwait(false);
            return;
        }

        // S3 forbids targeting an in-flight upload's part at the same key as
        // the source — Azure would happily Put Block From URL onto the
        // source's own uncommitted-block list, which the eventual Complete
        // would silently reconcile against the live blob. Rejecting up-front
        // matches S3's "InvalidRequest" behaviour for the same scenario.
        if (string.IsNullOrEmpty(sourceVersionId)
            && string.Equals(sourceBucket, destBucket, StringComparison.Ordinal)
            && string.Equals(sourceKey, destKey, StringComparison.Ordinal))
        {
            await S3ErrorMapping.WriteAsync(ctx, new S3ErrorMapping.Mapping(400, "InvalidRequest",
                "The copy source and destination of an UploadPartCopy must differ.")).ConfigureAwait(false);
            return;
        }

        var rangeHeader = NormalizeCopySourceRange(ctx.Request);
        if (rangeHeader.Error is { } rangeErr)
        {
            await S3ErrorMapping.WriteAsync(ctx, rangeErr).ConfigureAwait(false);
            return;
        }

        var stateStore = new MultipartUploadStateStore(blob);
        var stateRead = await stateStore.ReadSummaryAsync(destBucket, uploadId, ct).ConfigureAwait(false);
        var legacyMultipart = false;
        if (stateRead.Kind == MultipartUploadStateStore.ResultKind.NotFound)
        {
            if (!await TryHandleLegacyMultipartFallbackAsync(ctx, stateStore, destBucket, token.Value, ct).ConfigureAwait(false))
            {
                return;
            }

            legacyMultipart = true;
        }
        else if (!await TryHandleStateSummaryResultAsync(ctx, stateRead).ConfigureAwait(false))
        {
            return;
        }

        var blockId = UploadIdCodec.BlockId(token.Value.NonceHex, partNumber);
        var destUri = blob.BuildBlobUri(destBucket, destKey, "?comp=block&blockid=" + Uri.EscapeDataString(blockId));

        // Concrete-ETag copy-source preconditions: proxy-translated S3
        // ETags do not round-trip back to Azure's raw ETag space, so a
        // valid CAS would incorrectly 412. Reject with 501 — "*" is still
        // forwarded below with identical semantics to S3.
        var sourceConditionals = await TryEvaluateCopySourcePreconditionsAsync(
            ctx, blob, sourceBucket, sourceKey, sourceVersionId, S3Operation.UploadPartCopy, ct).ConfigureAwait(false);
        if (sourceConditionals.Error is { } preconditionError)
        {
            await S3ErrorMapping.WriteAsync(ctx, preconditionError).ConfigureAwait(false);
            return;
        }

        var sourceUri = blob.BuildSourceReadSasUri(
            sourceBucket,
            sourceKey,
            TimeSpan.FromHours(1),
            sourceConditionals.PinnedVersionId ?? sourceVersionId);
        using var azureReq = new HttpRequestMessage(HttpMethod.Put, destUri)
        {
            Content = new ByteArrayContent(Array.Empty<byte>()),
        };
        azureReq.Content.Headers.ContentLength = 0;
        azureReq.Headers.TryAddWithoutValidation("x-ms-copy-source", sourceUri.AbsoluteUri);
        if (rangeHeader.Value is { } range)
        {
            azureReq.Headers.TryAddWithoutValidation("x-ms-source-range", range);
        }

        HeaderForwarding.ForwardCopySourceConditionals(ctx.Request, azureReq);
        if (sourceConditionals.SourceIfMatch is { } sourceIfMatch)
        {
            azureReq.Headers.TryAddWithoutValidation("x-ms-source-if-match", sourceIfMatch);
        }

        using var azureResp = await blob.SendBlobRequestAsync(azureReq, ct).ConfigureAwait(false);
        if (!azureResp.IsSuccessStatusCode)
        {
            // Distinguish the source-side 404 (NoSuchKey) from the
            // destination-side 404 (NoSuchBucket); both surface as 404 from
            // Azure with x-ms-error-code on the response.
            await S3ErrorMapping.WriteAsync(ctx, S3ErrorMapping.FromAzure(azureResp, S3Operation.UploadPartCopy)).ConfigureAwait(false);
            return;
        }

        if (!legacyMultipart &&
            !await VerifyMultipartBucketGenerationAfterWriteAsync(ctx, stateStore, destBucket, uploadId, stateRead.Summary!.Value.ContainerGeneration, ct).ConfigureAwait(false))
        {
            return;
        }

        var lastModified = azureResp.Content.Headers.LastModified ?? DateTimeOffset.UtcNow;

        // S3 clients echo this ETag back in CompleteMultipartUpload. Azure's
        // Put Block From URL only returns Content-MD5 when the request
        // supplied x-ms-source-content-md5; in the more common case the
        // response carries x-ms-content-crc64 instead, with no ETag suitable
        // for S3. To keep a stable, S3-shaped ETag — and to match what
        // ListParts synthesises for the same (uploadId, partNumber) — we
        // derive the part ETag from the block name. The proxy's Complete
        // path rebuilds block IDs from (nonce, partNumber) and ignores the
        // echoed ETag, so this synthetic value is safe to round-trip.
        string etag;
        if (azureResp.Content.Headers.ContentMD5 is { } md5Bytes)
        {
            etag = "\"" + Convert.ToHexString(md5Bytes).ToLowerInvariant() + "\"";
        }
        else
        {
            etag = "\"" + SyntheticPartEtag(blockId) + "\"";
        }

        var xml = S3XmlWriter.CopyPartResult(lastModified, etag);
        await WriteXmlAsync(ctx, StatusCodes.Status200OK, xml, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Parses optional <c>x-amz-copy-source-range</c> header (format
    /// <c>bytes=start-end</c>; both endpoints inclusive). Returns the
    /// canonical <c>bytes=…</c> value to forward as <c>x-ms-source-range</c>,
    /// an error mapping if the header is malformed, or both null when the
    /// header is absent (= whole-blob copy).
    /// </summary>
    private static (string? Value, S3ErrorMapping.Mapping? Error) NormalizeCopySourceRange(HttpRequest request)
    {
        if (!request.Headers.TryGetValue("x-amz-copy-source-range", out var values) || values.Count == 0)
        {
            return (null, null);
        }
        var raw = values[0];
        if (string.IsNullOrEmpty(raw))
        {
            return (null, null);
        }
        const string Prefix = "bytes=";
        if (!raw.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return (null, S3ErrorMapping.InvalidArgument(
                "x-amz-copy-source-range must be of the form 'bytes=start-end'."));
        }
        var spec = raw.AsSpan(Prefix.Length);
        var dash = spec.IndexOf('-');
        if (dash <= 0 || dash == spec.Length - 1)
        {
            return (null, S3ErrorMapping.InvalidArgument(
                "x-amz-copy-source-range must be of the form 'bytes=start-end'."));
        }
        if (!long.TryParse(spec[..dash], NumberStyles.Integer, CultureInfo.InvariantCulture, out var start) ||
            !long.TryParse(spec[(dash + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var end) ||
            start < 0 || end < start)
        {
            return (null, S3ErrorMapping.InvalidArgument(
                "x-amz-copy-source-range must be of the form 'bytes=start-end' with end >= start >= 0."));
        }
        // Azure's x-ms-source-range uses the same RFC-7233 'bytes=' syntax,
        // so re-emit the canonical form (drops any whitespace S3 tolerates).
        return ("bytes=" + start.ToString(CultureInfo.InvariantCulture)
            + "-" + end.ToString(CultureInfo.InvariantCulture), null);
    }

    // ---------- CompleteMultipartUpload ----------

    private static async Task CompleteAsync(HttpContext ctx, BlobClient blob, string bucket, string key, bool virtualHosted, CancellationToken ct)
    {
        if (S3ErrorMapping.ClassifyLookupBucketName(bucket) is { } bucketError)
        {
            await S3ErrorMapping.WriteAsync(ctx, bucketError).ConfigureAwait(false);
            return;
        }
        var uploadId = ctx.Request.Query["uploadId"].ToString();
        var token = UploadIdCodec.TryDecode(uploadId, blob.AccountName, bucket, key, blob.AccountKeyBytes);
        if (token is null)
        {
            await S3ErrorMapping.WriteAsync(ctx, NoSuchUpload()).ConfigureAwait(false);
            return;
        }
        var stateStore = new MultipartUploadStateStore(blob);
        var stateAcquire = await stateStore.AcquireAsync(bucket, uploadId, ct).ConfigureAwait(false);
        var legacyMultipart = false;
        if (stateAcquire.Kind == MultipartUploadStateStore.ResultKind.NotFound)
        {
            if (!await TryHandleLegacyMultipartFallbackAsync(ctx, stateStore, bucket, token.Value, ct).ConfigureAwait(false))
            {
                return;
            }

            legacyMultipart = true;
        }
        else if (!await TryHandleStateAcquireResultAsync(ctx, stateAcquire).ConfigureAwait(false) || stateAcquire.Record is null || string.IsNullOrEmpty(stateAcquire.LeaseId))
        {
            return;
        }

        var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadlineCts.CancelAfter(LeaseOperationTimeout);
        var timedOut = false;
        var deletedState = false;
        using var buffered = new MemoryStream();
        try
        {
            // Buffer the (small) part list body; XmlReader requires sync I/O
            // and Kestrel forbids it on the live request stream.
            await ctx.Request.Body.CopyToAsync(new BoundedWriteStream(buffered, MaxCompleteBodyBytes), deadlineCts.Token).ConfigureAwait(false);
            buffered.Position = 0;

            var parsed = CompleteMultipartUploadParser.Parse(buffered);
            if (!parsed.Success)
            {
                await S3ErrorMapping.WriteAsync(ctx, new S3ErrorMapping.Mapping(
                    StatusCodes.Status400BadRequest, "MalformedXML",
                    parsed.Error ?? "The XML you provided was not well-formed.")).ConfigureAwait(false);
                return;
            }

            if (parsed.Parts.Count > 1)
            {
                // Request "all" (not just "uncommitted") block state: on a
                // retried CompleteMultipartUpload — e.g. after a prior
                // attempt's Put Block List succeeded but a later step (tag
                // application) failed and left the state record leased for
                // retry — the referenced blocks are no longer "uncommitted"
                // at all; they already moved into the blob's *committed*
                // block list. Validating against uncommitted-only would
                // then spuriously reject an already-successfully-committed
                // part list as InvalidPart. A block matching this upload's
                // nonce+partNumber naming is valid evidence the part was
                // uploaded, whether it is still pending or was already
                // committed by an earlier attempt.
                using var blockListResponse = await blob.GetBlockListAsync(bucket, key, "all", deadlineCts.Token).ConfigureAwait(false);
                if (!blockListResponse.IsSuccessStatusCode)
                {
                    await S3ErrorMapping.WriteAsync(ctx, S3ErrorMapping.FromAzure(blockListResponse, S3Operation.CompleteMultipartUpload)).ConfigureAwait(false);
                    return;
                }

                var blockListXml = await blockListResponse.Content.ReadAsByteArrayAsync(deadlineCts.Token).ConfigureAwait(false);
                var blockList = BlockListParser.Parse(new MemoryStream(blockListXml));
                if (TryFindInvalidPartError(
                        token.Value.NonceHex,
                        parsed.Parts,
                        blockList,
                        out var invalidPartError))
                {
                    await S3ErrorMapping.WriteAsync(ctx, invalidPartError).ConfigureAwait(false);
                    return;
                }

                if (TryFindEntityTooSmallError(
                        token.Value.NonceHex,
                        parsed.Parts,
                        blockList,
                        out var tooSmallError))
                {
                    await S3ErrorMapping.WriteAsync(ctx, tooSmallError).ConfigureAwait(false);
                    return;
                }
            }

            var blockIds = new List<string>(parsed.Parts.Count);
            foreach (var p in parsed.Parts)
            {
                blockIds.Add(UploadIdCodec.BlockId(token.Value.NonceHex, p.PartNumber));
            }

            var body = BlockListXml.Build(blockIds);
            using var azureReq = new HttpRequestMessage(HttpMethod.Put, blob.BuildBlobUri(bucket, key, "?comp=blocklist"))
            {
                Content = new ByteArrayContent(body),
            };
            azureReq.Options.Set(Aws2Azure.Core.Azure.AzureHttpClient.NoRetryOption, true);
            azureReq.Content.Headers.ContentLength = body.Length;
            azureReq.Content.Headers.TryAddWithoutValidation("Content-Type", "application/xml");
            if (!legacyMultipart)
            {
                var preCommitGeneration = await stateStore.VerifyContainerGenerationAsync(bucket, stateAcquire.Record!.Value.Summary.ContainerGeneration, deadlineCts.Token).ConfigureAwait(false);
                if (preCommitGeneration.Kind != MultipartUploadStateStore.ResultKind.Success)
                {
                    _ = await stateStore.DeleteLeasedAsync(bucket, uploadId, stateAcquire.LeaseId!, deadlineCts.Token).ConfigureAwait(false);
                    deletedState = true;
                    await S3ErrorMapping.WriteAsync(
                        ctx,
                        preCommitGeneration.Kind == MultipartUploadStateStore.ResultKind.Error
                            ? preCommitGeneration.Error ?? OperationAborted()
                            : OperationAborted()).ConfigureAwait(false);
                    return;
                }

                stateAcquire.Record!.Value.Headers.ApplyHeaders(azureReq);
            }
            var multipartPartCountMetadataHeader = "x-ms-meta-" + HeaderForwarding.InternalMultipartPartCountMetadataName;
            azureReq.Headers.Remove(multipartPartCountMetadataHeader);
            azureReq.Headers.TryAddWithoutValidation(
                multipartPartCountMetadataHeader,
                parsed.Parts.Count.ToString(CultureInfo.InvariantCulture));

            using var azureResp = await blob.SendBlobRequestAsync(azureReq, deadlineCts.Token).ConfigureAwait(false);
            if (!azureResp.IsSuccessStatusCode)
            {
                var mapping = S3ErrorMapping.FromAzure(azureResp, S3Operation.CompleteMultipartUpload);
                if (azureResp.StatusCode == HttpStatusCode.BadRequest &&
                    string.Equals(ReadHeader(azureResp, "x-ms-error-code"), "InvalidBlockList", StringComparison.Ordinal))
                {
                    mapping = new S3ErrorMapping.Mapping(400, "InvalidPart",
                        "One or more of the specified parts could not be found.");
                }
                await S3ErrorMapping.WriteAsync(ctx, mapping).ConfigureAwait(false);
                return;
            }

            if (!legacyMultipart)
            {
                var completeGeneration = await stateStore.VerifyContainerGenerationAsync(bucket, stateAcquire.Record!.Value.Summary.ContainerGeneration, deadlineCts.Token).ConfigureAwait(false);
                if (completeGeneration.Kind != MultipartUploadStateStore.ResultKind.Success)
                {
                    _ = await stateStore.DeleteLeasedAsync(bucket, uploadId, stateAcquire.LeaseId!, deadlineCts.Token).ConfigureAwait(false);
                    deletedState = true;
                    await S3ErrorMapping.WriteAsync(
                        ctx,
                        completeGeneration.Kind == MultipartUploadStateStore.ResultKind.Error
                            ? completeGeneration.Error ?? OperationAborted()
                            : OperationAborted()).ConfigureAwait(false);
                    return;
                }

                // Apply x-amz-tagging captured at initiate (issue #799) now
                // that the blob is fully committed. This is a second, non-
                // atomic Azure call (Set Blob Tags), unlike Content-Type/
                // x-amz-meta-* which ride along on the blocklist PUT itself.
                // A failure here leaves the state record leased (not yet
                // retired) so the lease simply expires and a client retry of
                // CompleteMultipartUpload can re-attempt tag application.
                var capturedTags = stateAcquire.Record.Value.Headers.Tags;
                if (capturedTags.Count > 0)
                {
                    var tagsBody = S3XmlWriter.AzureBlobTagsBody(capturedTags);
                    using var tagsResponse = await blob.PutBlobTagsAsync(bucket, key, tagsBody, versionId: null, deadlineCts.Token).ConfigureAwait(false);
                    if (!tagsResponse.IsSuccessStatusCode)
                    {
                        await S3ErrorMapping.WriteAsync(ctx, S3ErrorMapping.FromAzure(tagsResponse, S3Operation.CompleteMultipartUpload)).ConfigureAwait(false);
                        return;
                    }
                }

                var retiredState = await TryRetireCompletedStateAsync(
                    stateStore,
                    bucket,
                    uploadId,
                    stateAcquire.Record.Value.Summary,
                    stateAcquire.LeaseId!,
                    deadlineCts.Token).ConfigureAwait(false);
                if (!retiredState.Retired)
                {
                    await S3ErrorMapping.WriteAsync(ctx, retiredState.Error ?? new S3ErrorMapping.Mapping(
                        StatusCodes.Status500InternalServerError,
                        "InternalError",
                        "We encountered an internal error. Please try again.")).ConfigureAwait(false);
                    return;
                }

                if (!deletedState)
                {
                    deletedState = true;
                }
            }

            var azureEtag = ReadHeader(azureResp, "ETag") ?? string.Empty;
            var synth = "\"" + HeaderForwarding.TranslateAzureMultipartEtagToS3(azureEtag, parsed.Parts.Count) + "\"";
            // Real S3 emits <Location> shaped as the S3 URL the client hit
            // (e.g. https://{bucket}.s3.{region}.amazonaws.com/{key} for
            // virtual-hosted, https://s3.{region}.amazonaws.com/{bucket}/{key}
            // for path-style). The proxy's raw Azure blob URL leaked the
            // underlying storage account and confused clients that treated
            // <Location> as an S3 endpoint. Echo the inbound request's
            // scheme + host + the router-resolved addressing style (not a
            // re-derived guess) so the value matches whichever style the
            // caller used.
            var location = BuildS3ResponseLocation(ctx.Request, bucket, key, virtualHosted);
            var xml = S3XmlWriter.CompleteMultipartUploadResult(location, bucket, key, synth);
            await WriteXmlAsync(ctx, StatusCodes.Status200OK, xml, ct).ConfigureAwait(false);
            return;
        }
        catch (InvalidDataException)
        {
            await S3ErrorMapping.WriteAsync(ctx, new S3ErrorMapping.Mapping(
                StatusCodes.Status400BadRequest, "EntityTooLarge",
                "CompleteMultipartUpload body exceeded the allowed size.")).ConfigureAwait(false);
            return;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            timedOut = true;
            await S3ErrorMapping.WriteAsync(ctx, RequestTimedOut()).ConfigureAwait(false);
            return;
        }
        finally
        {
            deadlineCts.Dispose();
            if (!legacyMultipart && !timedOut && !deletedState)
            {
                await ReleaseMultipartLeaseBestEffortAsync(stateStore, bucket, uploadId, stateAcquire.LeaseId!).ConfigureAwait(false);
            }
        }
    }

    // ---------- AbortMultipartUpload ----------

    private static async Task AbortAsync(HttpContext ctx, BlobClient blob, string bucket, string key, CancellationToken ct)
    {
        if (S3ErrorMapping.ClassifyLookupBucketName(bucket) is { } bucketError)
        {
            await S3ErrorMapping.WriteAsync(ctx, bucketError).ConfigureAwait(false);
            return;
        }
        var uploadId = ctx.Request.Query["uploadId"].ToString();
        var token = UploadIdCodec.TryDecode(uploadId, blob.AccountName, bucket, key, blob.AccountKeyBytes);
        if (token is null)
        {
            await S3ErrorMapping.WriteAsync(ctx, NoSuchUpload()).ConfigureAwait(false);
            return;
        }
        var stateStore = new MultipartUploadStateStore(blob);
        var acquire = await stateStore.AcquireAsync(bucket, uploadId, ct).ConfigureAwait(false);
        var legacyMultipart = false;
        if (acquire.Kind == MultipartUploadStateStore.ResultKind.NotFound)
        {
            if (!await TryHandleLegacyMultipartFallbackAsync(ctx, stateStore, bucket, token.Value, ct).ConfigureAwait(false))
            {
                return;
            }

            legacyMultipart = true;
        }
        else if (!await TryHandleStateAcquireResultAsync(ctx, acquire).ConfigureAwait(false) || string.IsNullOrEmpty(acquire.LeaseId))
        {
            return;
        }

        if (legacyMultipart)
        {
            ctx.Response.StatusCode = StatusCodes.Status204NoContent;
            HeaderForwarding.ApplyCommonS3ResponseHeaders(ctx.Response);
            ctx.Response.ContentLength = 0;
            return;
        }

        var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadlineCts.CancelAfter(LeaseOperationTimeout);
        var timedOut = false;
        var deletedState = false;
        try
        {
            if (await stateStore.DeleteLeasedAsync(bucket, uploadId, acquire.LeaseId!, deadlineCts.Token).ConfigureAwait(false) is { } deleteError)
            {
                await S3ErrorMapping.WriteAsync(ctx, deleteError).ConfigureAwait(false);
                return;
            }
            deletedState = true;

            ctx.Response.StatusCode = StatusCodes.Status204NoContent;
            HeaderForwarding.ApplyCommonS3ResponseHeaders(ctx.Response);
            ctx.Response.ContentLength = 0;
            return;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            timedOut = true;
            await S3ErrorMapping.WriteAsync(ctx, RequestTimedOut()).ConfigureAwait(false);
            return;
        }
        finally
        {
            deadlineCts.Dispose();
            if (!timedOut && !deletedState)
            {
                await ReleaseMultipartLeaseBestEffortAsync(stateStore, bucket, uploadId, acquire.LeaseId!).ConfigureAwait(false);
            }
        }
    }

    // ---------- ListParts ----------

    /// <summary>S3 default page size for ListParts.</summary>
    private const int DefaultMaxParts = 1000;
    /// <summary>S3 hard cap on <c>max-parts</c>.</summary>
    private const int MaxAllowedMaxParts = 1000;

    private static async Task ListPartsAsync(HttpContext ctx, BlobClient blob, string bucket, string key, CancellationToken ct)
    {
        if (S3ErrorMapping.ClassifyLookupBucketName(bucket) is { } bucketError)
        {
            await S3ErrorMapping.WriteAsync(ctx, bucketError).ConfigureAwait(false);
            return;
        }
        var uploadId = ctx.Request.Query["uploadId"].ToString();
        var token = UploadIdCodec.TryDecode(uploadId, blob.AccountName, bucket, key, blob.AccountKeyBytes);
        if (token is null)
        {
            await S3ErrorMapping.WriteAsync(ctx, NoSuchUpload()).ConfigureAwait(false);
            return;
        }
        var stateStore = new MultipartUploadStateStore(blob);
        var stateRead = await stateStore.ReadSummaryAsync(bucket, uploadId, ct).ConfigureAwait(false);
        var legacyMultipart = false;
        if (stateRead.Kind == MultipartUploadStateStore.ResultKind.NotFound)
        {
            if (!await TryHandleLegacyMultipartFallbackAsync(ctx, stateStore, bucket, token.Value, ct).ConfigureAwait(false))
            {
                return;
            }

            legacyMultipart = true;
        }
        else if (!await TryHandleStateSummaryResultAsync(ctx, stateRead).ConfigureAwait(false))
        {
            return;
        }

        var (maxParts, maxPartsErr) = ParseMaxParts(ctx.Request.Query);
        if (maxPartsErr is not null) { await S3ErrorMapping.WriteAsync(ctx, maxPartsErr.Value).ConfigureAwait(false); return; }
        var (marker, markerErr) = ParsePartNumberMarker(ctx.Request.Query);
        if (markerErr is not null) { await S3ErrorMapping.WriteAsync(ctx, markerErr.Value).ConfigureAwait(false); return; }

        using var azureResp = await blob.GetBlockListAsync(bucket, key, "uncommitted", ct).ConfigureAwait(false);
        if (azureResp.StatusCode == HttpStatusCode.NotFound)
        {
            // Azure returns 404 for both missing container (bucket) and missing
            // blob. Surface NoSuchBucket explicitly; treat a missing blob as
            // "no parts yet" (matches S3's empty ListPartsResult behavior since
            // the uploadId HMAC already authenticated the (bucket,key,nonce)).
            var azureErrorCode = ReadHeader(azureResp, "x-ms-error-code");
            if (string.Equals(azureErrorCode, "ContainerNotFound", StringComparison.Ordinal))
            {
                await S3ErrorMapping.WriteAsync(ctx, S3ErrorMapping.FromAzure(azureResp, S3Operation.ListParts)).ConfigureAwait(false);
                return;
            }
            if (!legacyMultipart &&
                !await TryHandleMultipartStateStillActiveAsync(ctx, stateStore, bucket, uploadId, ct).ConfigureAwait(false))
            {
                return;
            }
            var emptyXml = S3XmlWriter.ListPartsResult(bucket, key, uploadId,
                marker, null, maxParts, false, Array.Empty<S3XmlWriter.ListedPart>());
            await WriteXmlAsync(ctx, StatusCodes.Status200OK, emptyXml, ct).ConfigureAwait(false);
            return;
        }
        if (!azureResp.IsSuccessStatusCode)
        {
            await S3ErrorMapping.WriteAsync(ctx, S3ErrorMapping.FromAzure(azureResp, S3Operation.ListParts)).ConfigureAwait(false);
            return;
        }

        if (!legacyMultipart &&
            !await TryHandleMultipartStateStillActiveAsync(ctx, stateStore, bucket, uploadId, ct).ConfigureAwait(false))
        {
            return;
        }

        var rawXml = await azureResp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        var parsed = BlockListParser.Parse(new MemoryStream(rawXml));

        // Filter to blocks issued by this upload (nonce match) and above the
        // PartNumberMarker. Synthesise ETag/LastModified — we don't retain
        // per-part MD5s and Azure exposes no per-block timestamp.
        var nonceHex = token.Value.NonceHex;
        var initiated = legacyMultipart ? token.Value.CreatedAt : stateRead.Summary!.Value.Initiated;
        var ours = new List<S3XmlWriter.ListedPart>(parsed.Uncommitted.Count);
        foreach (var b in parsed.Uncommitted)
        {
            if (!BlockListParser.TryParseBlockName(b.Name, out var blockNonce, out var pn))
            {
                continue;
            }
            if (!string.Equals(blockNonce, nonceHex, StringComparison.Ordinal))
            {
                continue;
            }
            if (pn <= marker)
            {
                continue;
            }
            ours.Add(new S3XmlWriter.ListedPart(
                pn,
                initiated,
                "\"" + SyntheticPartEtag(b.Name) + "\"",
                b.Size));
        }
        ours.Sort(static (a, b) => a.PartNumber.CompareTo(b.PartNumber));

        var truncated = ours.Count > maxParts;
        if (truncated)
        {
            ours.RemoveRange(maxParts, ours.Count - maxParts);
        }
        int? next = truncated ? ours[^1].PartNumber : null;

        var xml = S3XmlWriter.ListPartsResult(bucket, key, uploadId, marker, next, maxParts, truncated, ours);
        await WriteXmlAsync(ctx, StatusCodes.Status200OK, xml, ct).ConfigureAwait(false);
    }

    // ---------- ListMultipartUploads ----------

    private static async Task ListMultipartUploadsAsync(HttpContext ctx, BlobClient blob, string bucket, CancellationToken ct)
    {
        if (S3ErrorMapping.ClassifyLookupBucketName(bucket) is { } bucketError)
        {
            await S3ErrorMapping.WriteAsync(ctx, bucketError).ConfigureAwait(false);
            return;
        }

        if (await blob.CheckContainerExistsAsync(bucket, S3Operation.ListMultipartUploads, ct).ConfigureAwait(false) is { } bucketErr)
        {
            await S3ErrorMapping.WriteAsync(ctx, bucketErr).ConfigureAwait(false);
            return;
        }

        if (!TryParseMaxUploads(ctx.Request.Query, out var maxUploads, out var maxUploadsError))
        {
            await S3ErrorMapping.WriteAsync(ctx, maxUploadsError!.Value).ConfigureAwait(false);
            return;
        }

        var prefix = StringOrNull(ctx.Request.Query, "prefix");
        var delimiter = StringOrNull(ctx.Request.Query, "delimiter");
        var keyMarker = StringOrNull(ctx.Request.Query, "key-marker");
        var uploadIdMarker = StringOrNull(ctx.Request.Query, "upload-id-marker");

        var stateStore = new MultipartUploadStateStore(blob);
        var list = await stateStore.ListAsync(bucket, keyMarker, uploadIdMarker, prefix, delimiter, maxUploads, ct).ConfigureAwait(false);
        if (list.Kind == MultipartUploadStateStore.ResultKind.BucketMissing)
        {
            await S3ErrorMapping.WriteAsync(ctx, S3ErrorMapping.NoSuchBucket()).ConfigureAwait(false);
            return;
        }
        if (list.Kind == MultipartUploadStateStore.ResultKind.Error)
        {
            await S3ErrorMapping.WriteAsync(ctx, list.Error ?? new S3ErrorMapping.Mapping(500, "InternalError", "We encountered an internal error. Please try again.")).ConfigureAwait(false);
            return;
        }

        var xml = S3XmlWriter.ListMultipartUploadsResult(
            bucket,
            keyMarker,
            uploadIdMarker,
            list.NextKeyMarker,
            list.NextUploadIdMarker,
            prefix,
            delimiter,
            maxUploads,
            list.IsTruncated,
            list.Uploads,
            list.CommonPrefixes);
        await WriteXmlAsync(ctx, StatusCodes.Status200OK, xml, ct).ConfigureAwait(false);
    }

    private static (int value, S3ErrorMapping.Mapping? error) ParseMaxParts(IQueryCollection query)
    {
        var raw = query["max-parts"].ToString();
        if (string.IsNullOrEmpty(raw))
        {
            return (DefaultMaxParts, null);
        }
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed < 0)
        {
            return (0, S3ErrorMapping.InvalidArgument("max-parts must be a non-negative integer."));
        }
        return (Math.Min(parsed == 0 ? DefaultMaxParts : parsed, MaxAllowedMaxParts), null);
    }

    private static (int value, S3ErrorMapping.Mapping? error) ParsePartNumberMarker(IQueryCollection query)
    {
        var raw = query["part-number-marker"].ToString();
        if (string.IsNullOrEmpty(raw))
        {
            return (0, null);
        }
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed < 0)
        {
            return (0, S3ErrorMapping.InvalidArgument("part-number-marker must be a non-negative integer."));
        }
        return (parsed, null);
    }

    private static bool TryParseMaxUploads(IQueryCollection query, out int maxUploads, out S3ErrorMapping.Mapping? error)
    {
        error = null;
        if (!query.TryGetValue("max-uploads", out var values) || values.Count == 0 || string.IsNullOrEmpty(values[0]))
        {
            maxUploads = DefaultMaxParts;
            return true;
        }

        if (!int.TryParse(values[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
        {
            maxUploads = 0;
            error = S3ErrorMapping.InvalidArgument("max-uploads must be an integer between 1 and 1000.");
            return false;
        }

        maxUploads = Math.Min(parsed, 1000);
        return true;
    }

    /// <summary>
    /// Synthetic ETag for ListParts entries. Stable per (uploadId, partNumber)
    /// because the block name encodes both. NOT equal to the MD5 returned
    /// from UploadPart — Azure doesn't expose per-block MD5s on Get Block
    /// List and the proxy retains no upload state. Documented in gap doc.
    /// </summary>
    private static string SyntheticPartEtag(string blockBase64Name)
    {
        var hash = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.ASCII.GetBytes(blockBase64Name));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static bool TryFindEntityTooSmallError(
        string nonceHex,
        IReadOnlyList<CompleteMultipartUploadParser.PartRef> parts,
        BlockListParser.BlockList blockList,
        out S3ErrorMapping.Mapping error)
    {
        error = default;
        var sizes = new Dictionary<int, long>(blockList.Uncommitted.Count + blockList.Committed.Count);
        CollectPartSizes(blockList.Uncommitted, nonceHex, sizes);
        CollectPartSizes(blockList.Committed, nonceHex, sizes);

        for (var i = 0; i < parts.Count - 1; i++)
        {
            var partNumber = parts[i].PartNumber;
            if (sizes.TryGetValue(partNumber, out var size) && size < MinNonFinalPartBytes)
            {
                error = new S3ErrorMapping.Mapping(
                    StatusCodes.Status400BadRequest,
                    "EntityTooSmall",
                    "Your proposed upload is smaller than the minimum allowed object size.");
                return true;
            }
        }

        return false;
    }

    private static void CollectPartSizes(
        IReadOnlyList<BlockListParser.Block> blocks,
        string nonceHex,
        Dictionary<int, long> sizes)
    {
        for (var i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            if (BlockListParser.TryParseBlockName(block.Name, out var blockNonce, out var partNumber)
                && string.Equals(blockNonce, nonceHex, StringComparison.Ordinal))
            {
                sizes[partNumber] = block.Size;
            }
        }
    }

    private static bool TryFindInvalidPartError(
        string nonceHex,
        IReadOnlyList<CompleteMultipartUploadParser.PartRef> parts,
        BlockListParser.BlockList blockList,
        out S3ErrorMapping.Mapping error)
    {
        error = default;
        var uploadedParts = new HashSet<int>();
        // A block matching this upload's nonce+partNumber naming in either
        // the uncommitted or committed list is valid evidence the part was
        // uploaded — see the "all" blocklisttype comment at the call site
        // for why committed blocks must also count here (retried Complete
        // after the blob was already committed by a prior attempt).
        CollectUploadedParts(blockList.Uncommitted, nonceHex, uploadedParts);
        CollectUploadedParts(blockList.Committed, nonceHex, uploadedParts);

        for (var i = 0; i < parts.Count; i++)
        {
            if (!uploadedParts.Contains(parts[i].PartNumber))
            {
                error = new S3ErrorMapping.Mapping(
                    StatusCodes.Status400BadRequest,
                    "InvalidPart",
                    "One or more of the specified parts could not be found.");
                return true;
            }
        }

        return false;
    }

    private static void CollectUploadedParts(
        IReadOnlyList<BlockListParser.Block> blocks,
        string nonceHex,
        HashSet<int> uploadedParts)
    {
        for (var i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            if (BlockListParser.TryParseBlockName(block.Name, out var blockNonce, out var partNumber)
                && string.Equals(blockNonce, nonceHex, StringComparison.Ordinal))
            {
                uploadedParts.Add(partNumber);
            }
        }
    }

    // ---------- helpers ----------

    private static async Task<(S3ErrorMapping.Mapping? Error, string? SourceIfMatch, string? PinnedVersionId)> TryEvaluateCopySourcePreconditionsAsync(
        HttpContext context,
        BlobClient blob,
        string sourceBucket,
        string sourceKey,
        string? sourceVersionId,
        S3Operation operation,
        CancellationToken cancellationToken)
    {
        var hasIfMatch = HeaderForwarding.HasConcreteEtagPrecondition(context.Request, "x-amz-copy-source-if-match");
        var hasIfNoneMatch = HeaderForwarding.HasConcreteEtagPrecondition(context.Request, "x-amz-copy-source-if-none-match");
        if (!hasIfMatch && !hasIfNoneMatch)
        {
            return (null, null, null);
        }

        using var head = await blob.HeadBlobAsync(sourceBucket, sourceKey, sourceVersionId, cancellationToken).ConfigureAwait(false);
        if (!head.IsSuccessStatusCode)
        {
            return (S3ErrorMapping.FromAzure(head, operation), null, null);
        }

        var translatedEtag = TranslateResponseEtagForS3(head);
        if (string.IsNullOrEmpty(translatedEtag))
        {
            return (new S3ErrorMapping.Mapping(
                StatusCodes.Status500InternalServerError,
                "InternalError",
                "aws2azure: Azure source blob HEAD did not return an ETag."), null, null);
        }

        var shortCircuit = HeaderForwarding.EvaluateEtagConditionals(
            context.Request.Headers,
            "x-amz-copy-source-if-match",
            "x-amz-copy-source-if-none-match",
            translatedEtag,
            isReadOperation: false);
        if (shortCircuit == StatusCodes.Status412PreconditionFailed)
        {
            return (new S3ErrorMapping.Mapping(
                StatusCodes.Status412PreconditionFailed,
                "PreconditionFailed",
                "At least one of the pre-conditions you specified did not hold."), null, null);
        }

        var pinnedVersionId = sourceVersionId ?? ReadHeader(head, "x-ms-version-id");
        if (hasIfNoneMatch && string.IsNullOrEmpty(pinnedVersionId))
        {
            return (new S3ErrorMapping.Mapping(
                StatusCodes.Status501NotImplemented,
                "NotImplemented",
                "aws2azure: x-amz-copy-source-if-none-match requires versionId for a safe copy."), null, null);
        }

        return (null, hasIfMatch && string.IsNullOrEmpty(pinnedVersionId) ? head.Headers.ETag?.Tag : null, pinnedVersionId);
    }

    private static string? TranslateResponseEtagForS3(HttpResponseMessage response)
        => HeaderForwarding.TranslateAzureResponseEtagToS3(response);

    private static S3ErrorMapping.Mapping NoSuchUpload() =>
        new(404, "NoSuchUpload",
            "The specified multipart upload does not exist. The upload ID may be invalid, expired, or scoped to a different object.");

    private static S3ErrorMapping.Mapping OperationAborted() =>
        new(409, "OperationAborted",
            "A conflicting conditional operation is currently in progress against this resource.");

    private static S3ErrorMapping.Mapping RequestTimedOut() =>
        new(400, "RequestTimeout",
            "Your socket connection to the server was not read from or written to within the timeout period.");

    private static async Task<bool> TryHandleStateSummaryResultAsync(HttpContext ctx, MultipartUploadStateStore.SummaryResult result)
    {
        if (result.Kind == MultipartUploadStateStore.ResultKind.Success && result.Summary is not null)
        {
            return true;
        }

        await S3ErrorMapping.WriteAsync(
            ctx,
            result.Kind switch
            {
                MultipartUploadStateStore.ResultKind.BucketMissing => S3ErrorMapping.NoSuchBucket(),
                MultipartUploadStateStore.ResultKind.Error => result.Error ?? new S3ErrorMapping.Mapping(500, "InternalError", "We encountered an internal error. Please try again."),
                _ => NoSuchUpload()
            }).ConfigureAwait(false);
        return false;
    }

    private static async Task<bool> TryHandleStateAcquireResultAsync(HttpContext ctx, MultipartUploadStateStore.AcquireResult result)
    {
        if (result.Kind == MultipartUploadStateStore.ResultKind.Success && result.Record is not null && !string.IsNullOrEmpty(result.LeaseId))
        {
            return true;
        }

        await S3ErrorMapping.WriteAsync(
            ctx,
            result.Kind switch
            {
                MultipartUploadStateStore.ResultKind.Conflict => OperationAborted(),
                MultipartUploadStateStore.ResultKind.BucketMissing => S3ErrorMapping.NoSuchBucket(),
                MultipartUploadStateStore.ResultKind.Error => result.Error ?? new S3ErrorMapping.Mapping(500, "InternalError", "We encountered an internal error. Please try again."),
                _ => NoSuchUpload()
            }).ConfigureAwait(false);
        return false;
    }

    private static async Task<bool> VerifyMultipartBucketGenerationAfterWriteAsync(
        HttpContext ctx,
        MultipartUploadStateStore stateStore,
        string bucket,
        string uploadId,
        string expectedGeneration,
        CancellationToken cancellationToken)
    {
        var verification = await stateStore.VerifyContainerGenerationAsync(bucket, expectedGeneration, cancellationToken).ConfigureAwait(false);
        if (verification.Kind != MultipartUploadStateStore.ResultKind.Success)
        {
            _ = await stateStore.DeleteAsync(bucket, uploadId, cancellationToken).ConfigureAwait(false);
            await S3ErrorMapping.WriteAsync(
                ctx,
                verification.Kind == MultipartUploadStateStore.ResultKind.Error
                    ? verification.Error ?? OperationAborted()
                    : OperationAborted()).ConfigureAwait(false);
            return false;
        }

        var stateRead = await stateStore.ReadSummaryAsync(bucket, uploadId, cancellationToken).ConfigureAwait(false);
        if (stateRead.Kind == MultipartUploadStateStore.ResultKind.Success)
        {
            return true;
        }

        await S3ErrorMapping.WriteAsync(
            ctx,
            stateRead.Kind == MultipartUploadStateStore.ResultKind.Error
                ? stateRead.Error ?? OperationAborted()
                : stateRead.Kind == MultipartUploadStateStore.ResultKind.BucketMissing
                    ? S3ErrorMapping.NoSuchBucket()
                : OperationAborted()).ConfigureAwait(false);
        return false;
    }

    private static async Task<(bool Retired, S3ErrorMapping.Mapping? Error)> TryRetireCompletedStateAsync(
        MultipartUploadStateStore stateStore,
        string bucket,
        string uploadId,
        MultipartUploadStateStore.MultipartUploadStateSummary summary,
        string leaseId,
        CancellationToken cancellationToken)
    {
        var deleteError = await stateStore.DeleteLeasedAsync(bucket, uploadId, leaseId, cancellationToken).ConfigureAwait(false);
        if (deleteError is null)
        {
            return (true, null);
        }

        var invalidateError = await stateStore.InvalidateLeasedAsync(summary, leaseId, cancellationToken).ConfigureAwait(false);
        if (invalidateError is null)
        {
            return (true, null);
        }

        var stateRead = await stateStore.ReadSummaryAsync(bucket, uploadId, cancellationToken).ConfigureAwait(false);
        return stateRead.Kind is MultipartUploadStateStore.ResultKind.NotFound or MultipartUploadStateStore.ResultKind.BucketMissing
            ? (true, null)
            : (false, stateRead.Kind == MultipartUploadStateStore.ResultKind.Error ? stateRead.Error ?? invalidateError : invalidateError);
    }

    private static async Task<bool> TryHandleMultipartStateStillActiveAsync(
        HttpContext ctx,
        MultipartUploadStateStore stateStore,
        string bucket,
        string uploadId,
        CancellationToken cancellationToken)
    {
        var stateRead = await stateStore.ReadSummaryAsync(bucket, uploadId, cancellationToken).ConfigureAwait(false);
        if (stateRead.Kind == MultipartUploadStateStore.ResultKind.Success)
        {
            return true;
        }

        await S3ErrorMapping.WriteAsync(
            ctx,
            stateRead.Kind switch
            {
                MultipartUploadStateStore.ResultKind.BucketMissing => S3ErrorMapping.NoSuchBucket(),
                MultipartUploadStateStore.ResultKind.Error => stateRead.Error ?? OperationAborted(),
                _ => OperationAborted()
            }).ConfigureAwait(false);
        return false;
    }

    private static async Task<bool> TryHandleLegacyMultipartFallbackAsync(
        HttpContext ctx,
        MultipartUploadStateStore stateStore,
        string bucket,
        UploadIdCodec.UploadToken token,
        CancellationToken cancellationToken)
    {
        // The state record for this upload was not found. That is normal for
        // (a) a legacy pre-migration token that never had a durable record, or
        // (b) an upload whose bucket was deleted (cascading cleanup already
        // removed the record along with the container). Distinguish those from
        // "this upload never existed / already completed or aborted" by
        // checking whether the bucket/container itself still exists — a
        // missing container must surface as NoSuchBucket regardless of the
        // token's layout, not as NoSuchUpload.
        var generation = await stateStore.ReadContainerGenerationAsync(bucket, cancellationToken).ConfigureAwait(false);
        if (generation.Kind == MultipartUploadStateStore.ResultKind.Success)
        {
            if (token.LegacyLayout)
            {
                return true;
            }

            await S3ErrorMapping.WriteAsync(ctx, NoSuchUpload()).ConfigureAwait(false);
            return false;
        }

        await S3ErrorMapping.WriteAsync(
            ctx,
            generation.Kind switch
            {
                MultipartUploadStateStore.ResultKind.BucketMissing => S3ErrorMapping.NoSuchBucket(),
                MultipartUploadStateStore.ResultKind.Error => generation.Error ?? NoSuchUpload(),
                _ => NoSuchUpload()
            }).ConfigureAwait(false);
        return false;
    }

    private static async Task ReleaseMultipartLeaseBestEffortAsync(
        MultipartUploadStateStore stateStore,
        string bucket,
        string uploadId,
        string leaseId)
    {
        using var releaseCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        _ = await stateStore.ReleaseAsync(bucket, uploadId, leaseId, releaseCts.Token).ConfigureAwait(false);
    }

    private static async Task WriteXmlAsync(HttpContext ctx, int status, string xml, CancellationToken ct, bool includeContentType = true)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(xml);
        ctx.Response.StatusCode = status;
        HeaderForwarding.ApplyCommonS3ResponseHeaders(ctx.Response);
        // Real S3 omits Content-Type on CreateMultipartUpload 200 responses
        // even though the body is XML; other multipart XML responses
        // (Complete/ListParts/ListMultipartUploads/UploadPartCopy) do emit it.
        if (includeContentType)
        {
            ctx.Response.ContentType = "application/xml";
        }
        ctx.Response.ContentLength = bytes.Length;
        await ctx.Response.Body.WriteAsync(bytes, ct).ConfigureAwait(false);
    }

    // Rebuilds the inbound S3 request's URL — scheme + host + path — so the
    // proxy can echo it back in a response body (currently only used for
    // CompleteMultipartUpload's <Location>). Preserves whichever addressing
    // style the caller used: path-style ("https://host/bucket/key") or
    // virtual-hosted ("https://bucket.host/key"). Query is deliberately
    // dropped — <Location> is the object's canonical URL, not the request
    // URL that carried ?uploadId=…
    //
    // Real S3 emits a properly percent-encoded URL — but unlike ordinary
    // object URLs, the <Location> field treats the whole key as an opaque
    // token: internal '/' characters from virtual-folder-style keys are
    // ALSO percent-encoded (e.g. key "multipart/final.txt" → Location key
    // segment "multipart%2Ffinal.txt"), not preserved as path separators.
    // S3ObjectKey.EncodeForBlobUrl deliberately preserves '/' (correct for
    // Azure blob URLs) so it must not be reused here; Uri.EscapeDataString
    // percent-encodes every RFC 3986 reserved character including '/',
    // matching this field's real-AWS shape exactly. Addressing style
    // (path-style vs. virtual-hosted) is taken from the router's own
    // S3RouteResult.VirtualHosted flag rather than re-derived by pattern-
    // matching the decoded request path against the bucket name — a key
    // that happens to start with "<bucket>/" would otherwise be
    // misclassified as path-style and get a spuriously duplicated bucket
    // segment.
    private static string BuildS3ResponseLocation(HttpRequest request, string bucket, string key, bool virtualHosted)
    {
        var scheme = string.IsNullOrEmpty(request.Scheme) ? "https" : request.Scheme;
        var host = request.Host.HasValue ? request.Host.Value : string.Empty;
        var keyPath = virtualHosted
            ? "/" + Uri.EscapeDataString(key)
            : "/" + Uri.EscapeDataString(bucket) + "/" + Uri.EscapeDataString(key);
        return scheme + "://" + host + keyPath;
    }

    private static string? ReadHeader(HttpResponseMessage resp, string name)
    {
        if (resp.Headers.TryGetValues(name, out var v1))
        {
            foreach (var v in v1) return v;
        }
        if (resp.Content.Headers.TryGetValues(name, out var v2))
        {
            foreach (var v in v2) return v;
        }
        return null;
    }

    private static string? StringOrNull(IQueryCollection query, string key)
    {
        if (!query.TryGetValue(key, out var values) || values.Count == 0)
        {
            return null;
        }

        var value = values[0];
        return string.IsNullOrEmpty(value) ? null : value;
    }

    /// <summary>
    /// Pass-through <see cref="Stream"/> that updates an
    /// <see cref="IncrementalHash"/> digest as the body is read by the
    /// HttpClient pipeline forwarding it to Azure.
    /// </summary>
    private sealed class HashingStream : Stream
    {
        private readonly Stream _inner;
        private readonly IncrementalHash _hash;
        private byte[]? _finalHash;

        public HashingStream(Stream inner, HashAlgorithmName hashAlgorithm)
        {
            _inner = inner;
            _hash = IncrementalHash.CreateHash(hashAlgorithm);
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var n = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (n > 0)
            {
                _hash.AppendData(buffer.Span[..n]);
            }
            else
                FinaliseHash();

            return n;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = _inner.Read(buffer, offset, count);
            if (n > 0)
            {
                _hash.AppendData(buffer.AsSpan(offset, n));
            }
            else
                FinaliseHash();

            return n;
        }

        public byte[] GetFinalHash() => FinaliseHash();

        private byte[] FinaliseHash()
        {
            if (_finalHash is null)
            {
                _finalHash = _hash.GetHashAndReset();
            }

            return _finalHash;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _hash.Dispose();
            }

            base.Dispose(disposing);
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

}
