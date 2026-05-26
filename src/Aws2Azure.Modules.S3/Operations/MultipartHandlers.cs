using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using Aws2Azure.Core.Modules;
using Aws2Azure.Modules.S3.Errors;
using Aws2Azure.Modules.S3.Internal;
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

    public static Task HandleAsync(HttpContext context, S3RouteResult route, BlobClient blob, CancellationToken ct) =>
        route.Operation switch
        {
            S3Operation.CreateMultipartUpload => CreateAsync(context, blob, route.Bucket!, route.Key!, ct),
            S3Operation.UploadPart            => UploadPartAsync(context, blob, route.Bucket!, route.Key!, ct),
            S3Operation.UploadPartCopy        => UploadPartCopyAsync(context, blob, route.Bucket!, route.Key!, ct),
            S3Operation.CompleteMultipartUpload => CompleteAsync(context, blob, route.Bucket!, route.Key!, ct),
            S3Operation.AbortMultipartUpload  => AbortAsync(context, blob, route.Bucket!, route.Key!, ct),
            S3Operation.ListParts             => ListPartsAsync(context, blob, route.Bucket!, route.Key!, ct),
            S3Operation.ListMultipartUploads  => ListMultipartUploadsAsync(context, blob, route.Bucket!, ct),
            _ => WriteErrorAsync(context, S3ErrorMapping.NotImplemented(route.Operation)),
        };

    // ---------- CreateMultipartUpload ----------

    private static async Task CreateAsync(HttpContext ctx, BlobClient blob, string bucket, string key, CancellationToken ct)
    {
        if (!BlobClient.IsValidContainerName(bucket))
        {
            await WriteErrorAsync(ctx, S3ErrorMapping.InvalidBucketName()).ConfigureAwait(false);
            return;
        }
        if (!S3ObjectKey.IsValid(key))
        {
            await WriteErrorAsync(ctx, S3ErrorMapping.InvalidObjectKey()).ConfigureAwait(false);
            return;
        }

        // Probe the container so a missing bucket short-circuits with
        // NoSuchBucket instead of issuing a successful token the client
        // would then UploadPart against and fail.
        if (await CheckBucketAsync(blob, bucket, ct).ConfigureAwait(false) is { } bucketErr)
        {
            await WriteErrorAsync(ctx, bucketErr).ConfigureAwait(false);
            return;
        }

        var token = UploadIdCodec.Issue(blob.AccountName, bucket, key, blob.AccountKeyBytes);
        var xml = S3XmlWriter.InitiateMultipartUploadResult(bucket, key, token.Encoded);
        await WriteXmlAsync(ctx, StatusCodes.Status200OK, xml, ct).ConfigureAwait(false);
    }

    // ---------- UploadPart ----------

    private static async Task UploadPartAsync(HttpContext ctx, BlobClient blob, string bucket, string key, CancellationToken ct)
    {
        var uploadId = ctx.Request.Query["uploadId"].ToString();
        var partRaw  = ctx.Request.Query["partNumber"].ToString();
        if (!int.TryParse(partRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var partNumber) ||
            partNumber is < 1 or > 10000)
        {
            await WriteErrorAsync(ctx, S3ErrorMapping.InvalidArgument(
                "partNumber must be an integer in [1, 10000].")).ConfigureAwait(false);
            return;
        }

        var token = UploadIdCodec.TryDecode(uploadId, blob.AccountName, bucket, key, blob.AccountKeyBytes);
        if (token is null)
        {
            await WriteErrorAsync(ctx, NoSuchUpload()).ConfigureAwait(false);
            return;
        }

        // S3 rejects aws-chunked here for the same reason PutObject does:
        // we don't decode the chunk framing, so forwarding raw bytes would
        // corrupt the part.
        if (ctx.Request.Headers.TryGetValue("x-amz-content-sha256", out var contentSha))
        {
            foreach (var raw in contentSha)
            {
                var v = (raw ?? string.Empty).Trim();
                if (v.StartsWith("STREAMING-", StringComparison.Ordinal))
                {
                    await WriteErrorAsync(ctx,
                        new S3ErrorMapping.Mapping(StatusCodes.Status501NotImplemented,
                            "NotImplemented",
                            "aws2azure: aws-chunked payload uploads are not supported for UploadPart."))
                        .ConfigureAwait(false);
                    return;
                }
            }
        }

        var blockId = UploadIdCodec.BlockId(token.Value.NonceHex, partNumber);
        var query   = "?comp=block&blockid=" + Uri.EscapeDataString(blockId);

        // Wrap the request body so we incrementally MD5-hash the part while
        // it streams to Azure. The hex MD5 becomes the S3 part ETag the
        // client echoes back in CompleteMultipartUpload.
        using var md5 = MD5.Create();
        using var hashing = new HashingStream(ctx.Request.Body, md5);

        using var azureReq = new HttpRequestMessage(HttpMethod.Put, blob.BuildBlobUri(bucket, key, query))
        {
            Content = new StreamContent(hashing),
        };
        azureReq.Options.Set(Aws2Azure.Core.Azure.AzureHttpClient.NoRetryOption, true);
        if (ctx.Request.ContentLength is { } len)
        {
            azureReq.Content.Headers.ContentLength = len;
        }

        using var azureResp = await blob.SendBlobRequestAsync(azureReq, ct).ConfigureAwait(false);
        if (!azureResp.IsSuccessStatusCode)
        {
            await WriteErrorAsync(ctx, S3ErrorMapping.FromAzure(azureResp, S3Operation.UploadPart)).ConfigureAwait(false);
            return;
        }

        var etag = "\"" + Convert.ToHexString(md5.Hash!).ToLowerInvariant() + "\"";
        ctx.Response.StatusCode = StatusCodes.Status200OK;
        ctx.Response.Headers["ETag"] = etag;
        ctx.Response.ContentLength = 0;
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
        var uploadId = ctx.Request.Query["uploadId"].ToString();
        var partRaw  = ctx.Request.Query["partNumber"].ToString();
        if (!int.TryParse(partRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var partNumber) ||
            partNumber is < 1 or > 10000)
        {
            await WriteErrorAsync(ctx, S3ErrorMapping.InvalidArgument(
                "partNumber must be an integer in [1, 10000].")).ConfigureAwait(false);
            return;
        }

        var token = UploadIdCodec.TryDecode(uploadId, blob.AccountName, destBucket, destKey, blob.AccountKeyBytes);
        if (token is null)
        {
            await WriteErrorAsync(ctx, NoSuchUpload()).ConfigureAwait(false);
            return;
        }

        var rawSource = ctx.Request.Headers.TryGetValue("x-amz-copy-source", out var sourceValues) && sourceValues.Count > 0
            ? sourceValues[0]
            : null;
        var parsed = CopySourceParser.Parse(rawSource);
        if (!parsed.Success)
        {
            await WriteErrorAsync(ctx, S3ErrorMapping.InvalidArgument(parsed.Error!)).ConfigureAwait(false);
            return;
        }
        var sourceBucket = parsed.Bucket!;
        var sourceKey = parsed.Key!;

        if (!BlobClient.IsValidContainerName(sourceBucket))
        {
            await WriteErrorAsync(ctx,
                new S3ErrorMapping.Mapping(400, "InvalidBucketName",
                    "The specified copy-source bucket is not valid.")).ConfigureAwait(false);
            return;
        }
        if (!S3ObjectKey.IsValid(sourceKey))
        {
            await WriteErrorAsync(ctx,
                S3ErrorMapping.InvalidArgument("The specified copy-source object key is not valid.")).ConfigureAwait(false);
            return;
        }

        // S3 forbids targeting an in-flight upload's part at the same key as
        // the source — Azure would happily Put Block From URL onto the
        // source's own uncommitted-block list, which the eventual Complete
        // would silently reconcile against the live blob. Rejecting up-front
        // matches S3's "InvalidRequest" behaviour for the same scenario.
        if (string.Equals(sourceBucket, destBucket, StringComparison.Ordinal)
            && string.Equals(sourceKey, destKey, StringComparison.Ordinal))
        {
            await WriteErrorAsync(ctx, new S3ErrorMapping.Mapping(400, "InvalidRequest",
                "The copy source and destination of an UploadPartCopy must differ.")).ConfigureAwait(false);
            return;
        }

        var rangeHeader = NormalizeCopySourceRange(ctx.Request);
        if (rangeHeader.Error is { } rangeErr)
        {
            await WriteErrorAsync(ctx, rangeErr).ConfigureAwait(false);
            return;
        }

        var sourceUri = blob.BuildSourceReadSasUri(sourceBucket, sourceKey, TimeSpan.FromHours(1));
        var blockId = UploadIdCodec.BlockId(token.Value.NonceHex, partNumber);
        var destUri = blob.BuildBlobUri(destBucket, destKey, "?comp=block&blockid=" + Uri.EscapeDataString(blockId));

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

        // Concrete-ETag copy-source preconditions: proxy-translated S3
        // ETags do not round-trip back to Azure's raw ETag space, so a
        // valid CAS would incorrectly 412. Reject with 501 — "*" is still
        // forwarded below with identical semantics to S3.
        if (HeaderForwarding.HasConcreteEtagPrecondition(ctx.Request, "x-amz-copy-source-if-match") ||
            HeaderForwarding.HasConcreteEtagPrecondition(ctx.Request, "x-amz-copy-source-if-none-match"))
        {
            await WriteErrorAsync(ctx,
                new S3ErrorMapping.Mapping(StatusCodes.Status501NotImplemented,
                    "NotImplemented",
                    "aws2azure: UploadPartCopy with a concrete-ETag x-amz-copy-source-if-match / x-amz-copy-source-if-none-match precondition is not supported (only '*' is honored). Proxy-translated S3 ETags do not round-trip back to Azure's raw ETag space."))
                .ConfigureAwait(false);
            return;
        }

        // Source-conditional headers (S3 → Azure rename). Evaluated by
        // Azure on the source fetch and surfaced via 412 PreconditionFailed.
        ForwardSourceConditional(ctx.Request, azureReq, "x-amz-copy-source-if-match",            "x-ms-source-if-match");
        ForwardSourceConditional(ctx.Request, azureReq, "x-amz-copy-source-if-none-match",       "x-ms-source-if-none-match");
        ForwardSourceConditional(ctx.Request, azureReq, "x-amz-copy-source-if-modified-since",   "x-ms-source-if-modified-since");
        ForwardSourceConditional(ctx.Request, azureReq, "x-amz-copy-source-if-unmodified-since", "x-ms-source-if-unmodified-since");

        using var azureResp = await blob.SendBlobRequestAsync(azureReq, ct).ConfigureAwait(false);
        if (!azureResp.IsSuccessStatusCode)
        {
            // Distinguish the source-side 404 (NoSuchKey) from the
            // destination-side 404 (NoSuchBucket); both surface as 404 from
            // Azure with x-ms-error-code on the response.
            await WriteErrorAsync(ctx, S3ErrorMapping.FromAzure(azureResp, S3Operation.UploadPartCopy)).ConfigureAwait(false);
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

    private static void ForwardSourceConditional(HttpRequest source, HttpRequestMessage target, string s3Header, string azureHeader)
    {
        if (source.Headers.TryGetValue(s3Header, out var values))
        {
            foreach (var v in values)
            {
                if (!string.IsNullOrEmpty(v))
                {
                    target.Headers.TryAddWithoutValidation(azureHeader, v);
                }
            }
        }
    }

    // ---------- CompleteMultipartUpload ----------

    private static async Task CompleteAsync(HttpContext ctx, BlobClient blob, string bucket, string key, CancellationToken ct)
    {
        var uploadId = ctx.Request.Query["uploadId"].ToString();
        var token = UploadIdCodec.TryDecode(uploadId, blob.AccountName, bucket, key, blob.AccountKeyBytes);
        if (token is null)
        {
            await WriteErrorAsync(ctx, NoSuchUpload()).ConfigureAwait(false);
            return;
        }

        // Buffer the (small) part list body; XmlReader requires sync I/O and
        // Kestrel forbids it on the live request stream.
        using var buffered = new MemoryStream();
        try
        {
            await ctx.Request.Body.CopyToAsync(new LimitedStream(buffered, MaxCompleteBodyBytes), ct).ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            await WriteErrorAsync(ctx, new S3ErrorMapping.Mapping(
                StatusCodes.Status400BadRequest, "EntityTooLarge",
                "CompleteMultipartUpload body exceeded the allowed size.")).ConfigureAwait(false);
            return;
        }
        buffered.Position = 0;

        var parsed = CompleteMultipartUploadParser.Parse(buffered);
        if (!parsed.Success)
        {
            await WriteErrorAsync(ctx, new S3ErrorMapping.Mapping(
                StatusCodes.Status400BadRequest, "MalformedXML",
                parsed.Error ?? "The XML you provided was not well-formed.")).ConfigureAwait(false);
            return;
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
        azureReq.Content.Headers.ContentLength = body.Length;
        azureReq.Content.Headers.TryAddWithoutValidation("Content-Type", "application/xml");

        using var azureResp = await blob.SendBlobRequestAsync(azureReq, ct).ConfigureAwait(false);
        if (!azureResp.IsSuccessStatusCode)
        {
            // Azure returns 400 InvalidBlockList when a referenced block was
            // never uploaded — surface it as S3 InvalidPart so SDKs can
            // distinguish "you gave me a bogus partNumber" from generic 400s.
            var mapping = S3ErrorMapping.FromAzure(azureResp, S3Operation.CompleteMultipartUpload);
            if (azureResp.StatusCode == HttpStatusCode.BadRequest &&
                string.Equals(ReadHeader(azureResp, "x-ms-error-code"), "InvalidBlockList", StringComparison.Ordinal))
            {
                mapping = new S3ErrorMapping.Mapping(400, "InvalidPart",
                    "One or more of the specified parts could not be found.");
            }
            await WriteErrorAsync(ctx, mapping).ConfigureAwait(false);
            return;
        }

        var azureEtag = ReadHeader(azureResp, "ETag") ?? string.Empty;
        var synth = SynthesizeMultipartEtag(azureEtag, parsed.Parts.Count);
        var location = blob.BuildBlobUri(bucket, key).AbsoluteUri;
        var xml = S3XmlWriter.CompleteMultipartUploadResult(location, bucket, key, synth);
        await WriteXmlAsync(ctx, StatusCodes.Status200OK, xml, ct).ConfigureAwait(false);
    }

    // ---------- AbortMultipartUpload ----------

    private static async Task AbortAsync(HttpContext ctx, BlobClient blob, string bucket, string key, CancellationToken ct)
    {
        var uploadId = ctx.Request.Query["uploadId"].ToString();
        var token = UploadIdCodec.TryDecode(uploadId, blob.AccountName, bucket, key, blob.AccountKeyBytes);
        if (token is null)
        {
            await WriteErrorAsync(ctx, NoSuchUpload()).ConfigureAwait(false);
            return;
        }

        if (await CheckBucketAsync(blob, bucket, ct).ConfigureAwait(false) is { } bucketErr)
        {
            await WriteErrorAsync(ctx, bucketErr).ConfigureAwait(false);
            return;
        }

        // Azure auto-GCs uncommitted blocks ~7 days after their last upload,
        // which matches the UploadId TTL — so Abort is a server-side no-op
        // beyond credentials/bucket validation. Documented in the gap doc.
        ctx.Response.StatusCode = StatusCodes.Status204NoContent;
        ctx.Response.ContentLength = 0;
    }

    // ---------- ListParts ----------

    /// <summary>S3 default page size for ListParts.</summary>
    private const int DefaultMaxParts = 1000;
    /// <summary>S3 hard cap on <c>max-parts</c>.</summary>
    private const int MaxAllowedMaxParts = 1000;

    private static async Task ListPartsAsync(HttpContext ctx, BlobClient blob, string bucket, string key, CancellationToken ct)
    {
        var uploadId = ctx.Request.Query["uploadId"].ToString();
        var token = UploadIdCodec.TryDecode(uploadId, blob.AccountName, bucket, key, blob.AccountKeyBytes);
        if (token is null)
        {
            await WriteErrorAsync(ctx, NoSuchUpload()).ConfigureAwait(false);
            return;
        }

        var (maxParts, maxPartsErr) = ParseMaxParts(ctx.Request.Query);
        if (maxPartsErr is not null) { await WriteErrorAsync(ctx, maxPartsErr.Value).ConfigureAwait(false); return; }
        var (marker, markerErr) = ParsePartNumberMarker(ctx.Request.Query);
        if (markerErr is not null) { await WriteErrorAsync(ctx, markerErr.Value).ConfigureAwait(false); return; }

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
                await WriteErrorAsync(ctx, S3ErrorMapping.FromAzure(azureResp, S3Operation.ListParts)).ConfigureAwait(false);
                return;
            }
            var emptyXml = S3XmlWriter.ListPartsResult(bucket, key, uploadId,
                marker, null, maxParts, false, Array.Empty<S3XmlWriter.ListedPart>());
            await WriteXmlAsync(ctx, StatusCodes.Status200OK, emptyXml, ct).ConfigureAwait(false);
            return;
        }
        if (!azureResp.IsSuccessStatusCode)
        {
            await WriteErrorAsync(ctx, S3ErrorMapping.FromAzure(azureResp, S3Operation.ListParts)).ConfigureAwait(false);
            return;
        }

        var rawXml = await azureResp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        var parsed = BlockListParser.Parse(new MemoryStream(rawXml));

        // Filter to blocks issued by this upload (nonce match) and above the
        // PartNumberMarker. Synthesise ETag/LastModified — we don't retain
        // per-part MD5s and Azure exposes no per-block timestamp.
        var nonceHex = token.Value.NonceHex;
        var initiated = token.Value.CreatedAt;
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
        if (!BlobClient.IsValidContainerName(bucket))
        {
            await WriteErrorAsync(ctx, S3ErrorMapping.InvalidBucketName()).ConfigureAwait(false);
            return;
        }

        if (await CheckBucketAsync(blob, bucket, ct).ConfigureAwait(false) is { } bucketErr)
        {
            await WriteErrorAsync(ctx, bucketErr).ConfigureAwait(false);
            return;
        }

        // Stateless design: we don't record issued UploadIds and cannot
        // reconstruct one without the original timestamp (HMAC input). We
        // always return an empty list so SDKs receive a well-formed
        // response instead of a 501 — documented in the gap doc.
        var prefix    = ctx.Request.Query["prefix"].ToString();
        var delimiter = ctx.Request.Query["delimiter"].ToString();
        var xml = S3XmlWriter.ListMultipartUploadsResult(
            bucket,
            string.IsNullOrEmpty(prefix) ? null : prefix,
            string.IsNullOrEmpty(delimiter) ? null : delimiter,
            1000, false, Array.Empty<S3XmlWriter.ListedUpload>());
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

    // ---------- helpers ----------

    private static S3ErrorMapping.Mapping NoSuchUpload() =>
        new(404, "NoSuchUpload",
            "The specified multipart upload does not exist. The upload ID may be invalid, expired, or scoped to a different object.");

    private static async Task<S3ErrorMapping.Mapping?> CheckBucketAsync(BlobClient blob, string bucket, CancellationToken ct)
    {
        using var resp = await blob.GetContainerPropertiesAsync(bucket, ct).ConfigureAwait(false);
        if (resp.IsSuccessStatusCode)
        {
            return null;
        }
        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            return new S3ErrorMapping.Mapping(404, "NoSuchBucket", "The specified bucket does not exist.");
        }
        return S3ErrorMapping.FromAzure(resp, S3Operation.HeadBucket);
    }

    private static async Task WriteXmlAsync(HttpContext ctx, int status, string xml, CancellationToken ct)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(xml);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/xml";
        ctx.Response.ContentLength = bytes.Length;
        await ctx.Response.Body.WriteAsync(bytes, ct).ConfigureAwait(false);
    }

    private static Task WriteErrorAsync(HttpContext ctx, S3ErrorMapping.Mapping mapping) =>
        AwsErrorResponse.WriteAsync(ctx, Aws2Azure.Core.Modules.AwsErrorFormat.Xml,
            mapping.StatusCode, mapping.Code, mapping.Message);

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

    /// <summary>
    /// Builds an S3-style multipart ETag of the form <c>"{hash}-{count}"</c>.
    /// We don't have the per-part MD5s after the fact (CompleteMultipart
    /// happens long after UploadPart) so the hash component is derived from
    /// the Azure blob ETag — opaque but stable per commit. The dash-suffix
    /// is what SDKs use to detect a multipart object.
    /// </summary>
    private static string SynthesizeMultipartEtag(string azureEtag, int partCount)
    {
        var stripped = azureEtag.Trim('"');
        // 0x-prefixed hex from Azure -> drop the prefix so we look more like md5hex
        if (stripped.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            stripped = stripped[2..];
        }
        // Pad/truncate to 32 hex chars so the shape matches MD5hex.
        if (stripped.Length < 32)
        {
            stripped = stripped.PadRight(32, '0');
        }
        else if (stripped.Length > 32)
        {
            stripped = stripped[..32];
        }
        return "\"" + stripped.ToLowerInvariant() + "-" +
            partCount.ToString(CultureInfo.InvariantCulture) + "\"";
    }

    /// <summary>
    /// Pass-through <see cref="Stream"/> that updates an
    /// <see cref="IncrementalHash"/>-style digest as the body is read by
    /// the HttpClient pipeline forwarding it to Azure. We use
    /// <see cref="HashAlgorithm"/> directly so the final hash is available
    /// via <see cref="HashAlgorithm.Hash"/> once <see cref="TransformFinalBlock"/>
    /// runs.
    /// </summary>
    private sealed class HashingStream : Stream
    {
        private readonly Stream _inner;
        private readonly HashAlgorithm _hash;
        private bool _finalised;

        public HashingStream(Stream inner, HashAlgorithm hash)
        {
            _inner = inner;
            _hash = hash;
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
                _hash.TransformBlock(buffer.Span.Slice(0, n).ToArray(), 0, n, null, 0);
            }
            else if (!_finalised)
            {
                _hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                _finalised = true;
            }
            return n;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = _inner.Read(buffer, offset, count);
            if (n > 0)
            {
                _hash.TransformBlock(buffer, offset, n, null, 0);
            }
            else if (!_finalised)
            {
                _hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                _finalised = true;
            }
            return n;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// Body cap wrapper used when buffering <c>CompleteMultipartUpload</c>
    /// XML. Throws after <see cref="_limit"/> bytes have been written so
    /// the caller can return EntityTooLarge instead of OOM'ing.
    /// </summary>
    private sealed class LimitedStream : Stream
    {
        private readonly Stream _inner;
        private readonly int _limit;
        private int _written;

        public LimitedStream(Stream inner, int limit) { _inner = inner; _limit = limit; }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _written; set => throw new NotSupportedException(); }

        public override void Flush() => _inner.Flush();
        public override Task FlushAsync(CancellationToken ct) => _inner.FlushAsync(ct);

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _written += buffer.Length;
            if (_written > _limit)
            {
                throw new InvalidDataException($"Body exceeded the {_limit}-byte limit.");
            }
            await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            _written += count;
            if (_written > _limit)
            {
                throw new InvalidDataException($"Body exceeded the {_limit}-byte limit.");
            }
            _inner.Write(buffer, offset, count);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
