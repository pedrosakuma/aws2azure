using System.Globalization;
using System.Text.RegularExpressions;
using Aws2Azure.Core.Azure;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Core.Observability;
using Aws2Azure.Modules.S3.Errors;

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
    private readonly string _endpointWithSlash;
    private readonly string _accountName;
    private readonly byte[] _accountKeyBytes;

    /// <summary>Azure Storage REST API version sent on every blob request
    /// (matches the version used by <see cref="SendBlobRequestAsync"/> and
    /// the private <c>SendAsync</c>).</summary>
    public const string XmsVersion = "2021-12-02";

    public BlobClient(AzureHttpClient http, BlobCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(credentials);
        _http = http;
        _auth = new SharedKeyAuthenticator(credentials.AccountName, credentials.AccountKey);
        _serviceEndpoint = ResolveEndpoint(credentials);
        // _serviceEndpoint is always normalised to end with '/' by
        // ResolveEndpoint (either via TrimEnd('/') + "/" for an override
        // or directly in the cloud format). Cache the rendered string so
        // BuildBlobUri / BuildContainerUri can compose paths without
        // re-checking the trailing slash on every call.
        _endpointWithSlash = _serviceEndpoint.AbsoluteUri;
        _accountName = credentials.AccountName;
        _accountKeyBytes = Convert.FromBase64String(credentials.AccountKey);
    }

    /// <summary>
    /// Account name used to scope multipart UploadId tokens. Required for
    /// the HMAC binding so a token issued for account A cannot be replayed
    /// against account B.
    /// </summary>
    internal string AccountName => _accountName;

    /// <summary>
    /// Raw Azure account-key bytes (already base64-decoded). Used as the
    /// HMAC key for stateless multipart UploadId tokens; never logged or
    /// transmitted in cleartext.
    /// </summary>
    internal ReadOnlySpan<byte> AccountKeyBytes => _accountKeyBytes;

    /// <summary>
    /// Builds a short-lived read-only service SAS URL for a sibling blob in
    /// the same Azure storage account. Used as the <c>x-ms-copy-source</c>
    /// value for <c>UploadPartCopy</c> (<c>Put Block From URL</c>); the
    /// destination call has the proxy's regular SharedKey credentials and
    /// the SAS lets Azure dereference the source without a separate trust
    /// relationship. Azurite supports SAS but does not currently honour
    /// <c>x-ms-copy-source-authorization: SharedKey</c>, which is why this
    /// path uses SAS even in same-account scenarios.
    /// </summary>
    public Uri BuildSourceReadSasUri(string container, string key, TimeSpan validFor)
    {
        var baseUri = BuildBlobUri(container, key);

        var expiry = DateTimeOffset.UtcNow.Add(validFor).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        const string version = XmsVersion;
        const string permissions = "r";
        const string resource = "b";

        // The signed canonicalized resource is `/blob/{account}/{container}/{key}` —
        // independent of any account-name path segment present in the
        // service endpoint (e.g. Azurite's `/devstoreaccount1`).
        var canonicalResource = "/blob/" + _accountName + "/" + container + "/" + key;

        // String-to-sign layout for service SAS at x-ms-version=2020-12-06+
        // (sv) — 16 fields separated by '\n'. Unused fields are empty.
        var stringToSign = string.Join('\n', new[]
        {
            permissions,
            string.Empty,        // signedstart
            expiry,              // signedexpiry
            canonicalResource,   // canonicalizedresource
            string.Empty,        // signedidentifier
            string.Empty,        // signedIP
            string.Empty,        // signedProtocol
            version,             // signedversion
            resource,            // signedResource
            string.Empty,        // signedSnapshotTime
            string.Empty,        // signedEncryptionScope
            string.Empty,        // rscc
            string.Empty,        // rscd
            string.Empty,        // rsce
            string.Empty,        // rscl
            string.Empty,        // rsct
        });

        using var hmac = new System.Security.Cryptography.HMACSHA256(_accountKeyBytes);
        var sig = Convert.ToBase64String(hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(stringToSign)));

        var query = "?sv=" + Uri.EscapeDataString(version)
            + "&se=" + Uri.EscapeDataString(expiry)
            + "&sr=" + resource
            + "&sp=" + permissions
            + "&sig=" + Uri.EscapeDataString(sig);
        return new Uri(baseUri.AbsoluteUri + query, UriKind.Absolute);
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

    public async Task<S3ErrorMapping.Mapping?> CheckContainerExistsAsync(
        string container,
        S3Operation operation,
        CancellationToken cancellationToken)
    {
        using var response = await GetContainerPropertiesAsync(container, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            return null;
        }

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return new S3ErrorMapping.Mapping(404, "NoSuchBucket", "The specified bucket does not exist.");
        }

        return S3ErrorMapping.FromAzure(response, operation);
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
        CancellationToken cancellationToken) =>
        ListBlobsAsync(container, prefix, delimiter, marker, maxResults, includeVersions: false, cancellationToken);

    public Task<HttpResponseMessage> ListBlobsAsync(
        string container,
        string? prefix,
        string? delimiter,
        string? marker,
        int? maxResults,
        bool includeVersions,
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
        if (includeVersions)
        {
            sb.Append("&include=versions");
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
        var encoded = S3ObjectKey.EncodeForBlobUrl(key);
        // Compose "<endpointWithSlash><container>/<encodedKey>" in one
        // allocation; the prior implementation did three string concats
        // (`endpoint += "/"`, then two `+`) plus a fresh Uri parse on
        // the result — 17% of GetObject CPU and ~10 MB / 30s of String
        // allocations on the GetObject perf scenario.
        var path = string.Create(
            _endpointWithSlash.Length + container.Length + 1 + encoded.Length,
            (_endpointWithSlash, container, encoded),
            static (span, state) =>
            {
                var (endpoint, container, encoded) = state;
                endpoint.AsSpan().CopyTo(span);
                container.AsSpan().CopyTo(span[endpoint.Length..]);
                span[endpoint.Length + container.Length] = '/';
                encoded.AsSpan().CopyTo(span[(endpoint.Length + container.Length + 1)..]);
            });
        return new Uri(path, UriKind.Absolute);
    }

    /// <summary>
    /// Builds a blob URI with an additional pre-formatted query string
    /// (already starting with <c>?</c>, components already escaped). Used
    /// by the multipart handlers to target Azure subresources such as
    /// <c>?comp=block&amp;blockid=…</c> or <c>?comp=blocklist</c>.
    /// </summary>
    public Uri BuildBlobUri(string container, string key, string queryString)
    {
        var baseUri = BuildBlobUri(container, key).AbsoluteUri;
        return new Uri(baseUri + queryString, UriKind.Absolute);
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
        return await BackendTimingContext.TimeAsync(
            () => _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
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
        return new Uri(_endpointWithSlash + container, UriKind.Absolute);
    }

    /// <summary>
    /// Issues Azure <c>Get Block List</c> for <paramref name="key"/> with
    /// the requested <paramref name="blockListType"/> (<c>committed</c>,
    /// <c>uncommitted</c>, or <c>all</c>). Used by ListParts to enumerate
    /// the uncommitted blocks belonging to a multipart upload.
    /// </summary>
    public Task<HttpResponseMessage> GetBlockListAsync(
        string container, string key, string blockListType, CancellationToken cancellationToken)
    {
        var uri = BuildBlobUri(container, key,
            "?comp=blocklist&blocklisttype=" + Uri.EscapeDataString(blockListType));
        return SendAsync(HttpMethod.Get, uri, cancellationToken);
    }

    /// <summary>
    /// Returns the absolute URI for a sibling blob in the same Azure storage
    /// account. Used to build the <c>x-ms-copy-source</c> value for
    /// <see cref="ObjectHandlers"/>' CopyObject, which only supports copies
    /// inside the account the request was authenticated for.
    /// </summary>
    public Uri BuildAccountBlobUri(string container, string key) =>
        BuildBlobUri(container, key);

    /// <summary>
    /// Issues <c>GET {blob}?comp=tags</c> to retrieve Azure Blob Index Tags
    /// for an object. The returned XML uses Azure's <c>&lt;Tags&gt;</c> root
    /// (no namespace); the S3 module rewraps it as <c>&lt;Tagging&gt;</c>.
    /// </summary>
    public Task<HttpResponseMessage> GetBlobTagsAsync(string container, string key, CancellationToken cancellationToken)
    {
        var uri = BuildBlobUri(container, key, "?comp=tags");
        return SendAsync(HttpMethod.Get, uri, cancellationToken);
    }

    /// <summary>
    /// Issues <c>PUT {blob}?comp=tags</c> to replace the Azure Blob Index
    /// Tags for an object. <paramref name="azureTagsXml"/> must already be
    /// in Azure's wire format (<c>&lt;Tags&gt;&lt;TagSet&gt;…</c>).
    /// </summary>
    public Task<HttpResponseMessage> PutBlobTagsAsync(
        string container, string key, byte[] azureTagsXml, CancellationToken cancellationToken)
    {
        var uri = BuildBlobUri(container, key, "?comp=tags");
        var request = new HttpRequestMessage(HttpMethod.Put, uri)
        {
            Content = new ByteArrayContent(azureTagsXml),
        };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/xml");
        request.Content.Headers.ContentLength = azureTagsXml.LongLength;
        return SendBlobRequestAsync(request, cancellationToken);
    }

    /// <summary>
    /// Issues <c>HEAD {blob}</c> (Get Blob Properties). The response carries
    /// the <c>x-ms-immutability-policy-until-date</c>, <c>x-ms-immutability-
    /// policy-mode</c>, and <c>x-ms-legal-hold</c> headers (when set) that back
    /// S3 GetObjectRetention / GetObjectLegalHold.
    /// </summary>
    public Task<HttpResponseMessage> HeadBlobAsync(string container, string key, CancellationToken cancellationToken)
    {
        var uri = BuildBlobUri(container, key);
        var request = new HttpRequestMessage(HttpMethod.Head, uri);
        return SendBlobRequestAsync(request, cancellationToken);
    }

    /// <summary>
    /// Issues <c>PUT {blob}?comp=immutabilityPolicies</c> setting a time-based
    /// retention policy. <paramref name="mode"/> is <c>Unlocked</c> or
    /// <c>Locked</c>; <paramref name="untilDateRfc1123"/> is the RFC1123 expiry.
    /// Backs S3 PutObjectRetention.
    /// </summary>
    public Task<HttpResponseMessage> SetBlobImmutabilityPolicyAsync(
        string container, string key, string untilDateRfc1123, string mode, CancellationToken cancellationToken)
    {
        var uri = BuildBlobUri(container, key, "?comp=immutabilityPolicies");
        var request = new HttpRequestMessage(HttpMethod.Put, uri);
        request.Headers.TryAddWithoutValidation("x-ms-immutability-policy-until-date", untilDateRfc1123);
        request.Headers.TryAddWithoutValidation("x-ms-immutability-policy-mode", mode);
        return SendBlobRequestAsync(request, cancellationToken);
    }

    /// <summary>
    /// Issues <c>DELETE {blob}?comp=immutabilityPolicies</c> removing an
    /// unlocked retention policy (fails on a locked policy, like S3).
    /// </summary>
    public Task<HttpResponseMessage> DeleteBlobImmutabilityPolicyAsync(
        string container, string key, CancellationToken cancellationToken)
    {
        var uri = BuildBlobUri(container, key, "?comp=immutabilityPolicies");
        return SendAsync(HttpMethod.Delete, uri, cancellationToken);
    }

    /// <summary>
    /// Issues <c>PUT {blob}?comp=legalhold</c> setting/clearing the blob legal
    /// hold via <c>x-ms-legal-hold</c>. Backs S3 PutObjectLegalHold.
    /// </summary>
    public Task<HttpResponseMessage> SetBlobLegalHoldAsync(
        string container, string key, bool hold, CancellationToken cancellationToken)
    {
        var uri = BuildBlobUri(container, key, "?comp=legalhold");
        var request = new HttpRequestMessage(HttpMethod.Put, uri);
        request.Headers.TryAddWithoutValidation("x-ms-legal-hold", hold ? "true" : "false");
        return SendBlobRequestAsync(request, cancellationToken);
    }

    /// <summary>
    /// Issues <c>PUT {container}?restype=container&amp;comp=metadata</c>
    /// replacing the container metadata with <paramref name="metadata"/>.
    /// Used by the S3 module to back PutBucketTagging / DeleteBucketTagging
    /// (Azure has no native bucket-tagging endpoint).
    /// </summary>
    public Task<HttpResponseMessage> SetContainerMetadataAsync(
        string container, IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken)
    {
        var uri = new Uri(_serviceEndpoint, $"{container}?restype=container&comp=metadata");
        var request = new HttpRequestMessage(HttpMethod.Put, uri);
        foreach (var kv in metadata)
        {
            request.Headers.TryAddWithoutValidation("x-ms-meta-" + kv.Key, kv.Value);
        }
        request.Content = new ByteArrayContent(Array.Empty<byte>());
        request.Content.Headers.ContentLength = 0;
        return SendBlobRequestAsync(request, cancellationToken);
    }

    /// <summary>
    /// Issues <c>GET {container}?restype=container&amp;comp=metadata</c>.
    /// Returns the raw response so callers can read <c>x-ms-meta-*</c>
    /// headers directly.
    /// </summary>
    public Task<HttpResponseMessage> GetContainerMetadataAsync(string container, CancellationToken cancellationToken)
    {
        var uri = new Uri(_serviceEndpoint, $"{container}?restype=container&comp=metadata");
        return SendAsync(HttpMethod.Get, uri, cancellationToken);
    }

    /// <summary>
    /// Extracts the <c>x-ms-meta-*</c> headers from a container properties
    /// or metadata response into a case-sensitive dictionary keyed by the
    /// bare metadata name (no prefix). The header names returned by Azure
    /// are lower-cased to match the keys accepted by
    /// <see cref="SetContainerMetadataAsync"/>.
    /// </summary>
    public static IDictionary<string, string> ReadContainerMetadata(HttpResponseMessage response)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var header in response.Headers)
        {
            if (header.Key.StartsWith("x-ms-meta-", StringComparison.OrdinalIgnoreCase))
            {
                var key = header.Key.Substring("x-ms-meta-".Length).ToLowerInvariant();
                foreach (var v in header.Value) { result[key] = v; break; }
            }
        }
        return result;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, Uri uri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.TryAddWithoutValidation("x-ms-date",
            DateTimeOffset.UtcNow.ToString("R", CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation("x-ms-version", "2021-12-02");
        await _auth.AuthenticateAsync(request, cancellationToken).ConfigureAwait(false);
        return await BackendTimingContext.TimeAsync(
            () => _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
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
