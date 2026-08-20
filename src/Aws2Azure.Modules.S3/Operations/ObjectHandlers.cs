using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Aws2Azure.Core.Azure;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Core.Modules;
using Aws2Azure.Core.SigV4;
using Aws2Azure.Modules.S3.Errors;
using Aws2Azure.Modules.S3.Internal;
using Aws2Azure.Modules.S3.Streaming;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace Aws2Azure.Modules.S3.Operations;

/// <summary>
/// Handlers for slice-2 object operations:
/// <see cref="S3Operation.PutObject"/>, <see cref="S3Operation.GetObject"/>,
/// <see cref="S3Operation.HeadObject"/>, <see cref="S3Operation.DeleteObject"/>.
/// </summary>
internal static class ObjectHandlers
{
    private const long MinPresignedPostFormFieldBytes = 64 * 1024;
    private const long MaxBufferedPresignedPostFileBytes = 64L * 1024 * 1024;

    public static async Task HandleAsync(
        HttpContext context,
        S3RouteResult route,
        BlobClient blob,
        CancellationToken cancellationToken,
        ICredentialResolver? credentials = null)
    {
        var bucket = route.Bucket!;
        var key = route.Key;

        if (S3ErrorMapping.ClassifyLookupBucketName(bucket) is { } bucketError)
        {
            await EmitErrorAsync(context, bucketError, route.Operation).ConfigureAwait(false);
            return;
        }
        if (route.Operation != S3Operation.PostObject && !S3ObjectKey.IsValid(key!))
        {
            await EmitErrorAsync(context, S3ErrorMapping.InvalidObjectKey(), route.Operation).ConfigureAwait(false);
            return;
        }

        switch (route.Operation)
        {
            case S3Operation.PutObject:
                await PutAsync(context, blob, bucket, key!, credentials, cancellationToken).ConfigureAwait(false);
                break;
            case S3Operation.GetObject:
                await GetAsync(context, blob, bucket, key!, cancellationToken).ConfigureAwait(false);
                break;
            case S3Operation.HeadObject:
                await HeadAsync(context, blob, bucket, key!, cancellationToken).ConfigureAwait(false);
                break;
            case S3Operation.DeleteObject:
                await DeleteAsync(context, blob, bucket, key!, cancellationToken).ConfigureAwait(false);
                break;
            case S3Operation.CopyObject:
                await CopyAsync(context, blob, bucket, key!, cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    public static async Task HandlePostObjectAsync(
        HttpContext context,
        S3RouteResult route,
        AzureHttpClient http,
        ICredentialResolver credentials,
        CancellationToken cancellationToken)
    {
        var bucket = route.Bucket!;
        if (S3ErrorMapping.ClassifyLookupBucketName(bucket) is { } bucketError)
        {
            await S3ErrorMapping.WriteAsync(context, bucketError).ConfigureAwait(false);
            return;
        }

        if (!TryCreateMultipartReader(context.Request, out var reader))
        {
            await S3ErrorMapping.WriteAsync(context, S3ErrorMapping.InvalidArgument(
                "Presigned POST uploads require multipart/form-data with a valid boundary.")).ConfigureAwait(false);
            return;
        }

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        MultipartSection? fileSection = null;
        string? fileContentType = null;
        string? fileName = null;
        long fileLength = 0;

        try
        {
            while (true)
            {
                var section = await reader.ReadNextSectionAsync(cancellationToken).ConfigureAwait(false);
                if (section is null)
                {
                    break;
                }

                if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition))
                {
                    continue;
                }

                var fieldName = disposition.Name.Value?.Trim('"');
                if (string.IsNullOrEmpty(fieldName))
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(disposition.FileName.Value) || !string.IsNullOrEmpty(disposition.FileNameStar.Value))
                {
                    fileSection = section;
                    fileContentType = section.ContentType;
                    fileName = disposition.FileNameStar.Value ?? disposition.FileName.Value;
                    break;
                }

                var value = await ReadSmallFormValueAsync(section.Body, cancellationToken).ConfigureAwait(false);
                fields[fieldName] = value;
            }
        }
        catch (InvalidDataException ex)
        {
            await S3ErrorMapping.WriteAsync(context, S3ErrorMapping.InvalidArgument(ex.Message)).ConfigureAwait(false);
            return;
        }

        if (fileSection is null)
        {
            await S3ErrorMapping.WriteAsync(context, S3ErrorMapping.InvalidArgument(
                "Presigned POST form is missing the file body.")).ConfigureAwait(false);
            return;
        }

        if (!fields.TryGetValue("key", out var rawKey) || string.IsNullOrEmpty(rawKey))
        {
            await S3ErrorMapping.WriteAsync(context, S3ErrorMapping.InvalidArgument(
                "Presigned POST form is missing the key field.")).ConfigureAwait(false);
            return;
        }

        var key = ResolvePostObjectKey(rawKey, fileName);
        if (!S3ObjectKey.IsValid(key))
        {
            await S3ErrorMapping.WriteAsync(context, S3ErrorMapping.InvalidObjectKey()).ConfigureAwait(false);
            return;
        }

        var validatedFields = new Dictionary<string, string>(fields, StringComparer.OrdinalIgnoreCase)
        {
            ["key"] = key,
        };

        var prevalidatedPolicy = PresignedPostPolicy.Validate(
            bucket,
            validatedFields,
            fileLength: 0,
            credentials,
            skipContentLengthRange: true);
        if (!prevalidatedPolicy.Success)
        {
            await S3ErrorMapping.WriteAsync(context, prevalidatedPolicy.Error!.Value).ConfigureAwait(false);
            return;
        }

        byte[] fileBody;
        try
        {
            fileBody = await ReadPostObjectFileAsync(fileSection.Body, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException ex)
        {
            await S3ErrorMapping.WriteAsync(context, new S3ErrorMapping.Mapping(
                StatusCodes.Status400BadRequest,
                "EntityTooLarge",
                ex.Message)).ConfigureAwait(false);
            return;
        }
        fileLength = fileBody.LongLength;
        var policy = PresignedPostPolicy.Validate(bucket, validatedFields, fileLength, credentials);
        if (!policy.Success)
        {
            await S3ErrorMapping.WriteAsync(context, policy.Error!.Value).ConfigureAwait(false);
            return;
        }

        context.Items["aws2azure.accessKeyId"] = policy.AccessKeyId;
        if (!string.IsNullOrEmpty(policy.Region))
        {
            context.Items["aws2azure.region"] = policy.Region;
        }

        if (credentials.GetAzureCredentialsFor(policy.AccessKeyId!, AzureService.Blob) is not BlobCredentials blobCredentials)
        {
            await S3ErrorMapping.WriteAsync(context, S3ErrorMapping.NoCredentials()).ConfigureAwait(false);
            return;
        }

        var blob = new BlobClient(http, blobCredentials);
        if (blob.IsInternalContainer(bucket))
        {
            await S3ErrorMapping.WriteAsync(context, S3ErrorMapping.NoSuchBucket()).ConfigureAwait(false);
            return;
        }

        await PostObjectAsync(context, blob, bucket, key, fields, fileBody, fileContentType, policy.SuccessStatusCode, cancellationToken).ConfigureAwait(false);
    }

    private static async Task CopyAsync(HttpContext context, BlobClient blob, string destBucket, string destKey, CancellationToken ct)
    {
        // Parse + validate x-amz-copy-source up-front; never round-trip an
        // ambiguous source to Azure (a typo'd bucket would otherwise become
        // a different error code surface).
        var parsed = CopySourceParser.ParseAndValidate(context.Request);
        if (!parsed.Success)
        {
            await S3ErrorMapping.WriteAsync(context, parsed.Error).ConfigureAwait(false);
            return;
        }
        var sourceBucket = parsed.Bucket!;
        var sourceKey = parsed.Key!;
        var sourceVersionId = parsed.VersionId;
        if (blob.IsInternalContainer(sourceBucket))
        {
            await S3ErrorMapping.WriteAsync(context, S3ErrorMapping.NoSuchBucket()).ConfigureAwait(false);
            return;
        }

        // S3 rejects same-bucket/same-key CopyObject unless the request
        // changes something (metadata, storage class, encryption…). We only
        // model metadata for now, so a default/COPY self-copy is always a
        // no-op and must surface as InvalidRequest to match SDK expectations.
        var directive = ReadDirective(context.Request, "x-amz-metadata-directive");
        var replace = string.Equals(directive, "REPLACE", StringComparison.OrdinalIgnoreCase);
        if (!replace && string.IsNullOrEmpty(sourceVersionId)
            && string.Equals(sourceBucket, destBucket, StringComparison.Ordinal)
            && string.Equals(sourceKey, destKey, StringComparison.Ordinal))
        {
            await S3ErrorMapping.WriteAsync(context, new S3ErrorMapping.Mapping(400, "InvalidRequest",
                "This copy request is illegal because it is trying to copy an object to itself "
                + "without changing the object's metadata, storage class, website redirect "
                + "location or encryption attributes.")).ConfigureAwait(false);
            return;
        }
        if (!replace && !string.IsNullOrEmpty(directive) && !string.Equals(directive, "COPY", StringComparison.OrdinalIgnoreCase))
        {
            await S3ErrorMapping.WriteAsync(context, S3ErrorMapping.InvalidArgument(
                "x-amz-metadata-directive must be COPY or REPLACE.")).ConfigureAwait(false);
            return;
        }

        // Concrete-ETag copy-source preconditions: once the proxy
        // translates Azure ETags on read responses, the client round-trips
        // the translated value here and Azure cannot reverse it, so a valid
        // CAS would incorrectly 412. Fail loud rather than risk wrong-side
        // copies. The "*" sentinel is still forwarded below — Azure honors
        // it with identical semantics to S3.
        var sourceConditionals = await TryEvaluateCopySourcePreconditionsAsync(
            context, blob, sourceBucket, sourceKey, sourceVersionId, S3Operation.CopyObject, ct).ConfigureAwait(false);
        if (sourceConditionals.Error is { } preconditionError)
        {
            await S3ErrorMapping.WriteAsync(context, preconditionError).ConfigureAwait(false);
            return;
        }

        var sourceUri = BuildSourceObjectUri(
            blob,
            sourceBucket,
            sourceKey,
            sourceConditionals.PinnedVersionId ?? sourceVersionId);
        using var azureReq = new HttpRequestMessage(HttpMethod.Put, blob.BuildBlobUri(destBucket, destKey));
        azureReq.Content = new ByteArrayContent(Array.Empty<byte>());
        azureReq.Content.Headers.ContentLength = 0;
        azureReq.Headers.TryAddWithoutValidation("x-ms-copy-source", sourceUri.AbsoluteUri);
        HeaderForwarding.ForwardCopySourceConditionals(context.Request, azureReq);
        if (sourceConditionals.SourceIfMatch is { } sourceIfMatch)
        {
            azureReq.Headers.TryAddWithoutValidation("x-ms-source-if-match", sourceIfMatch);
        }

        // Metadata directive: COPY (default) preserves source metadata —
        // Azure's Copy Blob does the same when no x-ms-meta-* are sent.
        // REPLACE means use the request's metadata (Azure rule: any
        // x-ms-meta-* present replaces source metadata wholesale).
        if (replace)
        {
            HeaderForwarding.ForwardMetadata(context.Request, azureReq);
        }

        using var azureResp = await blob.SendBlobRequestAsync(azureReq, ct).ConfigureAwait(false);
        if (!azureResp.IsSuccessStatusCode)
        {
            await S3ErrorMapping.WriteAsync(context, S3ErrorMapping.FromAzure(azureResp, S3Operation.CopyObject)).ConfigureAwait(false);
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
                    await S3ErrorMapping.WriteAsync(context, new S3ErrorMapping.Mapping(
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
        string? etag = null;
        if (replace)
        {
            var (err, propsETag, propsLastModified) =
                await SetDestinationPropertiesAsync(context.Request, blob, destBucket, destKey, rawAzureEtag, ct).ConfigureAwait(false);
            if (err is { } mapping)
            {
                await S3ErrorMapping.WriteAsync(context, mapping).ConfigureAwait(false);
                return;
            }
            if (propsLastModified is not null) lastModified = propsLastModified.Value;
            if (!string.IsNullOrEmpty(propsETag)) rawAzureEtag = propsETag;

            var (metadataError, metadataETag, metadataLastModified) =
                await SetDestinationMetadataAsync(context.Request, blob, destBucket, destKey, rawAzureEtag, ct).ConfigureAwait(false);
            if (metadataError is { } metadataMapping)
            {
                await S3ErrorMapping.WriteAsync(context, metadataMapping).ConfigureAwait(false);
                return;
            }
            if (metadataLastModified is not null) lastModified = metadataLastModified.Value;
            if (!string.IsNullOrEmpty(metadataETag)) rawAzureEtag = metadataETag;
        }
        else
        {
            var (metadataError, metadataS3Etag, metadataETag, metadataLastModified) =
                await StripInternalCopiedMetadataAsync(blob, destBucket, destKey, rawAzureEtag, ct).ConfigureAwait(false);
            if (metadataError is { } metadataMapping)
            {
                await S3ErrorMapping.WriteAsync(context, metadataMapping).ConfigureAwait(false);
                return;
            }
            if (!string.IsNullOrEmpty(metadataS3Etag)) etag = metadataS3Etag;
            if (metadataLastModified is not null) lastModified = metadataLastModified.Value;
            if (!string.IsNullOrEmpty(metadataETag)) rawAzureEtag = metadataETag;
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
        etag ??= await GetDestinationS3EtagAsync(blob, destBucket, destKey, rawAzureEtag, ct).ConfigureAwait(false)
                 ?? (rawAzureEtag is not null
                     ? "\"" + HeaderForwarding.TranslateAzureEtagToS3(rawAzureEtag, contentMd5Base64: null) + "\""
                     : null);

        var body = Xml.S3XmlWriter.CopyObjectResult(lastModified, etag);

        context.Response.StatusCode = StatusCodes.Status200OK;
        HeaderForwarding.ApplyCommonS3ResponseHeaders(context.Response, destBucket);
        context.Response.ContentType = "application/xml; charset=utf-8";
        // Expose x-amz-version-id / x-amz-server-side-encryption equivalents
        // in the future via HeaderForwarding if needed. For this slice we
        // only emit the canonical CopyObjectResult envelope.
        await context.Response.WriteAsync(body, ct).ConfigureAwait(false);
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
            HttpRequest source, BlobClient blob, string destBucket, string destKey, string? ifMatchEtag, CancellationToken ct)
    {
        var uri = new Uri(blob.BuildBlobUri(destBucket, destKey).AbsoluteUri + "?comp=properties");
        using var req = new HttpRequestMessage(HttpMethod.Put, uri);
        req.Content = new ByteArrayContent(Array.Empty<byte>());
        req.Content.Headers.ContentLength = 0;
        if (!string.IsNullOrEmpty(ifMatchEtag))
        {
            req.Headers.TryAddWithoutValidation("If-Match", ifMatchEtag);
        }

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

    private static async Task<(S3ErrorMapping.Mapping? Error, string? ETag, DateTimeOffset? LastModified)>
        SetDestinationMetadataAsync(
            HttpRequest source, BlobClient blob, string destBucket, string destKey, string? ifMatchEtag, CancellationToken ct)
    {
        var uri = new Uri(blob.BuildBlobUri(destBucket, destKey).AbsoluteUri + "?comp=metadata");
        using var req = new HttpRequestMessage(HttpMethod.Put, uri);
        req.Content = new ByteArrayContent(Array.Empty<byte>());
        req.Content.Headers.ContentLength = 0;
        if (!string.IsNullOrEmpty(ifMatchEtag))
        {
            req.Headers.TryAddWithoutValidation("If-Match", ifMatchEtag);
        }
        HeaderForwarding.ForwardMetadata(source, req);

        using var resp = await blob.SendBlobRequestAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            return (S3ErrorMapping.FromAzure(resp, S3Operation.CopyObject), null, null);
        }

        return (null, resp.Headers.ETag?.Tag, resp.Content.Headers.LastModified);
    }

    private static async Task<(S3ErrorMapping.Mapping? Error, string? S3Etag, string? ETag, DateTimeOffset? LastModified)>
        StripInternalCopiedMetadataAsync(
            BlobClient blob,
            string destBucket,
            string destKey,
            string? ifMatchEtag,
            CancellationToken ct)
    {
        using var head = new HttpRequestMessage(HttpMethod.Head, blob.BuildBlobUri(destBucket, destKey));
        if (!string.IsNullOrEmpty(ifMatchEtag))
        {
            head.Headers.TryAddWithoutValidation("If-Match", ifMatchEtag);
        }

        using var headResp = await blob.SendBlobRequestAsync(head, ct).ConfigureAwait(false);
        if (!headResp.IsSuccessStatusCode || !HeaderForwarding.HasInternalMultipartPartCountMetadata(headResp))
        {
            return (null, headResp.IsSuccessStatusCode ? HeaderForwarding.TranslateAzureResponseEtagToS3(headResp) : null, null, headResp.Content.Headers.LastModified);
        }

        var currentEtag = headResp.Headers.ETag?.Tag ?? ifMatchEtag;
        var uri = new Uri(blob.BuildBlobUri(destBucket, destKey).AbsoluteUri + "?comp=metadata");
        using var req = new HttpRequestMessage(HttpMethod.Put, uri);
        req.Content = new ByteArrayContent(Array.Empty<byte>());
        req.Content.Headers.ContentLength = 0;
        if (!string.IsNullOrEmpty(currentEtag))
        {
            req.Headers.TryAddWithoutValidation("If-Match", currentEtag);
        }

        HeaderForwarding.CopyUserMetadataFromAzureResponse(headResp, req);

        using var resp = await blob.SendBlobRequestAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            if (resp.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.NotFound)
            {
                return (null, null, null, null);
            }

            return (S3ErrorMapping.FromAzure(resp, S3Operation.CopyObject), null, null, null);
        }

        return (null, null, resp.Headers.ETag?.Tag, resp.Content.Headers.LastModified);
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
        return HeaderForwarding.TranslateAzureResponseEtagToS3(resp);
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

    private static async Task PutAsync(HttpContext context, BlobClient blob, string bucket, string key, ICredentialResolver? credentials, CancellationToken ct)
    {
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
            await S3ErrorMapping.WriteAsync(context,
                new S3ErrorMapping.Mapping(StatusCodes.Status501NotImplemented,
                    "NotImplemented",
                    "aws2azure: PutObject with a concrete-ETag If-Match / If-None-Match precondition is not supported (only '*' is honored). Optimistic-concurrency support against Azure Blob requires a proxy-side HEAD-then-PUT cycle that is not yet implemented; rejecting to avoid silent stale-overwrites."))
                .ConfigureAwait(false);
            return;
        }

        var body = AwsChunkedRequest.PrepareBodyStream(context.Request, credentials);

        try
        {
            using var azureReq = new HttpRequestMessage(HttpMethod.Put, blob.BuildBlobUri(bucket, key))
            {
                Content = new StreamContent(body.Body),
            };
            // The request body is a non-replayable client stream; the Azure client's
            // retry loop would otherwise have to buffer the entire upload.
            azureReq.Options.Set(Aws2Azure.Core.Azure.AzureHttpClient.NoRetryOption, true);
            azureReq.Headers.TryAddWithoutValidation("x-ms-blob-type", "BlockBlob");
            HeaderForwarding.CopyToAzureRequest(context.Request, azureReq);

            // If we decoded aws-chunked, remove that encoding from Content-Encoding
            // since the body is now decoded. Preserve any other encodings (e.g. gzip).
            if (body.Decoder is not null && azureReq.Content.Headers.ContentEncoding.Contains("aws-chunked"))
            {
                var encodings = azureReq.Content.Headers.ContentEncoding.ToList();
                encodings.Remove("aws-chunked");
                azureReq.Content.Headers.ContentEncoding.Clear();
                foreach (var enc in encodings)
                {
                    azureReq.Content.Headers.ContentEncoding.Add(enc);
                }
            }

            if (body.ContentLength is { } len)
            {
                azureReq.Content.Headers.ContentLength = len;
            }

            using var azureResp = await blob.SendBlobRequestAsync(azureReq, ct).ConfigureAwait(false);
            if (!azureResp.IsSuccessStatusCode)
            {
                await S3ErrorMapping.WriteAsync(context, S3ErrorMapping.FromAzure(azureResp, S3Operation.PutObject)).ConfigureAwait(false);
                return;
            }

            // S3 PUT object response is empty with ETag in the header.
            context.Response.StatusCode = StatusCodes.Status200OK;
            HeaderForwarding.ApplyCommonS3ResponseHeaders(context.Response);
            HeaderForwarding.CopyFromAzureResponse(azureResp, context.Response);
            HeaderForwarding.ApplyObjectResponseHeaders(
                context.Response,
                serverSideEncryption: true,
                checksumType: true);
            context.Response.ContentLength = 0;
        }
        finally
        {
            body.Decoder?.Dispose();
        }
    }

    private static bool HasConcreteEtagPrecondition(HttpRequest request, string header)
        => HeaderForwarding.HasConcreteEtagPrecondition(request, header);

    private static Uri BuildObjectUri(BlobClient blob, string bucket, string key, HttpRequest request)
    {
        var versionId = HeaderForwarding.ReadVersionIdQuery(request);
        if (string.IsNullOrEmpty(versionId))
        {
            return blob.BuildBlobUri(bucket, key);
        }
        return blob.BuildBlobUri(bucket, key, "?versionid=" + Uri.EscapeDataString(versionId));
    }

    private static async Task GetAsync(HttpContext context, BlobClient blob, string bucket, string key, CancellationToken ct)
    {
        using var azureReq = new HttpRequestMessage(HttpMethod.Get, BuildObjectUri(blob, bucket, key, context.Request));
        HeaderForwarding.CopyToAzureRequest(context.Request, azureReq);

        using var azureResp = await blob.SendBlobRequestAsync(azureReq, ct).ConfigureAwait(false);
        if (!azureResp.IsSuccessStatusCode && azureResp.StatusCode != System.Net.HttpStatusCode.NotModified)
        {
            await S3ErrorMapping.WriteAsync(context, S3ErrorMapping.FromAzure(azureResp, S3Operation.GetObject)).ConfigureAwait(false);
            return;
        }

        context.Response.StatusCode = (int)azureResp.StatusCode;
        HeaderForwarding.ApplyCommonS3ResponseHeaders(context.Response);
        HeaderForwarding.CopyFromAzureResponse(azureResp, context.Response);
        HeaderForwarding.ApplyObjectResponseHeaders(
            context.Response,
            defaultContentType: azureResp.StatusCode != System.Net.HttpStatusCode.NotModified,
            serverSideEncryption: true);
        ApplyGetResponseOverrides(context.Request, context.Response);

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
                if (status == StatusCodes.Status412PreconditionFailed)
                {
                    // A failed If-Match on a read is 412 PreconditionFailed.
                    // Real S3 returns a full <Error> envelope here (SDK clients
                    // dispatch on the <Code>), not an empty body — so emit one
                    // rather than a bare status. Drop the object-metadata
                    // headers copied from Azure's 200 so the error response
                    // doesn't leak the object's ETag / Last-Modified / etc.
                    context.Response.Headers.Clear();
                    await S3ErrorMapping.WriteAsync(context, new S3ErrorMapping.Mapping(
                        StatusCodes.Status412PreconditionFailed,
                        "PreconditionFailed",
                        "At least one of the pre-conditions you specified did not hold.")).ConfigureAwait(false);
                    return;
                }

                // 304 Not Modified (If-None-Match matched): no body, faithful
                // to both S3 and HTTP — leave the headers as Azure returned.
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
        using var azureReq = new HttpRequestMessage(HttpMethod.Head, BuildObjectUri(blob, bucket, key, context.Request));
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
            HeaderForwarding.ApplyCommonS3ResponseHeaders(context.Response);
            HeaderForwarding.CopyFromAzureResponse(azureResp, context.Response);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        HeaderForwarding.ApplyCommonS3ResponseHeaders(context.Response);
        HeaderForwarding.CopyFromAzureResponse(azureResp, context.Response);
        HeaderForwarding.ApplyObjectResponseHeaders(
            context.Response,
            defaultContentType: true,
            serverSideEncryption: true);

        var translatedEtag = context.Response.Headers[Microsoft.Net.Http.Headers.HeaderNames.ETag].ToString();
        if (!string.IsNullOrEmpty(translatedEtag))
        {
            var shortCircuit = HeaderForwarding.EvaluateEtagConditionals(context.Request, translatedEtag, isReadOperation: true);
            if (shortCircuit is { } status)
            {
                if (status == StatusCodes.Status412PreconditionFailed)
                {
                    // A failed If-Match on a HEAD is 412 PreconditionFailed.
                    // HEAD carries no body, but the response must not leak the
                    // object's success metadata (ETag / Last-Modified /
                    // Content-Length) copied from Azure's 200 — emit a clean
                    // bodiless error with x-amz-error-code, like every other
                    // HEAD error path.
                    context.Response.Headers.Clear();
                    await EmitHeadErrorAsync(context, new S3ErrorMapping.Mapping(
                        StatusCodes.Status412PreconditionFailed,
                        "PreconditionFailed",
                        "At least one of the pre-conditions you specified did not hold.")).ConfigureAwait(false);
                    return;
                }

                // 304 Not Modified (If-None-Match matched): bodiless, headers
                // preserved as Azure returned them.
                context.Response.StatusCode = status;
            }
        }
        // HEAD must not emit a body even though Content-Length was set above.
    }

    private static async Task DeleteAsync(HttpContext context, BlobClient blob, string bucket, string key, CancellationToken ct)
    {
        using var azureReq = new HttpRequestMessage(HttpMethod.Delete, BuildObjectUri(blob, bucket, key, context.Request));
        HeaderForwarding.CopyToAzureRequest(context.Request, azureReq);

        using var azureResp = await blob.SendBlobRequestAsync(azureReq, ct).ConfigureAwait(false);
        if (azureResp.IsSuccessStatusCode)
        {
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            HeaderForwarding.ApplyCommonS3ResponseHeaders(context.Response);
            HeaderForwarding.CopyFromAzureResponse(azureResp, context.Response);
            if (azureResp.Headers.TryGetValues("x-ms-delete-type-permanent", out var deleteType))
            {
                foreach (var value in deleteType)
                {
                    if (string.Equals(value, "false", StringComparison.OrdinalIgnoreCase))
                    {
                        context.Response.Headers["x-amz-delete-marker"] = "true";
                        break;
                    }
                }
            }
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
                HeaderForwarding.ApplyCommonS3ResponseHeaders(context.Response);
                context.Response.ContentLength = 0;
                return;
            }
            await S3ErrorMapping.WriteAsync(context, mapping).ConfigureAwait(false);
            return;
        }

        await S3ErrorMapping.WriteAsync(context, S3ErrorMapping.FromAzure(azureResp, S3Operation.DeleteObject)).ConfigureAwait(false);
    }

    private static async Task PostObjectAsync(
        HttpContext context,
        BlobClient blob,
        string bucket,
        string key,
        IReadOnlyDictionary<string, string> fields,
        byte[] fileBody,
        string? fileContentType,
        int successStatusCode,
        CancellationToken ct)
    {
        using var azureReq = new HttpRequestMessage(HttpMethod.Put, blob.BuildBlobUri(bucket, key))
        {
            Content = new ByteArrayContent(fileBody),
        };
        azureReq.Options.Set(Aws2Azure.Core.Azure.AzureHttpClient.NoRetryOption, true);
        azureReq.Headers.TryAddWithoutValidation("x-ms-blob-type", "BlockBlob");

        if (!TryApplyPostObjectHeaders(fields, azureReq, fileContentType, out var headerError))
        {
            await S3ErrorMapping.WriteAsync(context, headerError!.Value).ConfigureAwait(false);
            return;
        }

        using var azureResp = await blob.SendBlobRequestAsync(azureReq, ct).ConfigureAwait(false);
        if (!azureResp.IsSuccessStatusCode)
        {
            await S3ErrorMapping.WriteAsync(context, S3ErrorMapping.FromAzure(azureResp, S3Operation.PutObject)).ConfigureAwait(false);
            return;
        }

        context.Response.StatusCode = successStatusCode;
        HeaderForwarding.CopyFromAzureResponse(azureResp, context.Response);
        if (successStatusCode == StatusCodes.Status204NoContent)
        {
            context.Response.ContentLength = 0;
        }
    }

    private static bool TryApplyPostObjectHeaders(
        IReadOnlyDictionary<string, string> fields,
        HttpRequestMessage request,
        string? fileContentType,
        out S3ErrorMapping.Mapping? error)
    {
        error = null;
        var contentType = TryGetField(fields, HeaderNames.ContentType, out var explicitContentType)
            ? explicitContentType
            : fileContentType;
        if (!string.IsNullOrEmpty(contentType))
        {
            if (!System.Net.Http.Headers.MediaTypeHeaderValue.TryParse(contentType, out var parsedContentType))
            {
                error = S3ErrorMapping.InvalidArgument("Presigned POST Content-Type is not valid.");
                return false;
            }

            request.Content!.Headers.ContentType = parsedContentType;
        }

        CopyPostField(fields, request, HeaderNames.ContentEncoding, HeaderNames.ContentEncoding);
        CopyPostField(fields, request, HeaderNames.ContentLanguage, HeaderNames.ContentLanguage);
        CopyPostField(fields, request, HeaderNames.ContentDisposition, HeaderNames.ContentDisposition);
        CopyPostField(fields, request, HeaderNames.CacheControl, HeaderNames.CacheControl);

        foreach (var field in fields)
        {
            if (!field.Key.StartsWith("x-amz-meta-", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrEmpty(field.Value))
            {
                continue;
            }

            var metadataName = field.Key.AsSpan("x-amz-meta-".Length);
            if (HeaderForwarding.IsInternalMetadataName(metadataName))
            {
                continue;
            }

            request.Headers.TryAddWithoutValidation("x-ms-meta-" + metadataName.ToString(), field.Value);
        }

        return true;
    }

    private static void CopyPostField(
        IReadOnlyDictionary<string, string> fields,
        HttpRequestMessage request,
        string fieldName,
        string targetHeader)
    {
        if (TryGetField(fields, fieldName, out var value) && !string.IsNullOrEmpty(value))
        {
            request.Content!.Headers.TryAddWithoutValidation(targetHeader, value);
        }
    }

    private static bool TryCreateMultipartReader(HttpRequest request, out MultipartReader reader)
    {
        reader = default!;
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var mediaType)
            || StringSegment.IsNullOrEmpty(mediaType.Boundary))
        {
            return false;
        }

        reader = new MultipartReader(mediaType.Boundary.Value!, request.Body);
        return true;
    }

    private static async Task<string> ReadSmallFormValueAsync(Stream body, CancellationToken cancellationToken)
    {
        using var memory = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var read = await body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (memory.Length + read > MinPresignedPostFormFieldBytes)
            {
                throw new InvalidDataException("Presigned POST form field exceeds the allowed size.");
            }

            memory.Write(buffer, 0, read);
        }

        return Encoding.UTF8.GetString(memory.GetBuffer(), 0, (int)memory.Length);
    }

    private static async Task<byte[]> ReadPostObjectFileAsync(Stream body, CancellationToken cancellationToken)
    {
        using var memory = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var read = await body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (memory.Length + read > MaxBufferedPresignedPostFileBytes)
            {
                throw new InvalidDataException("Presigned POST file exceeds the buffered upload size limit.");
            }

            memory.Write(buffer, 0, read);
        }
        return memory.ToArray();
    }

