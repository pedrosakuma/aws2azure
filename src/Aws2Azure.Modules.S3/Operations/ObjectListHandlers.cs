using System.Globalization;
using Aws2Azure.Core.Modules;
using Aws2Azure.Modules.S3.Errors;
using Aws2Azure.Modules.S3.Internal;
using Aws2Azure.Modules.S3.Xml;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.S3.Operations;

/// <summary>
/// Handlers for object-listing operations:
/// <see cref="S3Operation.ListObjectsV2"/> (modern, continuation-token based)
/// and <see cref="S3Operation.ListObjects"/> (legacy V1, marker based).
/// </summary>
internal static class ObjectListHandlers
{
    // S3 caps max-keys at 1000 (default 1000). Azure caps maxresults at 5000.
    // We honour the S3 contract: never return more than the requested max-keys
    // in a single response; paginate against Azure when its first page is
    // short of that cap.
    private const int DefaultMaxKeys = 1000;
    private const int MaxMaxKeys = 1000;
    private const int AzureMaxResults = 5000;
    private const string DefaultStorageClass = "STANDARD";

    public static async Task HandleAsync(
        HttpContext context,
        S3RouteResult route,
        BlobClient blob,
        CancellationToken cancellationToken)
    {
        var bucket = route.Bucket!;
        if (S3ErrorMapping.ClassifyLookupBucketName(bucket) is { } bucketError)
        {
            await S3ErrorMapping.WriteAsync(context, bucketError).ConfigureAwait(false);
            return;
        }

        if (route.Operation == S3Operation.ListObjectVersions)
        {
            await HandleListVersionsAsync(context, bucket, blob, cancellationToken).ConfigureAwait(false);
            return;
        }

        var query = context.Request.Query;
        var isV2 = route.Operation == S3Operation.ListObjectsV2;

        var prefix = StringOrNull(query, "prefix");
        var delimiter = StringOrNull(query, "delimiter");
        var encodingType = StringOrNull(query, "encoding-type");
        var encodeUrl = string.Equals(encodingType, "url", StringComparison.OrdinalIgnoreCase);

        if (!string.IsNullOrEmpty(encodingType) && !encodeUrl)
        {
            await S3ErrorMapping.WriteAsync(context, S3ErrorMapping.InvalidArgument(
                "Invalid Encoding Method specified in Request")).ConfigureAwait(false);
            return;
        }

        if (!TryParseMaxKeys(query, out var maxKeys, out var maxKeysError))
        {
            await S3ErrorMapping.WriteAsync(context, S3ErrorMapping.InvalidArgument(maxKeysError!)).ConfigureAwait(false);
            return;
        }

        string? azureStartMarker;
        string? requestContinuationToken = null;
        string? requestMarker = null;
        string? startAfter = null;

        if (isV2)
        {
            requestContinuationToken = StringOrNull(query, "continuation-token");
            startAfter = StringOrNull(query, "start-after");
            if (!string.IsNullOrEmpty(requestContinuationToken))
            {
                // Token-driven pagination wins over start-after on subsequent
                // pages, matching S3 docs ("StartAfter is bypassed for any
                // subsequent listing requests that use a continuation token").
                var decoded = ContinuationTokenCodec.TryDecode(requestContinuationToken);
                if (decoded is null)
                {
                    await S3ErrorMapping.WriteAsync(context, S3ErrorMapping.InvalidArgument(
                        "The continuation token provided is incorrect")).ConfigureAwait(false);
                    return;
                }
                azureStartMarker = decoded;
            }
            else
            {
                // Azure's marker is "list starts after this key" — same exclusive
                // semantics as S3 start-after, so we can pass it through.
                azureStartMarker = startAfter;
            }
        }
        else
        {
            requestMarker = StringOrNull(query, "marker");
            azureStartMarker = requestMarker;
        }

        var contents = new List<S3XmlWriter.ListedObject>(Math.Min(maxKeys, 64));
        var commonPrefixes = new List<string>();
        var seenPrefixes = new HashSet<string>(StringComparer.Ordinal);
        string? azureMarker = azureStartMarker;
        string? truncatedAzureMarker = null;
        var truncated = false;

        // Loop over Azure pages until we either fill the S3 max-keys budget
        // or exhaust the listing. Each iteration asks Azure for at most the
        // remaining budget (+ commonPrefixes already collected count toward
        // the budget per S3 docs).
        while (true)
        {
            var collected = contents.Count + commonPrefixes.Count;
            var remaining = maxKeys - collected;
            if (remaining <= 0)
            {
                // Hit the cap on the previous iteration; we already issued
                // the page request whose marker becomes NextContinuationToken.
                truncated = true;
                truncatedAzureMarker = azureMarker;
                break;
            }

            var azureMax = Math.Min(remaining, AzureMaxResults);
            using var response = await blob.ListBlobsAsync(
                bucket, prefix, delimiter, azureMarker, azureMax, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                await S3ErrorMapping.WriteAsync(context,
                    S3ErrorMapping.FromAzure(response, route.Operation)).ConfigureAwait(false);
                return;
            }

            var xml = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var page = AzureBlobXmlReader.ParseBlobListPage(xml, azureMax);

            // Merge entries up to the remaining budget. CommonPrefixes are
            // de-duplicated across Azure pages (Azure can re-emit the same
            // prefix on the next segment when blobs of that prefix span pages).
            for (var i = 0; i < page.Blobs.Count; i++)
            {
                if (contents.Count + commonPrefixes.Count >= maxKeys)
                {
                    truncated = true;
                    break;
                }
                var b = page.Blobs[i];
                contents.Add(new S3XmlWriter.ListedObject(
                    b.Name, b.LastModified, b.ETag, b.ContentLength, DefaultStorageClass));
            }
            if (!truncated)
            {
                for (var i = 0; i < page.BlobPrefixes.Count; i++)
                {
                    if (contents.Count + commonPrefixes.Count >= maxKeys)
                    {
                        truncated = true;
                        break;
                    }
                    var p = page.BlobPrefixes[i];
                    if (seenPrefixes.Add(p))
                    {
                        commonPrefixes.Add(p);
                    }
                }
            }

            if (truncated)
            {
                // Azure may still have more on the current page — use the
                // previous azureMarker so the next caller resumes from the
                // page we partially consumed. NOTE: if Azure returned more
                // than fits in our budget, the user-facing token rewinds to
                // re-fetch the same page (acceptable: S3 only promises that
                // the *first* page of the next call starts at-or-after the
                // last returned key, never that it's exactly contiguous).
                truncatedAzureMarker = azureMarker;
                break;
            }

            if (string.IsNullOrEmpty(page.NextMarker))
            {
                // Listing complete.
                break;
            }
            azureMarker = page.NextMarker;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/xml; charset=utf-8";

        if (isV2)
        {
            var nextToken = truncated && !string.IsNullOrEmpty(truncatedAzureMarker)
                ? ContinuationTokenCodec.Encode(truncatedAzureMarker!)
                : null;
            await S3XmlWriter.WriteListObjectsV2ResultAsync(
                context.Response.Body,
                bucket, prefix, delimiter, maxKeys,
                keyCount: contents.Count + commonPrefixes.Count,
                isTruncated: truncated && nextToken is not null,
                continuationToken: requestContinuationToken,
                nextContinuationToken: nextToken,
                startAfter: startAfter,
                encodeUrl: encodeUrl,
                contents: contents,
                commonPrefixes: commonPrefixes).ConfigureAwait(false);
        }
        else
        {
            // V1 NextMarker semantics: only meaningful when a delimiter is
            // set (S3 docs). The safe continuation value is the same marker
            // we'd hand back to Azure ourselves — anything else risks
            // landing the client behind already-returned keys when the
            // listing interleaves blobs and prefixes.
            string? nextMarker = null;
            if (truncated && !string.IsNullOrEmpty(delimiter) && !string.IsNullOrEmpty(truncatedAzureMarker))
            {
                nextMarker = truncatedAzureMarker;
            }
            await S3XmlWriter.WriteListBucketResultAsync(
                context.Response.Body,
                bucket, prefix, delimiter, maxKeys,
                isTruncated: truncated,
                marker: requestMarker,
                nextMarker: nextMarker,
                encodeUrl: encodeUrl,
                contents: contents,
                commonPrefixes: commonPrefixes).ConfigureAwait(false);
        }
    }

    // ListObjectVersions: single Azure page (versions listing) shaped into the
    // S3 ListVersionsResult. Pagination via key-marker → Azure marker; delete
    // markers are not modelled (no S3↔Azure delete-marker mapping).
    private static async Task HandleListVersionsAsync(
        HttpContext context, string bucket, BlobClient blob, CancellationToken cancellationToken)
    {
        var query = context.Request.Query;
        var prefix = StringOrNull(query, "prefix");
        var delimiter = StringOrNull(query, "delimiter");
        var encodingType = StringOrNull(query, "encoding-type");
        var encodeUrl = string.Equals(encodingType, "url", StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(encodingType) && !encodeUrl)
        {
            await S3ErrorMapping.WriteAsync(context, S3ErrorMapping.InvalidArgument(
                "Invalid Encoding Method specified in Request")).ConfigureAwait(false);
            return;
        }
        if (!TryParseMaxKeys(query, out var maxKeys, out var maxKeysError))
        {
            await S3ErrorMapping.WriteAsync(context, S3ErrorMapping.InvalidArgument(maxKeysError!)).ConfigureAwait(false);
            return;
        }
        var keyMarker = StringOrNull(query, "key-marker");

        var versions = new List<S3XmlWriter.ListedVersion>(Math.Min(maxKeys, 64));
        var commonPrefixes = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var truncated = false;
        string? nextKeyMarker = null;

        if (maxKeys > 0)
        {
            var azureMax = Math.Min(maxKeys, AzureMaxResults);
            using var response = await blob.ListBlobsAsync(
                bucket, prefix, delimiter, keyMarker, azureMax, includeVersions: true, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                await S3ErrorMapping.WriteAsync(context,
                    S3ErrorMapping.FromAzure(response, S3Operation.ListObjectVersions)).ConfigureAwait(false);
                return;
            }
            var xml = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var page = AzureBlobXmlReader.ParseBlobVersionListPage(xml, azureMax);

            foreach (var v in page.Versions)
            {
                if (versions.Count + commonPrefixes.Count >= maxKeys)
                {
                    truncated = true;
                    break;
                }
                versions.Add(new S3XmlWriter.ListedVersion(
                    v.Name, v.VersionId ?? string.Empty, v.IsCurrent,
                    v.LastModified, v.ETag ?? string.Empty, v.ContentLength, DefaultStorageClass));
            }
            if (!truncated)
            {
                foreach (var p in page.BlobPrefixes)
                {
                    if (versions.Count + commonPrefixes.Count >= maxKeys)
                    {
                        truncated = true;
                        break;
                    }
                    if (seen.Add(p)) { commonPrefixes.Add(p); }
                }
            }

            // Azure's opaque NextMarker is the only resumable continuation; we
            // never emit a NextVersionIdMarker, so resume is a page boundary.
            // Re-fetching the page marker may re-return listed versions but
            // never skips them. If our budget filled before the page ended,
            // fall back to the request marker (page partially consumed).
            if (!string.IsNullOrEmpty(page.NextMarker))
            {
                truncated = true;
                nextKeyMarker = page.NextMarker;
            }
            else if (truncated)
            {
                nextKeyMarker = keyMarker;
            }
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/xml; charset=utf-8";
        await S3XmlWriter.WriteListVersionsResultAsync(
            context.Response.Body, bucket, prefix, delimiter, maxKeys,
            isTruncated: truncated, keyMarker: keyMarker, nextKeyMarker: truncated ? nextKeyMarker : null,
            encodeUrl: encodeUrl, versions: versions, commonPrefixes: commonPrefixes).ConfigureAwait(false);
    }

    private static bool TryParseMaxKeys(IQueryCollection query, out int maxKeys, out string? error)
    {
        error = null;
        if (!query.TryGetValue("max-keys", out var values) || values.Count == 0 || string.IsNullOrEmpty(values[0]))
        {
            maxKeys = DefaultMaxKeys;
            return true;
        }
        if (!int.TryParse(values[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed < 0)
        {
            maxKeys = 0;
            error = "Argument max-keys must be an integer between 0 and 2147483647";
            return false;
        }
        maxKeys = Math.Min(parsed, MaxMaxKeys);
        if (maxKeys == 0)
        {
            // S3 treats max-keys=0 as a valid request that returns zero keys;
            // honour that without round-tripping to Azure.
            return true;
        }
        return true;
    }

    private static string? StringOrNull(IQueryCollection query, string key)
    {
        if (!query.TryGetValue(key, out var values) || values.Count == 0)
        {
            return null;
        }
        var v = values[0];
        return string.IsNullOrEmpty(v) ? null : v;
    }
}
