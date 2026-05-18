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
    public static void CopyFromAzureResponse(HttpResponseMessage source, HttpResponse target)
    {
        foreach (var header in StandardResponseHeaders)
        {
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

        if (source.Content is { } content)
        {
            foreach (var header in StandardResponseHeaders)
            {
                if (content.Headers.TryGetValues(header, out var values))
                {
                    target.Headers[header] = string.Join(",", values);
                }
            }
            // Content-Length lives on HttpContent.Headers only; preserve it so
            // HEAD callers learn the blob size and GET bodies aren't chunked.
            if (content.Headers.ContentLength is { } len)
            {
                target.ContentLength = len;
            }
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
