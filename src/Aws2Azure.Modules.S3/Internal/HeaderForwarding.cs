using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace Aws2Azure.Modules.S3.Internal;

/// <summary>
/// Header-translation helpers between S3 (client ⇄ proxy) and Azure Blob
/// (proxy ⇄ Azure). Hand-written so the proxy stays SDK-free.
/// </summary>
/// <remarks>
/// AOT-safe (no reflection, no LINQ over header collections). Only the small
/// allowlist of headers that affect object semantics is forwarded — everything
/// else is dropped so a stray hop-by-hop header (e.g. <c>Connection</c>) or a
/// client-side fingerprinting header can never leak into the Azure call or
/// back into the S3 response.
/// </remarks>
internal static class HeaderForwarding
{
    // S3 request → Azure PUT/GET/HEAD/DELETE request.
    private static readonly string[] StandardRequestHeaders =
    {
        HeaderNames.ContentType,
        HeaderNames.ContentEncoding,
        HeaderNames.ContentLanguage,
        HeaderNames.ContentDisposition,
        HeaderNames.CacheControl,
        HeaderNames.ContentMD5,
        HeaderNames.IfMatch,
        HeaderNames.IfNoneMatch,
        HeaderNames.IfModifiedSince,
        HeaderNames.IfUnmodifiedSince,
        HeaderNames.Range,
    };

    // Azure response → S3 response.
    private static readonly string[] StandardResponseHeaders =
    {
        HeaderNames.ETag,
        HeaderNames.LastModified,
        HeaderNames.ContentType,
        HeaderNames.ContentEncoding,
        HeaderNames.ContentLanguage,
        HeaderNames.ContentDisposition,
        HeaderNames.CacheControl,
        HeaderNames.ContentMD5,
        HeaderNames.AcceptRanges,
        HeaderNames.ContentRange,
    };

