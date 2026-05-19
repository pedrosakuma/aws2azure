using System.Globalization;
using System.Text.RegularExpressions;
using Aws2Azure.Core.Azure;
using Aws2Azure.Core.Configuration;

namespace Aws2Azure.Modules.S3.Internal;

/// <summary>
/// Thin per-request wrapper that issues Blob-service REST calls used by the
/// S3 module. Owns the <see cref="SharedKeyAuthenticator"/> for the request's
/// resolved <see cref="BlobCredentials"/>. Reflection-free and AOT-safe.
/// </summary>
internal sealed partial class BlobClient
{
    // S3 bucket-name rules are stricter than Azure container rules; the
    // intersection that round-trips cleanly is 3–63 chars, lowercase, digits,
    // and hyphens, no leading/trailing hyphen, no double-hyphen. See gap doc.
    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{1,61}[a-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex ContainerNameRegex();

    private readonly AzureHttpClient _http;
    private readonly SharedKeyAuthenticator _auth;
    private readonly Uri _serviceEndpoint;

    public BlobClient(AzureHttpClient http, BlobCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(credentials);
        _http = http;
        _auth = new SharedKeyAuthenticator(credentials.AccountName, credentials.AccountKey);
        _serviceEndpoint = ResolveEndpoint(credentials);
    }

    public static bool IsValidContainerName(string name) =>
        !string.IsNullOrEmpty(name)
        && !name.Contains("--", StringComparison.Ordinal)
        && ContainerNameRegex().IsMatch(name);

    public Task<HttpResponseMessage> ListContainersAsync(CancellationToken cancellationToken) =>
        ListContainersAsync(marker: null, cancellationToken);

    public Task<HttpResponseMessage> ListContainersAsync(string? marker, CancellationToken cancellationToken)
    {
        var query = string.IsNullOrEmpty(marker)
            ? "?comp=list"
            : "?comp=list&marker=" + Uri.EscapeDataString(marker);
        var uri = new Uri(_serviceEndpoint, query);
        return SendAsync(HttpMethod.Get, uri, cancellationToken);
    }

    public Task<HttpResponseMessage> CreateContainerAsync(string container, CancellationToken cancellationToken)
    {
        var uri = new Uri(_serviceEndpoint, $"{container}?restype=container");
        return SendAsync(HttpMethod.Put, uri, cancellationToken);
    }

    public Task<HttpResponseMessage> DeleteContainerAsync(string container, CancellationToken cancellationToken)
    {
        var uri = new Uri(_serviceEndpoint, $"{container}?restype=container");
        return SendAsync(HttpMethod.Delete, uri, cancellationToken);
    }

    public Task<HttpResponseMessage> GetContainerPropertiesAsync(string container, CancellationToken cancellationToken)
    {
        var uri = new Uri(_serviceEndpoint, $"{container}?restype=container");
        return SendAsync(HttpMethod.Head, uri, cancellationToken);
    }

    /// <summary>
    /// Lists a single page of blobs in <paramref name="container"/>. All
    /// arguments are forwarded to Azure verbatim (already-validated /
    /// already-clamped by the caller). The returned <see cref="HttpResponseMessage"/>
    /// is the raw Azure response — the handler parses + reshapes into the
    /// S3 envelope (ListBucketResult / ListObjectsV2Result).
    /// </summary>
    public Task<HttpResponseMessage> ListBlobsAsync(
        string container,
        string? prefix,
        string? delimiter,
        string? marker,
        int? maxResults,
        CancellationToken cancellationToken)
    {
        var sb = new System.Text.StringBuilder(64);
        sb.Append(container).Append("?restype=container&comp=list");
        if (!string.IsNullOrEmpty(prefix))
        {
            sb.Append("&prefix=").Append(Uri.EscapeDataString(prefix));
        }
        if (!string.IsNullOrEmpty(delimiter))
        {
            sb.Append("&delimiter=").Append(Uri.EscapeDataString(delimiter));
        }
        if (!string.IsNullOrEmpty(marker))
        {
            sb.Append("&marker=").Append(Uri.EscapeDataString(marker));
        }
        if (maxResults is int max)
        {
            sb.Append("&maxresults=").Append(max.ToString(CultureInfo.InvariantCulture));
        }
        var uri = new Uri(_serviceEndpoint, sb.ToString());
        return SendAsync(HttpMethod.Get, uri, cancellationToken);
    }

    /// <summary>
    /// Builds the absolute Azure blob URI for <paramref name="container"/> /
    /// <paramref name="key"/>. The key is percent-encoded per Azure's URL rules
    /// while preserving '/'. The URI is constructed from a fully-qualified
    /// string so <see cref="Uri"/>'s relative-resolution / dot-segment
    /// normalization cannot rewrite the path (defence in depth — keys
    /// containing "." / ".." segments are also rejected by
    /// <see cref="S3ObjectKey.IsValid"/>).
    /// </summary>
    public Uri BuildBlobUri(string container, string key)
    {
        var endpoint = _serviceEndpoint.AbsoluteUri;
        if (endpoint.Length == 0 || endpoint[^1] != '/')
        {
            endpoint += "/";
        }
        return new Uri(endpoint + container + "/" + S3ObjectKey.EncodeForBlobUrl(key), UriKind.Absolute);
    }

    /// <summary>
    /// Authenticates an arbitrary blob-scoped <see cref="HttpRequestMessage"/>
    /// (already built with method/uri/headers/body by the caller) and sends
    /// it. Lets handlers stream request/response bodies directly to/from the
    /// network without an intermediate buffer.
    /// </summary>
    public async Task<HttpResponseMessage> SendBlobRequestAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.TryAddWithoutValidation("x-ms-date",
            DateTimeOffset.UtcNow.ToString("R", CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation("x-ms-version", "2021-12-02");
        await _auth.AuthenticateAsync(request, cancellationToken).ConfigureAwait(false);
        return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the absolute Azure container URI (no blob path). Used by
    /// handlers that need to issue container-scoped requests such as the
    /// HeadBucket-style probe used to short-circuit DeleteObjects when the
    /// destination bucket is missing.
    /// </summary>
    public Uri BuildContainerUri(string container)
    {
        var endpoint = _serviceEndpoint.AbsoluteUri;
        if (endpoint.Length == 0 || endpoint[^1] != '/')
        {
            endpoint += "/";
        }
        return new Uri(endpoint + container, UriKind.Absolute);
    }

    /// <summary>
    /// Returns the absolute URI for a sibling blob in the same Azure storage
    /// account. Used to build the <c>x-ms-copy-source</c> value for
    /// <see cref="ObjectHandlers"/>' CopyObject, which only supports copies
    /// inside the account the request was authenticated for.
    /// </summary>
    public Uri BuildAccountBlobUri(string container, string key) =>
        BuildBlobUri(container, key);

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, Uri uri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.TryAddWithoutValidation("x-ms-date",
            DateTimeOffset.UtcNow.ToString("R", CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation("x-ms-version", "2021-12-02");
        await _auth.AuthenticateAsync(request, cancellationToken).ConfigureAwait(false);
        return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
    }

    private static Uri ResolveEndpoint(BlobCredentials credentials)
    {
        if (!string.IsNullOrWhiteSpace(credentials.ServiceEndpoint))
        {
            var raw = credentials.ServiceEndpoint!.TrimEnd('/') + "/";
            return new Uri(raw, UriKind.Absolute);
        }
        return new Uri($"https://{credentials.AccountName}.blob.core.windows.net/", UriKind.Absolute);
    }
}
