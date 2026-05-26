using Aws2Azure.Core.Modules;
using Aws2Azure.Modules.S3.Errors;
using Aws2Azure.Modules.S3.Internal;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace Aws2Azure.Modules.S3.Operations;

/// <summary>
/// Handlers for slice-2 object operations:
/// <see cref="S3Operation.PutObject"/>, <see cref="S3Operation.GetObject"/>,
/// <see cref="S3Operation.HeadObject"/>, <see cref="S3Operation.DeleteObject"/>.
/// </summary>
internal static class ObjectHandlers
{
    public static async Task HandleAsync(
        HttpContext context,
        S3RouteResult route,
        BlobClient blob,
        CancellationToken cancellationToken)
    {
        var bucket = route.Bucket!;
        var key = route.Key!;

        if (!BlobClient.IsValidContainerName(bucket))
        {
            await EmitErrorAsync(context, S3ErrorMapping.InvalidBucketName(), route.Operation).ConfigureAwait(false);
            return;
        }
        if (!S3ObjectKey.IsValid(key))
        {
            await EmitErrorAsync(context, S3ErrorMapping.InvalidObjectKey(), route.Operation).ConfigureAwait(false);
            return;
        }

        switch (route.Operation)
        {
            case S3Operation.PutObject:
                await PutAsync(context, blob, bucket, key, cancellationToken).ConfigureAwait(false);
                break;
            case S3Operation.GetObject:
                await GetAsync(context, blob, bucket, key, cancellationToken).ConfigureAwait(false);
                break;
            case S3Operation.HeadObject:
                await HeadAsync(context, blob, bucket, key, cancellationToken).ConfigureAwait(false);
                break;
            case S3Operation.DeleteObject:
                await DeleteAsync(context, blob, bucket, key, cancellationToken).ConfigureAwait(false);
                break;
            case S3Operation.CopyObject:
                await CopyAsync(context, blob, bucket, key, cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    private static async Task CopyAsync(HttpContext context, BlobClient blob, string destBucket, string destKey, CancellationToken ct)
    {
        // Parse + validate x-amz-copy-source up-front; never round-trip an
        // ambiguous source to Azure (a typo'd bucket would otherwise become
        // a different error code surface).
        var rawSource = context.Request.Headers.TryGetValue("x-amz-copy-source", out var headerValues) && headerValues.Count > 0
            ? headerValues[0]
            : null;
        var parsed = CopySourceParser.Parse(rawSource);
        if (!parsed.Success)
        {
            await WriteErrorAsync(context, S3ErrorMapping.InvalidArgument(parsed.Error!)).ConfigureAwait(false);
            return;
        }
        var sourceBucket = parsed.Bucket!;
        var sourceKey = parsed.Key!;

        if (!BlobClient.IsValidContainerName(sourceBucket))
        {
            await WriteErrorAsync(context,
                new S3ErrorMapping.Mapping(400, "InvalidBucketName",
                    "The specified copy-source bucket is not valid.")).ConfigureAwait(false);
            return;
        }
        if (!S3ObjectKey.IsValid(sourceKey))
        {
            await WriteErrorAsync(context,
                S3ErrorMapping.InvalidArgument("The specified copy-source object key is not valid.")).ConfigureAwait(false);
            return;
        }

        var sourceUri = blob.BuildAccountBlobUri(sourceBucket, sourceKey);

        // S3 rejects same-bucket/same-key CopyObject unless the request
        // changes something (metadata, storage class, encryption…). We only
        // model metadata for now, so a default/COPY self-copy is always a
        // no-op and must surface as InvalidRequest to match SDK expectations.
        var directive = ReadDirective(context.Request, "x-amz-metadata-directive");
        var replace = string.Equals(directive, "REPLACE", StringComparison.OrdinalIgnoreCase);
        if (!replace && string.Equals(sourceBucket, destBucket, StringComparison.Ordinal)
            && string.Equals(sourceKey, destKey, StringComparison.Ordinal))
        {
            await WriteErrorAsync(context, new S3ErrorMapping.Mapping(400, "InvalidRequest",
                "This copy request is illegal because it is trying to copy an object to itself "
                + "without changing the object's metadata, storage class, website redirect "
                + "location or encryption attributes.")).ConfigureAwait(false);
            return;
        }
        if (!replace && !string.IsNullOrEmpty(directive) && !string.Equals(directive, "COPY", StringComparison.OrdinalIgnoreCase))
        {
            await WriteErrorAsync(context, S3ErrorMapping.InvalidArgument(
                "x-amz-metadata-directive must be COPY or REPLACE.")).ConfigureAwait(false);
            return;
        }

        using var azureReq = new HttpRequestMessage(HttpMethod.Put, blob.BuildBlobUri(destBucket, destKey));
        // The CopyObject request body is always empty; the source lives in
        // the x-ms-copy-source header. Setting a zero-length StringContent
        // gives us a Content.Headers bag for Content-Type forwarding.
        azureReq.Content = new ByteArrayContent(Array.Empty<byte>());
        azureReq.Content.Headers.ContentLength = 0;
        azureReq.Headers.TryAddWithoutValidation("x-ms-copy-source", sourceUri.AbsoluteUri);

        // Concrete-ETag copy-source preconditions: once the proxy
        // translates Azure ETags on read responses, the client round-trips
        // the translated value here and Azure cannot reverse it, so a valid
        // CAS would incorrectly 412. Fail loud rather than risk wrong-side
        // copies. The "*" sentinel is still forwarded below — Azure honors
        // it with identical semantics to S3.
        if (HasConcreteEtagPrecondition(context.Request, "x-amz-copy-source-if-match") ||
            HasConcreteEtagPrecondition(context.Request, "x-amz-copy-source-if-none-match"))
        {
            await WriteErrorAsync(context,
                new S3ErrorMapping.Mapping(StatusCodes.Status501NotImplemented,
                    "NotImplemented",
                    "aws2azure: CopyObject with a concrete-ETag x-amz-copy-source-if-match / x-amz-copy-source-if-none-match precondition is not supported (only '*' is honored). Proxy-translated S3 ETags do not round-trip back to Azure's raw ETag space."))
                .ConfigureAwait(false);
            return;
        }

        // Source-conditional headers (S3 → Azure rename).
        ForwardSourceConditional(context.Request, azureReq, "x-amz-copy-source-if-match",            "x-ms-source-if-match");
        ForwardSourceConditional(context.Request, azureReq, "x-amz-copy-source-if-none-match",       "x-ms-source-if-none-match");
        ForwardSourceConditional(context.Request, azureReq, "x-amz-copy-source-if-modified-since",   "x-ms-source-if-modified-since");
        ForwardSourceConditional(context.Request, azureReq, "x-amz-copy-source-if-unmodified-since", "x-ms-source-if-unmodified-since");

        // Metadata directive: COPY (default) preserves source metadata —
        // Azure's Copy Blob does the same when no x-ms-meta-* are sent.
        // REPLACE means use the request's metadata (Azure rule: any
        // x-ms-meta-* present replaces source metadata wholesale).
        if (replace)
        {
            ForwardMetadata(context.Request, azureReq);
        }

        using var azureResp = await blob.SendBlobRequestAsync(azureReq, ct).ConfigureAwait(false);
        if (!azureResp.IsSuccessStatusCode)
        {
            await WriteErrorAsync(context, S3ErrorMapping.FromAzure(azureResp, S3Operation.CopyObject)).ConfigureAwait(false);
            return;
        }

        // Azure intra-account copy is synchronous: x-ms-copy-status=success
        // is set in the response and the destination is fully written. If
        // the proxy is ever pointed at a cross-account topology this would
        // come back as "pending" — surface that as a clear NotImplemented
        // rather than reporting fake success to the client.
        if (azureResp.Headers.TryGetValues("x-ms-copy-status", out var copyStatus))
        {
            foreach (var s in copyStatus)
            {
                if (!string.Equals(s, "success", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteErrorAsync(context, new S3ErrorMapping.Mapping(
                        StatusCodes.Status501NotImplemented, "NotImplemented",
                        $"aws2azure: only synchronous CopyObject is supported (x-ms-copy-status={s}).")).ConfigureAwait(false);
                    return;
                }
            }
        }

        var lastModified = azureResp.Content.Headers.LastModified ?? DateTimeOffset.UtcNow;
        // Capture the raw Azure ETag of the version we just wrote — used
        // below as the If-Match guard on the destination HEAD so a racing
        // overwrite cannot make us return another writer's ETag, and as
        // a synthetic-fallback source if the HEAD itself fails.
        var rawAzureEtag = azureResp.Headers.ETag?.Tag;

        // Azure's Copy Blob does not honour x-ms-blob-* property overrides
        // on the copy itself. For REPLACE we always issue a follow-up
        // Set Blob Properties call — even when no system headers are
        // supplied — because S3 REPLACE does not preserve source
        // Content-Type / Cache-Control / etc. unless the caller asked for
        // them. The follow-up call also rewrites ETag/Last-Modified, so we
        // overwrite our return values with the response from that call to
        // keep the CopyObjectResult coherent with the destination blob.
        if (replace)
        {
            var (err, propsETag, propsLastModified) =
                await SetDestinationPropertiesAsync(context.Request, blob, destBucket, destKey, ct).ConfigureAwait(false);
            if (err is { } mapping)
            {
                await WriteErrorAsync(context, mapping).ConfigureAwait(false);
                return;
            }
            if (propsLastModified is not null) lastModified = propsLastModified.Value;
            if (!string.IsNullOrEmpty(propsETag)) rawAzureEtag = propsETag;
        }

        // Issue a HEAD against the destination to obtain the authoritative
        // ETag + Content-MD5 pair that future GET/HEAD calls will surface.
        // CopyBlob and SetBlobProperties responses don't carry Content-MD5,
        // so translating their raw ETag directly would diverge from HEAD's
        // result (synthetic-from-etag vs real-MD5-hex). Guard the HEAD with
        // If-Match against the raw ETag we just wrote so a concurrent
        // overwrite/delete cannot make us return another writer's ETag —
        // on 412/404/transient failure, fall back to translating the known
        // raw ETag (deterministic synthetic) rather than returning null or
        // a wrong-version ETag.
        var etag = await GetDestinationS3EtagAsync(blob, destBucket, destKey, rawAzureEtag, ct).ConfigureAwait(false)
                   ?? (rawAzureEtag is not null
                       ? "\"" + HeaderForwarding.TranslateAzureEtagToS3(rawAzureEtag, contentMd5Base64: null) + "\""
                       : null);

        var body = Xml.S3XmlWriter.CopyObjectResult(lastModified, etag);

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/xml; charset=utf-8";
        // Expose x-amz-version-id / x-amz-server-side-encryption equivalents
        // in the future via HeaderForwarding if needed. For this slice we
        // only emit the canonical CopyObjectResult envelope.
        await context.Response.WriteAsync(body, ct).ConfigureAwait(false);
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

    private static string? ReadDirective(HttpRequest request, string header)
    {
        if (request.Headers.TryGetValue(header, out var values))
        {
            foreach (var v in values)
            {
                if (!string.IsNullOrEmpty(v))
                {
                    return v;
                }
            }
        }
        return null;
    }

    /// <summary>
    /// REPLACE-mode metadata forwarding for CopyObject. The Copy Blob REST
    /// API itself only honours <c>x-ms-meta-*</c> (system properties like
    /// Content-Type are applied separately via Set Blob Properties).
    /// </summary>
    private static void ForwardMetadata(HttpRequest source, HttpRequestMessage target)
    {
        foreach (var kv in source.Headers)
        {
            if (kv.Key.StartsWith("x-amz-meta-", StringComparison.OrdinalIgnoreCase))
            {
                var azureName = "x-ms-meta-" + kv.Key.AsSpan("x-amz-meta-".Length).ToString();
                foreach (var value in kv.Value)
                {
                    if (!string.IsNullOrEmpty(value))
                    {
                        target.Headers.TryAddWithoutValidation(azureName, value);
                    }
                }
            }
        }
    }

    /// <summary>
    /// After a REPLACE copy, push the request's system properties onto the
    /// destination blob via Set Blob Properties. The call is unconditional
    /// (even when no system headers were supplied) because S3 REPLACE does
    /// not preserve source Content-Type / Cache-Control etc. — sending an
    /// empty Set Blob Properties clears Azure's inherited values to match.
    /// Returns the response ETag and Last-Modified so the caller can keep
    /// CopyObjectResult coherent with the final destination state, or an
    /// error mapping if Azure rejects the call.
    /// </summary>
    private static async Task<(S3ErrorMapping.Mapping? Error, string? ETag, DateTimeOffset? LastModified)>
        SetDestinationPropertiesAsync(
            HttpRequest source, BlobClient blob, string destBucket, string destKey, CancellationToken ct)
    {
        var uri = new Uri(blob.BuildBlobUri(destBucket, destKey).AbsoluteUri + "?comp=properties");
        using var req = new HttpRequestMessage(HttpMethod.Put, uri);
        req.Content = new ByteArrayContent(Array.Empty<byte>());
        req.Content.Headers.ContentLength = 0;

        ForwardBlobProperty(source, req, HeaderNames.ContentType,        "x-ms-blob-content-type");
        ForwardBlobProperty(source, req, HeaderNames.ContentEncoding,    "x-ms-blob-content-encoding");
        ForwardBlobProperty(source, req, HeaderNames.ContentLanguage,    "x-ms-blob-content-language");
        ForwardBlobProperty(source, req, HeaderNames.CacheControl,       "x-ms-blob-cache-control");
        ForwardBlobProperty(source, req, HeaderNames.ContentDisposition, "x-ms-blob-content-disposition");

        using var resp = await blob.SendBlobRequestAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            return (S3ErrorMapping.FromAzure(resp, S3Operation.CopyObject), null, null);
        }
        return (null, resp.Headers.ETag?.Tag, resp.Content.Headers.LastModified);
    }

    /// <summary>
    /// HEADs the destination blob — optionally guarded by <c>If-Match</c>
    /// against the raw Azure ETag we just wrote — to obtain the
    /// authoritative pair of ETag + Content-MD5 and return the same
    /// S3-shaped, quoted ETag HEAD/GET would emit. Returns <c>null</c>
    /// when the HEAD fails for any reason (404, 412 race against a
    /// concurrent overwrite, or transient error); callers are expected to
    /// fall back to translating the raw ETag they already have.
    /// </summary>
    private static async Task<string?> GetDestinationS3EtagAsync(
        BlobClient blob, string destBucket, string destKey, string? ifMatchRawEtag, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Head, blob.BuildBlobUri(destBucket, destKey));
        if (!string.IsNullOrEmpty(ifMatchRawEtag))
        {
            req.Headers.TryAddWithoutValidation("If-Match", ifMatchRawEtag);
        }
        using var resp = await blob.SendBlobRequestAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            return null;
        }
        return TranslateResponseEtagForS3(resp);
    }

    /// <summary>
    /// Returns a quoted, S3-shaped ETag for an Azure response — pairs the
    /// raw Azure ETag with any Content-MD5 hint and delegates to
    /// <see cref="HeaderForwarding.TranslateAzureEtagToS3"/>. Returns
    /// <c>null</c> when the response has no ETag at all (caller decides
    /// whether to fall back to a different value).
    /// </summary>
    private static string? TranslateResponseEtagForS3(HttpResponseMessage resp)
    {
        var raw = resp.Headers.ETag?.Tag;
        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }
        string? md5Base64 = null;
        if (resp.Content?.Headers.ContentMD5 is { Length: 16 } md5Bytes)
        {
            md5Base64 = Convert.ToBase64String(md5Bytes);
        }
        return "\"" + HeaderForwarding.TranslateAzureEtagToS3(raw, md5Base64) + "\"";
    }

    private static bool ForwardBlobProperty(HttpRequest source, HttpRequestMessage target, string s3Header, string azureHeader)
    {
        if (!source.Headers.TryGetValue(s3Header, out var values))
        {
            return false;
        }
        foreach (var v in values)
        {
            if (!string.IsNullOrEmpty(v))
            {
                target.Headers.TryAddWithoutValidation(azureHeader, v);
                return true;
            }
        }
        return false;
    }

    private static async Task PutAsync(HttpContext context, BlobClient blob, string bucket, string key, CancellationToken ct)
    {
        // Refuse aws-chunked uploads explicitly — decoding the AWS chunk format
        // (with or without trailing checksum) is not part of slice 2 and
        // silently forwarding the bytes would corrupt the blob.
        if (context.Request.Headers.TryGetValue("x-amz-content-sha256", out var contentSha))
        {
            foreach (var raw in contentSha)
            {
                var v = (raw ?? string.Empty).Trim();
                if (v.StartsWith("STREAMING-", StringComparison.Ordinal))
                {
                    await WriteErrorAsync(context,
                        new S3ErrorMapping.Mapping(StatusCodes.Status501NotImplemented,
                            "NotImplemented",
                            "aws2azure: aws-chunked payload uploads are not supported yet."))
                        .ConfigureAwait(false);
                    return;
                }
            }
        }

        // Concrete-ETag preconditions on writes (If-Match / If-None-Match
        // with a value other than "*") would need a HEAD-then-PUT cycle to
        // preserve atomicity once the proxy translates ETags. Until that
        // is implemented, fail loudly instead of silently dropping the
        // precondition and risking a stale-overwrite (optimistic-
        // concurrency violation). The "*" sentinel is forwarded by
        // HeaderForwarding.CopyToAzureRequest and honored by Azure with
        // identical semantics to S3.
        if (HasConcreteEtagPrecondition(context.Request, Microsoft.Net.Http.Headers.HeaderNames.IfMatch) ||
            HasConcreteEtagPrecondition(context.Request, Microsoft.Net.Http.Headers.HeaderNames.IfNoneMatch))
        {
            await WriteErrorAsync(context,
                new S3ErrorMapping.Mapping(StatusCodes.Status501NotImplemented,
                    "NotImplemented",
                    "aws2azure: PutObject with a concrete-ETag If-Match / If-None-Match precondition is not supported (only '*' is honored). Optimistic-concurrency support against Azure Blob requires a proxy-side HEAD-then-PUT cycle that is not yet implemented; rejecting to avoid silent stale-overwrites."))
                .ConfigureAwait(false);
            return;
        }

        using var azureReq = new HttpRequestMessage(HttpMethod.Put, blob.BuildBlobUri(bucket, key))
        {
            Content = new StreamContent(context.Request.Body),
        };
        // The request body is a non-replayable client stream; the Azure client's
        // retry loop would otherwise have to buffer the entire upload.
        azureReq.Options.Set(Aws2Azure.Core.Azure.AzureHttpClient.NoRetryOption, true);
        azureReq.Headers.TryAddWithoutValidation("x-ms-blob-type", "BlockBlob");
        HeaderForwarding.CopyToAzureRequest(context.Request, azureReq);
        if (context.Request.ContentLength is { } len)
        {
            azureReq.Content.Headers.ContentLength = len;
        }

        using var azureResp = await blob.SendBlobRequestAsync(azureReq, ct).ConfigureAwait(false);
        if (!azureResp.IsSuccessStatusCode)
        {
            await WriteErrorAsync(context, S3ErrorMapping.FromAzure(azureResp, S3Operation.PutObject)).ConfigureAwait(false);
            return;
        }

        // S3 PUT object response is empty with ETag in the header.
        context.Response.StatusCode = StatusCodes.Status200OK;
        HeaderForwarding.CopyFromAzureResponse(azureResp, context.Response);
        context.Response.ContentLength = 0;
    }

    private static bool HasConcreteEtagPrecondition(HttpRequest request, string header)
        => HeaderForwarding.HasConcreteEtagPrecondition(request, header);

    private static async Task GetAsync(HttpContext context, BlobClient blob, string bucket, string key, CancellationToken ct)
    {
        using var azureReq = new HttpRequestMessage(HttpMethod.Get, blob.BuildBlobUri(bucket, key));
        HeaderForwarding.CopyToAzureRequest(context.Request, azureReq);

        using var azureResp = await blob.SendBlobRequestAsync(azureReq, ct).ConfigureAwait(false);
        if (!azureResp.IsSuccessStatusCode && azureResp.StatusCode != System.Net.HttpStatusCode.NotModified)
        {
            await WriteErrorAsync(context, S3ErrorMapping.FromAzure(azureResp, S3Operation.GetObject)).ConfigureAwait(false);
            return;
        }

        context.Response.StatusCode = (int)azureResp.StatusCode;
        HeaderForwarding.CopyFromAzureResponse(azureResp, context.Response);

        if (azureResp.StatusCode == System.Net.HttpStatusCode.NotModified)
        {
            // 304 carries no body; ASP.NET will refuse a body anyway.
            return;
        }

        // Proxy-side If-Match / If-None-Match evaluation against the
        // translated ETag — Azure can't recognize the S3-shaped value the
        // client received on a prior request (see HeaderForwarding).
        var translatedEtag = context.Response.Headers[Microsoft.Net.Http.Headers.HeaderNames.ETag].ToString();
        if (!string.IsNullOrEmpty(translatedEtag))
        {
            var shortCircuit = HeaderForwarding.EvaluateEtagConditionals(context.Request, translatedEtag, isReadOperation: true);
            if (shortCircuit is { } status)
            {
                context.Response.StatusCode = status;
                context.Response.ContentLength = null;
                return;
            }
        }

        await using var azureStream = await azureResp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await azureStream.CopyToAsync(context.Response.Body, ct).ConfigureAwait(false);
    }

    private static async Task HeadAsync(HttpContext context, BlobClient blob, string bucket, string key, CancellationToken ct)
    {
        using var azureReq = new HttpRequestMessage(HttpMethod.Head, blob.BuildBlobUri(bucket, key));
        HeaderForwarding.CopyToAzureRequest(context.Request, azureReq);

        using var azureResp = await blob.SendBlobRequestAsync(azureReq, ct).ConfigureAwait(false);
        if (!azureResp.IsSuccessStatusCode && azureResp.StatusCode != System.Net.HttpStatusCode.NotModified)
        {
            await EmitHeadErrorAsync(context, S3ErrorMapping.FromAzure(azureResp, S3Operation.HeadObject)).ConfigureAwait(false);
            return;
        }

        if (azureResp.StatusCode == System.Net.HttpStatusCode.NotModified)
        {
            // Azure honored a forwarded If-None-Match: * (or any other
            // conditional that mapped directly) and returned 304. Pass it
            // through cleanly — do NOT route through the error path which
            // would stamp an x-amz-error-code header.
            context.Response.StatusCode = StatusCodes.Status304NotModified;
            HeaderForwarding.CopyFromAzureResponse(azureResp, context.Response);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        HeaderForwarding.CopyFromAzureResponse(azureResp, context.Response);

        var translatedEtag = context.Response.Headers[Microsoft.Net.Http.Headers.HeaderNames.ETag].ToString();
        if (!string.IsNullOrEmpty(translatedEtag))
        {
            var shortCircuit = HeaderForwarding.EvaluateEtagConditionals(context.Request, translatedEtag, isReadOperation: true);
            if (shortCircuit is { } status)
            {
                context.Response.StatusCode = status;
            }
        }
        // HEAD must not emit a body even though Content-Length was set above.
    }

    private static async Task DeleteAsync(HttpContext context, BlobClient blob, string bucket, string key, CancellationToken ct)
    {
        using var azureReq = new HttpRequestMessage(HttpMethod.Delete, blob.BuildBlobUri(bucket, key));
        HeaderForwarding.CopyToAzureRequest(context.Request, azureReq);

        using var azureResp = await blob.SendBlobRequestAsync(azureReq, ct).ConfigureAwait(false);
        if (azureResp.IsSuccessStatusCode)
        {
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            context.Response.ContentLength = 0;
            return;
        }

        // S3 DeleteObject is idempotent for missing blobs only — a missing
        // *container* must still surface as NoSuchBucket so callers don't
        // silently overlook a config / typo problem.
        if (azureResp.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            var mapping = S3ErrorMapping.FromAzure(azureResp, S3Operation.DeleteObject);
            if (mapping.Code == "NoSuchKey")
            {
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                context.Response.ContentLength = 0;
                return;
            }
            await WriteErrorAsync(context, mapping).ConfigureAwait(false);
            return;
        }

        await WriteErrorAsync(context, S3ErrorMapping.FromAzure(azureResp, S3Operation.DeleteObject)).ConfigureAwait(false);
    }

    private static Task EmitErrorAsync(HttpContext context, S3ErrorMapping.Mapping mapping, S3Operation op) =>
        op == S3Operation.HeadObject
            ? EmitHeadErrorAsync(context, mapping)
            : WriteErrorAsync(context, mapping);

    private static Task WriteErrorAsync(HttpContext context, S3ErrorMapping.Mapping mapping) =>
        AwsErrorResponse.WriteAsync(
            context,
            AwsErrorFormat.Xml,
            mapping.StatusCode,
            mapping.Code,
            mapping.Message);

    private static Task EmitHeadErrorAsync(HttpContext context, S3ErrorMapping.Mapping mapping)
    {
        context.Response.StatusCode = mapping.StatusCode;
        context.Response.Headers["x-amz-request-id"] = context.TraceIdentifier;
        context.Response.Headers["x-amz-error-code"] = mapping.Code;
        context.Response.ContentLength = 0;
        return Task.CompletedTask;
    }
}
