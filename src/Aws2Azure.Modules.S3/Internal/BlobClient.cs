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
