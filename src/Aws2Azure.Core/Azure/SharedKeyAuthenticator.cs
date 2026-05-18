using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace Aws2Azure.Core.Azure;

/// <summary>
/// Implements the Azure Storage "Shared Key" authentication scheme used by
/// Blob, Queue and Table services. The algorithm canonicalizes a fixed set of
/// standard HTTP headers plus all <c>x-ms-*</c> headers and the canonical
/// resource, signs the result with HMAC-SHA256 using the base64-decoded
/// account key, and emits an <c>Authorization: SharedKey account:signature</c>
/// header.
/// </summary>
public sealed class SharedKeyAuthenticator : IAzureAuthenticator
{
    private readonly string _accountName;
    private readonly byte[] _key;

    public SharedKeyAuthenticator(string accountName, string base64Key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountName);
        ArgumentException.ThrowIfNullOrWhiteSpace(base64Key);
        _accountName = accountName;
        _key = Convert.FromBase64String(base64Key);
    }

    public ValueTask AuthenticateAsync(HttpRequestMessage request, CancellationToken cancellationToken = default)
    {
        var signature = ComputeSignature(request);
        request.Headers.TryAddWithoutValidation("Authorization", $"SharedKey {_accountName}:{signature}");
        return ValueTask.CompletedTask;
    }

    public string ComputeSignature(HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.RequestUri is null)
        {
            throw new InvalidOperationException("RequestUri is required for SharedKey signing.");
        }

        EnsureDateHeader(request);

        var contentLength = request.Content?.Headers.ContentLength;
        var contentLengthStr = contentLength is null or 0 ? string.Empty : contentLength.Value.ToString(CultureInfo.InvariantCulture);

        var stringToSign =
            request.Method.Method + "\n" +
            GetHeader(request, "Content-Encoding") + "\n" +
            GetHeader(request, "Content-Language") + "\n" +
            contentLengthStr + "\n" +
            GetContentHeader(request, "Content-MD5") + "\n" +
            GetContentHeader(request, "Content-Type") + "\n" +
            "\n" + // Date — empty when x-ms-date is present (always set below)
            GetHeader(request, "If-Modified-Since") + "\n" +
            GetHeader(request, "If-Match") + "\n" +
            GetHeader(request, "If-None-Match") + "\n" +
            GetHeader(request, "If-Unmodified-Since") + "\n" +
            GetHeader(request, "Range") + "\n" +
            BuildCanonicalizedHeaders(request) +
            BuildCanonicalizedResource(request.RequestUri, _accountName);

        using var hmac = new HMACSHA256(_key);
        var bytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(stringToSign));
        return Convert.ToBase64String(bytes);
    }

    private static void EnsureDateHeader(HttpRequestMessage request)
    {
        if (!request.Headers.TryGetValues("x-ms-date", out _))
        {
            request.Headers.TryAddWithoutValidation("x-ms-date", DateTimeOffset.UtcNow.ToString("R", CultureInfo.InvariantCulture));
        }
        if (!request.Headers.TryGetValues("x-ms-version", out _))
        {
            request.Headers.TryAddWithoutValidation("x-ms-version", "2021-12-02");
        }
    }

    private static string GetHeader(HttpRequestMessage request, string name)
    {
        if (request.Headers.TryGetValues(name, out var values))
        {
            return string.Join(",", values);
        }
        if (request.Content?.Headers.TryGetValues(name, out var contentValues) == true)
        {
            return string.Join(",", contentValues);
        }
        return string.Empty;
    }

    private static string GetContentHeader(HttpRequestMessage request, string name)
    {
        if (request.Content?.Headers.TryGetValues(name, out var contentValues) == true)
        {
            return string.Join(",", contentValues);
        }
        return string.Empty;
    }

    internal static string BuildCanonicalizedHeaders(HttpRequestMessage request)
    {
        var msHeaders = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var header in request.Headers)
        {
            var name = header.Key.ToLowerInvariant();
            if (name.StartsWith("x-ms-", StringComparison.Ordinal))
            {
                msHeaders[name] = CollapseWhitespace(string.Join(",", header.Value));
            }
        }
        if (msHeaders.Count == 0)
        {
            return string.Empty;
        }
        var sb = new StringBuilder();
        foreach (var kvp in msHeaders)
        {
            sb.Append(kvp.Key).Append(':').Append(kvp.Value).Append('\n');
        }
        return sb.ToString();
    }

    internal static string BuildCanonicalizedResource(Uri uri, string accountName)
    {
        var sb = new StringBuilder();
        sb.Append('/').Append(accountName).Append(uri.AbsolutePath);

        var query = uri.Query;
        if (string.IsNullOrEmpty(query) || query == "?")
        {
            return sb.ToString();
        }

        var parsed = HttpUtility.ParseQueryString(query);
        var parameters = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (string? rawKey in parsed.Keys)
        {
            if (rawKey is null) continue;
            var key = rawKey.ToLowerInvariant();
            var values = parsed.GetValues(rawKey) ?? Array.Empty<string>();
            if (!parameters.TryGetValue(key, out var list))
            {
                list = new List<string>();
                parameters[key] = list;
            }
            foreach (var v in values)
            {
                list.Add(v);
            }
        }
        foreach (var kvp in parameters)
        {
            kvp.Value.Sort(StringComparer.Ordinal);
            sb.Append('\n').Append(kvp.Key).Append(':').Append(string.Join(",", kvp.Value));
        }
        return sb.ToString();
    }

    private static string CollapseWhitespace(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var sb = new StringBuilder(value.Length);
        var lastWasSpace = false;
        foreach (var ch in value.Trim())
        {
            if (ch == '\r' || ch == '\n')
            {
                if (!lastWasSpace)
                {
                    sb.Append(' ');
                    lastWasSpace = true;
                }
                continue;
            }
            if (char.IsWhiteSpace(ch))
            {
                if (!lastWasSpace)
                {
                    sb.Append(' ');
                    lastWasSpace = true;
                }
                continue;
            }
            sb.Append(ch);
            lastWasSpace = false;
        }
        return sb.ToString();
    }
}