    private static string ResolvePostObjectKey(string rawKey, string? fileName)
    {
        var normalizedFileName = NormalizePostedFileName(fileName);
        if (string.IsNullOrEmpty(fileName)
            || rawKey.IndexOf("${filename}", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return rawKey;
        }

        return rawKey.Replace("${filename}", normalizedFileName, StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizePostedFileName(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName))
        {
            return fileName;
        }

        var slash = fileName.LastIndexOfAny(['/', '\\']);
        return slash >= 0 ? fileName[(slash + 1)..] : fileName;
    }

    private static bool TryGetField(IReadOnlyDictionary<string, string> fields, string name, out string value)
    {
        foreach (var field in fields)
        {
            if (string.Equals(field.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                value = field.Value;
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    private static string? ReadHeader(HttpResponseMessage response, string name)
    {
        if (response.Headers.TryGetValues(name, out var values))
        {
            foreach (var value in values)
            {
                if (!string.IsNullOrEmpty(value))
                {
                    return value;
                }
            }
        }

        if (response.Content?.Headers.TryGetValues(name, out var contentValues) == true)
        {
            foreach (var value in contentValues)
            {
                if (!string.IsNullOrEmpty(value))
                {
                    return value;
                }
            }
        }

        return null;
    }

    private static Uri BuildSourceObjectUri(BlobClient blob, string bucket, string key, string? versionId) =>
        string.IsNullOrEmpty(versionId)
            ? blob.BuildBlobUri(bucket, key)
            : blob.BuildBlobUri(bucket, key, "?versionid=" + Uri.EscapeDataString(versionId));

    private static async Task<(S3ErrorMapping.Mapping? Error, string? SourceIfMatch, string? PinnedVersionId)> TryEvaluateCopySourcePreconditionsAsync(
        HttpContext context,
        BlobClient blob,
        string sourceBucket,
        string sourceKey,
        string? sourceVersionId,
        S3Operation operation,
        CancellationToken ct)
    {
        var hasIfMatch = HasConcreteEtagPrecondition(context.Request, "x-amz-copy-source-if-match");
        var hasIfNoneMatch = HasConcreteEtagPrecondition(context.Request, "x-amz-copy-source-if-none-match");
        if (!hasIfMatch && !hasIfNoneMatch)
        {
            return (null, null, null);
        }

        using var head = await blob.HeadBlobAsync(sourceBucket, sourceKey, sourceVersionId, ct).ConfigureAwait(false);
        if (!head.IsSuccessStatusCode)
        {
            return (S3ErrorMapping.FromAzure(head, operation), null, null);
        }

        var sourceEtag = HeaderForwarding.TranslateAzureResponseEtagToS3(head);
        if (string.IsNullOrEmpty(sourceEtag))
        {
            return (new S3ErrorMapping.Mapping(StatusCodes.Status500InternalServerError,
                "InternalError",
                "aws2azure: Azure source blob HEAD did not return an ETag."), null, null);
        }

        var shortCircuit = HeaderForwarding.EvaluateEtagConditionals(
            context.Request.Headers,
            "x-amz-copy-source-if-match",
            "x-amz-copy-source-if-none-match",
            sourceEtag,
            isReadOperation: false);
        if (shortCircuit == StatusCodes.Status412PreconditionFailed)
        {
            return (new S3ErrorMapping.Mapping(StatusCodes.Status412PreconditionFailed,
                "PreconditionFailed",
                "At least one of the pre-conditions you specified did not hold."), null, null);
        }

        var pinnedVersionId = sourceVersionId ?? ReadHeader(head, "x-ms-version-id");
        if (hasIfNoneMatch && string.IsNullOrEmpty(pinnedVersionId))
        {
            return (new S3ErrorMapping.Mapping(StatusCodes.Status501NotImplemented,
                "NotImplemented",
                "aws2azure: x-amz-copy-source-if-none-match requires versionId for a safe copy."), null, null);
        }

        return (null, hasIfMatch && string.IsNullOrEmpty(pinnedVersionId) ? head.Headers.ETag?.Tag : null, pinnedVersionId);
    }

    private static void ApplyGetResponseOverrides(HttpRequest request, HttpResponse response)
    {
        ApplyOverride(request, response, "response-content-type", HeaderNames.ContentType);
        ApplyOverride(request, response, "response-content-disposition", HeaderNames.ContentDisposition);
        ApplyOverride(request, response, "response-content-encoding", HeaderNames.ContentEncoding);
        ApplyOverride(request, response, "response-content-language", HeaderNames.ContentLanguage);
        ApplyOverride(request, response, "response-cache-control", HeaderNames.CacheControl);
        ApplyOverride(request, response, "response-expires", HeaderNames.Expires);
    }

    private static void ApplyOverride(HttpRequest request, HttpResponse response, string queryKey, string headerName)
    {
        var value = request.Query[queryKey].ToString();
        if (!string.IsNullOrEmpty(value))
        {
            response.Headers[headerName] = value;
        }
    }

    private static Task EmitErrorAsync(HttpContext context, S3ErrorMapping.Mapping mapping, S3Operation op) =>
        op == S3Operation.HeadObject
            ? EmitHeadErrorAsync(context, mapping)
            : S3ErrorMapping.WriteAsync(context, mapping);

    private static Task EmitHeadErrorAsync(HttpContext context, S3ErrorMapping.Mapping mapping)
    {
        context.Response.StatusCode = mapping.StatusCode;
        HeaderForwarding.ApplyCommonS3ResponseHeaders(context.Response);
        context.Response.Headers["x-amz-error-code"] = mapping.Code;
        context.Response.ContentLength = 0;
        return Task.CompletedTask;
    }
}