    /// <summary>
    /// Copies the standard request headers plus <c>x-amz-meta-*</c>
    /// (renamed to <c>x-ms-meta-*</c>) from <paramref name="source"/> onto
    /// <paramref name="target"/>. Content-related headers go onto the request
    /// content when present; the rest go onto the request itself.
    /// </summary>
    public static void CopyToAzureRequest(HttpRequest source, HttpRequestMessage target)
    {
        foreach (var header in StandardRequestHeaders)
        {
            if (!source.Headers.TryGetValue(header, out var values))
            {
                continue;
            }
            foreach (var value in values)
            {
                if (string.IsNullOrEmpty(value))
                {
                    continue;
                }
                if (IsContentHeader(header))
                {
                    target.Content?.Headers.TryAddWithoutValidation(header, value);
                }
                else
                {
                    target.Headers.TryAddWithoutValidation(header, value);
                }
            }
        }

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
    /// Copies allow-listed Azure response headers onto the outgoing S3
    /// response. Azure metadata (<c>x-ms-meta-*</c>) is renamed to
    /// <c>x-amz-meta-*</c>. The Azure request id is exposed as the AWS
    /// extended request id for diagnostic continuity.
    /// </summary>
    /// <remarks>
    /// ETag translation: Azure returns ETags in opaque <c>"0x8DC..."</c>
    /// form, which the AWS SDK then tries to hex-parse as an MD5 (because
    /// classic S3 ETags ARE the object's lowercase hex MD5). That parse
    /// throws <see cref="ArgumentOutOfRangeException"/> on every GET, so
    /// we replace the ETag with an S3-shaped 32-char lowercase hex string:
    /// either the hex form of the Azure <c>Content-MD5</c> (when present —
    /// matches the real object MD5 byte-for-byte) or a deterministic MD5
    /// derived from the Azure ETag bytes (stable across reads, but NOT
    /// equal to the object MD5; matches the pattern already used by
    /// multipart synthetic part ETags). Clients using strong-ETag
    /// conditional requests (<c>If-Match</c> / <c>If-None-Match</c>) will
    /// still round-trip through the proxy because both sides see the
    /// translated value.
    /// </remarks>
    public static void CopyFromAzureResponse(HttpResponseMessage source, HttpResponse target)
    {
        foreach (var header in StandardResponseHeaders)
        {
            if (header == HeaderNames.ETag)
            {
                continue;
            }
            if (TryGetHeader(source, header, out var values))
            {
                target.Headers[header] = values;
            }
        }

        foreach (var kv in source.Headers)
        {
            if (kv.Key.StartsWith("x-ms-meta-", StringComparison.OrdinalIgnoreCase))
            {
                var s3Name = "x-amz-meta-" + kv.Key.AsSpan("x-ms-meta-".Length).ToString();
                foreach (var value in kv.Value)
                {
                    target.Headers.Append(s3Name, value);
                }
            }
        }

        string? contentMd5Base64 = null;
        if (source.Content is { } content)
        {
            foreach (var header in StandardResponseHeaders)
            {
                if (header == HeaderNames.ETag)
                {
                    continue;
                }
                if (content.Headers.TryGetValues(header, out var values))
                {
                    target.Headers[header] = string.Join(",", values);
                }
            }
            if (content.Headers.TryGetValues(HeaderNames.ContentMD5, out var md5))
            {
                foreach (var v in md5)
                {
                    if (!string.IsNullOrEmpty(v)) { contentMd5Base64 = v; break; }
                }
            }
            // Content-Length lives on HttpContent.Headers only; preserve it so
            // HEAD callers learn the blob size and GET bodies aren't chunked.
            if (content.Headers.ContentLength is { } len)
            {
                target.ContentLength = len;
            }
        }

        if (TryGetEtag(source, out var azureEtag))
        {
            target.Headers[HeaderNames.ETag] = "\"" + TranslateAzureEtagToS3(azureEtag, contentMd5Base64) + "\"";
        }

        if (source.Headers.TryGetValues("x-ms-request-id", out var azureReqId))
        {
            foreach (var v in azureReqId)
            {
                target.Headers["x-amz-id-2"] = v;
                break;
            }
        }
    }

    private static bool TryGetEtag(HttpResponseMessage source, out string value)
    {
        if (source.Headers.TryGetValues(HeaderNames.ETag, out var hdr))
        {
            foreach (var v in hdr)
            {
                if (!string.IsNullOrEmpty(v)) { value = v; return true; }
            }
        }
        if (source.Content is { } c && c.Headers.TryGetValues(HeaderNames.ETag, out var chdr))
        {
            foreach (var v in chdr)
            {
                if (!string.IsNullOrEmpty(v)) { value = v; return true; }
            }
        }
        value = string.Empty;
        return false;
    }

    /// <summary>
    /// Converts an Azure-style ETag (opaque, often <c>"0x..."</c>) into an
    /// S3-shaped 32-char lowercase hex string. Prefers the real MD5 when
    /// Azure surfaces <c>Content-MD5</c>; falls back to a deterministic
    /// MD5 of the ETag bytes so the value is stable across reads.
    /// </summary>
    internal static string TranslateAzureEtagToS3(string azureEtag, string? contentMd5Base64)
    {
        if (!string.IsNullOrEmpty(contentMd5Base64))
        {
            try
            {
                var bytes = Convert.FromBase64String(contentMd5Base64);
                if (bytes.Length == 16)
                {
                    return Convert.ToHexString(bytes).ToLowerInvariant();
                }
            }
            catch (FormatException) { /* fall through to synthetic path */ }
        }
        // Strip surrounding quotes so the synthetic value is stable across
        // whichever HttpClient layer happened to surface the header.
        var trimmed = azureEtag.AsSpan().Trim().Trim('"');
        var hash = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.ASCII.GetBytes(trimmed.ToString()));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static bool IsContentHeader(string name) =>
        name.Equals(HeaderNames.ContentType, StringComparison.OrdinalIgnoreCase)
        || name.Equals(HeaderNames.ContentEncoding, StringComparison.OrdinalIgnoreCase)
        || name.Equals(HeaderNames.ContentLanguage, StringComparison.OrdinalIgnoreCase)
        || name.Equals(HeaderNames.ContentDisposition, StringComparison.OrdinalIgnoreCase)
        || name.Equals(HeaderNames.ContentMD5, StringComparison.OrdinalIgnoreCase)
        || name.Equals(HeaderNames.CacheControl, StringComparison.OrdinalIgnoreCase);

    private static bool TryGetHeader(HttpResponseMessage message, string header, out string[] values)
    {
        if (message.Headers.TryGetValues(header, out var hdr))
        {
            values = System.Linq.Enumerable.ToArray(hdr);
            return values.Length > 0;
        }
        values = Array.Empty<string>();
        return false;
    }
}
