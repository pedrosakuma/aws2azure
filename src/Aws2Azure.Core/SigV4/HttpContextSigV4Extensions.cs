using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Core.SigV4;

/// <summary>
/// Adapters that translate an ASP.NET Core <see cref="HttpContext"/> into the
/// <see cref="SigV4Request"/> shape consumed by <see cref="SigV4Validator"/>.
/// </summary>
public static class HttpContextSigV4Extensions
{
    /// <summary>
    /// Build a <see cref="SigV4Request"/> from the current HTTP context. The
    /// caller MUST supply the payload hash (precomputed or one of the SigV4
    /// sentinels: <c>UNSIGNED-PAYLOAD</c>, <c>STREAMING-AWS4-HMAC-SHA256-PAYLOAD</c>).
    /// </summary>
    public static SigV4Request BuildSigV4Request(this HttpContext context, string payloadHash, bool s3PathStyle)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(payloadHash);

        var request = context.Request;

        // CORS preflight: the canonical method must be the one the browser is
        // about to invoke, exposed via the Access-Control-Request-Method header.
        var method = request.Method;
        if (string.Equals(method, "OPTIONS", StringComparison.OrdinalIgnoreCase)
            && request.Headers.TryGetValue("Access-Control-Request-Method", out var preflightMethod)
            && preflightMethod.Count > 0
            && !string.IsNullOrEmpty(preflightMethod[0]))
        {
            method = preflightMethod[0]!;
        }

        var headers = new List<KeyValuePair<string, string>>(request.Headers.Count);
        foreach (var kv in request.Headers)
        {
            foreach (var value in kv.Value)
            {
                if (value is not null)
                {
                    headers.Add(new KeyValuePair<string, string>(kv.Key, value));
                }
            }
        }

        // Ensure 'host' header is present (Kestrel exposes it via request.Host).
        var hasHost = false;
        for (var i = 0; i < headers.Count; i++)
        {
            if (string.Equals(headers[i].Key, SigV4Constants.HostHeader, StringComparison.OrdinalIgnoreCase))
            {
                hasHost = true;
                break;
            }
        }
        if (!hasHost && request.Host.HasValue)
        {
            headers.Add(new KeyValuePair<string, string>(SigV4Constants.HostHeader, request.Host.Value!));
        }

        return new SigV4Request
        {
            HttpMethod = method,
            RawPath = request.Path.HasValue ? request.Path.Value! : "/",
            RawQueryString = request.QueryString.HasValue ? request.QueryString.Value! : string.Empty,
            Headers = headers,
            PayloadHash = payloadHash,
            S3PathStyle = s3PathStyle,
        };
    }
}
