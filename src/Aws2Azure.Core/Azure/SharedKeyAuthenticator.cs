using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
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
    // HMAC-SHA256 output (32 bytes) base64-encodes to 44 chars; 64 is a safe ceiling.
    private const int HmacSha256ByteLength = 32;
    private const int HmacSha256Base64CharLength = 64;

    // Typical StringToSign payloads for blob ops stay well under 1 KiB; rent slightly above to skip first growth.
    private const int InitialBufferCapacity = 1024;

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

        var buffer = new PooledByteBuffer(InitialBufferCapacity);
        try
        {
            WriteStringToSign(ref buffer, request, _accountName);

            Span<byte> hash = stackalloc byte[HmacSha256ByteLength];
            var written = HMACSHA256.HashData(_key, buffer.WrittenSpan, hash);
            Debug.Assert(written == HmacSha256ByteLength);

            Span<char> base64 = stackalloc char[HmacSha256Base64CharLength];
            if (!Convert.TryToBase64Chars(hash, base64, out var charsWritten))
            {
                // Unreachable: 44 chars fit in 64.
                throw new InvalidOperationException("Base64 encoding of HMAC failed.");
            }
            return new string(base64[..charsWritten]);
        }
        finally
        {
            buffer.Dispose();
        }
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

    private static void WriteStringToSign(ref PooledByteBuffer buffer, HttpRequestMessage request, string accountName)
    {
        buffer.AppendUtf8(request.Method.Method);
        buffer.AppendByte((byte)'\n');
        AppendHeaderValue(ref buffer, request, "Content-Encoding");
        AppendHeaderValue(ref buffer, request, "Content-Language");

        var contentLength = request.Content?.Headers.ContentLength;
        if (contentLength is not null && contentLength.Value != 0)
        {
            // long.MaxValue = 19 chars; 24 leaves headroom.
            Span<char> scratch = stackalloc char[24];
            if (contentLength.Value.TryFormat(scratch, out var written, provider: CultureInfo.InvariantCulture))
            {
                buffer.AppendUtf8(scratch[..written]);
            }
            else
            {
                buffer.AppendUtf8(contentLength.Value.ToString(CultureInfo.InvariantCulture));
            }
        }
        buffer.AppendByte((byte)'\n');

        AppendContentHeaderValue(ref buffer, request, "Content-MD5");
        AppendContentHeaderValue(ref buffer, request, "Content-Type");
        // Date — empty when x-ms-date is present (always set by EnsureDateHeader).
        buffer.AppendByte((byte)'\n');
        AppendHeaderValue(ref buffer, request, "If-Modified-Since");
        AppendHeaderValue(ref buffer, request, "If-Match");
        AppendHeaderValue(ref buffer, request, "If-None-Match");
        AppendHeaderValue(ref buffer, request, "If-Unmodified-Since");
        AppendHeaderValue(ref buffer, request, "Range");

        WriteCanonicalizedHeaders(ref buffer, request);
        WriteCanonicalizedResource(ref buffer, request.RequestUri!, accountName);
    }

    private static void AppendHeaderValue(ref PooledByteBuffer buffer, HttpRequestMessage request, string name)
    {
        if (request.Headers.TryGetValues(name, out var values))
        {
            AppendJoinedValues(ref buffer, values);
        }
        else if (request.Content?.Headers.TryGetValues(name, out var contentValues) == true)
        {
            AppendJoinedValues(ref buffer, contentValues);
        }
        buffer.AppendByte((byte)'\n');
    }

    private static void AppendContentHeaderValue(ref PooledByteBuffer buffer, HttpRequestMessage request, string name)
    {
        if (request.Content?.Headers.TryGetValues(name, out var contentValues) == true)
        {
            AppendJoinedValues(ref buffer, contentValues);
        }
        buffer.AppendByte((byte)'\n');
    }

    private static void AppendJoinedValues(ref PooledByteBuffer buffer, IEnumerable<string> values)
    {
        var first = true;
        foreach (var value in values)
        {
            if (!first) buffer.AppendByte((byte)',');
            first = false;
            if (!string.IsNullOrEmpty(value)) buffer.AppendUtf8(value);
        }
    }

    internal static string BuildCanonicalizedHeaders(HttpRequestMessage request)
    {
        var buffer = new PooledByteBuffer(256);
        try
        {
            WriteCanonicalizedHeaders(ref buffer, request);
            return Encoding.UTF8.GetString(buffer.WrittenSpan);
        }
        finally
        {
            buffer.Dispose();
        }
    }

    internal static string BuildCanonicalizedResource(Uri uri, string accountName)
    {
        var buffer = new PooledByteBuffer(128);
        try
        {
            WriteCanonicalizedResource(ref buffer, uri, accountName);
            return Encoding.UTF8.GetString(buffer.WrittenSpan);
        }
        finally
        {
            buffer.Dispose();
        }
    }

    private static void WriteCanonicalizedHeaders(ref PooledByteBuffer buffer, HttpRequestMessage request)
    {
        SortedDictionary<string, string>? msHeaders = null;
        foreach (var header in request.Headers)
        {
            if (!StartsWithXmsPrefix(header.Key)) continue;
            msHeaders ??= new SortedDictionary<string, string>(StringComparer.Ordinal);
            msHeaders[header.Key.ToLowerInvariant()] = CollapseWhitespace(JoinValues(header.Value));
        }
        if (msHeaders is null) return;

        foreach (var kvp in msHeaders)
        {
            buffer.AppendUtf8(kvp.Key);
            buffer.AppendByte((byte)':');
            buffer.AppendUtf8(kvp.Value);
            buffer.AppendByte((byte)'\n');
        }
    }

    private static void WriteCanonicalizedResource(ref PooledByteBuffer buffer, Uri uri, string accountName)
    {
        buffer.AppendByte((byte)'/');
        buffer.AppendUtf8(accountName);
        buffer.AppendUtf8(uri.AbsolutePath);

        var query = uri.Query;
        if (string.IsNullOrEmpty(query) || query == "?") return;

        var parsed = HttpUtility.ParseQueryString(query);
        SortedDictionary<string, List<string>>? parameters = null;
        foreach (string? rawKey in parsed.Keys)
        {
            if (rawKey is null) continue;
            var key = rawKey.ToLowerInvariant();
            parameters ??= new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
            if (!parameters.TryGetValue(key, out var list))
            {
                list = new List<string>();
                parameters[key] = list;
            }
            var values = parsed.GetValues(rawKey);
            if (values is null) continue;
            foreach (var v in values) list.Add(v);
        }
        if (parameters is null) return;

        foreach (var kvp in parameters)
        {
            kvp.Value.Sort(StringComparer.Ordinal);
            buffer.AppendByte((byte)'\n');
            buffer.AppendUtf8(kvp.Key);
            buffer.AppendByte((byte)':');
            var first = true;
            foreach (var v in kvp.Value)
            {
                if (!first) buffer.AppendByte((byte)',');
                first = false;
                if (!string.IsNullOrEmpty(v)) buffer.AppendUtf8(v);
            }
        }
    }

    private static bool StartsWithXmsPrefix(string headerName)
    {
        if (headerName.Length < 5) return false;
        // Case-insensitive compare to "x-ms-" without allocating a lowercased copy.
        return (headerName[0] | 0x20) == 'x'
            && headerName[1] == '-'
            && (headerName[2] | 0x20) == 'm'
            && (headerName[3] | 0x20) == 's'
            && headerName[4] == '-';
    }

    private static string JoinValues(IEnumerable<string> values)
    {
        string? single = null;
        List<string>? all = null;
        foreach (var v in values)
        {
            if (all is not null)
            {
                all.Add(v);
            }
            else if (single is null)
            {
                single = v;
            }
            else
            {
                all = new List<string>(4) { single, v };
                single = null;
            }
        }
        if (all is not null) return string.Join(',', all);
        return single ?? string.Empty;
    }

    internal static string CollapseWhitespace(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        if (!NeedsCollapsing(value)) return value;

        var sb = new StringBuilder(value.Length);
        var lastWasSpace = false;
        foreach (var ch in value.AsSpan().Trim())
        {
            if (ch == '\r' || ch == '\n' || char.IsWhiteSpace(ch))
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

    private static bool NeedsCollapsing(string value)
    {
        if (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1])) return true;
        var lastWasSpace = false;
        foreach (var ch in value)
        {
            if (ch == '\r' || ch == '\n' || ch == '\t') return true;
            if (ch == ' ')
            {
                if (lastWasSpace) return true;
                lastWasSpace = true;
            }
            else
            {
                if (char.IsWhiteSpace(ch)) return true; // other unicode whitespace
                lastWasSpace = false;
            }
        }
        return false;
    }

    /// <summary>
    /// Pooled, growable, non-allocating byte buffer scoped to a single signing operation.
    /// Backing array is returned to <see cref="ArrayPool{T}.Shared"/> on <see cref="Dispose"/>.
    /// </summary>
    private ref struct PooledByteBuffer
    {
        private byte[] _array;
        private int _written;

        public PooledByteBuffer(int initialCapacity)
        {
            _array = ArrayPool<byte>.Shared.Rent(initialCapacity);
            _written = 0;
        }

        public ReadOnlySpan<byte> WrittenSpan => _array.AsSpan(0, _written);

        public void AppendByte(byte b)
        {
            EnsureCapacity(1);
            _array[_written++] = b;
        }

        public void AppendUtf8(string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            AppendUtf8(value.AsSpan());
        }

        public void AppendUtf8(scoped ReadOnlySpan<char> chars)
        {
            if (chars.IsEmpty) return;
            var maxBytes = Encoding.UTF8.GetMaxByteCount(chars.Length);
            EnsureCapacity(maxBytes);
            var written = Encoding.UTF8.GetBytes(chars, _array.AsSpan(_written));
            _written += written;
        }

        private void EnsureCapacity(int additional)
        {
            var required = _written + additional;
            if (required <= _array.Length) return;
            var newSize = Math.Max(_array.Length * 2, required);
            var newArr = ArrayPool<byte>.Shared.Rent(newSize);
            _array.AsSpan(0, _written).CopyTo(newArr);
            ArrayPool<byte>.Shared.Return(_array);
            _array = newArr;
        }

        public void Dispose()
        {
            if (_array is not null)
            {
                ArrayPool<byte>.Shared.Return(_array);
                _array = null!;
            }
        }
    }
}
