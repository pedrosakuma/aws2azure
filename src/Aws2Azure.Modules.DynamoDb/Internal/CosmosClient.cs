using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Aws2Azure.Core.Azure;
using Aws2Azure.Core.Configuration;

namespace Aws2Azure.Modules.DynamoDb.Internal;

/// <summary>
/// Thin REST client over <see cref="AzureHttpClient"/> that handles
/// Cosmos-specific concerns: master-key OR AAD signing, the
/// <c>x-ms-date</c> / <c>x-ms-version</c> / <c>x-ms-documentdb-*</c>
/// header set, and partition-key threading.
///
/// <para>Auth scheme is injected via <see cref="ICosmosAuthenticator"/>
/// so the same client serves both credential shapes the proxy
/// supports. The chosen authenticator is fixed per credential
/// (i.e. per AWS access key) at module composition time.</para>
/// </summary>
internal sealed class CosmosClient
{
    private readonly AzureHttpClient _http;
    private readonly string _endpoint;
    private readonly ICosmosAuthenticator _authenticator;

    public const string ApiVersion = "2018-12-31";

    public string DatabaseName { get; }

    public CosmosClient(AzureHttpClient http, CosmosCredentials credentials, ICosmosAuthenticator authenticator)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(authenticator);
        if (string.IsNullOrEmpty(credentials.Endpoint))
            throw new ArgumentException("Cosmos endpoint must be configured.", nameof(credentials));
        if (string.IsNullOrEmpty(credentials.DatabaseName))
            throw new ArgumentException("Cosmos database name must be configured.", nameof(credentials));

        _http = http;
        _endpoint = credentials.Endpoint;
        _authenticator = authenticator;
        DatabaseName = credentials.DatabaseName;
    }

    /// <summary>
    /// Sends a signed Cosmos REST request. The <paramref name="resourceType"/>
    /// and <paramref name="resourceLink"/> are used by master-key signing
    /// (and forwarded to RBAC checks under AAD); the caller is
    /// responsible for putting the request URL together (typically the
    /// resource link with a leading slash, optionally with query string).
    /// </summary>
    public async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string resourceType,
        string resourceLink,
        string requestUri,
        HttpContent? content,
        System.Collections.Generic.IReadOnlyList<KeyValuePair<string, string>>? extraHeaders,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(resourceType);
        ArgumentNullException.ThrowIfNull(resourceLink);
        ArgumentException.ThrowIfNullOrEmpty(requestUri);

        var baseUri = new Uri(_endpoint.TrimEnd('/') + "/", UriKind.Absolute);
        using var request = new HttpRequestMessage(method, new Uri(baseUri, requestUri.TrimStart('/')));

        await _authenticator.AuthenticateAsync(request, resourceType, resourceLink, ct).ConfigureAwait(false);
        request.Headers.TryAddWithoutValidation("x-ms-version", ApiVersion);

        if (extraHeaders is not null)
        {
            for (int i = 0; i < extraHeaders.Count; i++)
            {
                var kv = extraHeaders[i];
                request.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
            }
        }

        if (content is not null)
        {
            request.Content = content;
        }

        // Cosmos returns a Bearer-realm WWW-Authenticate on auth failure; the
        // shared client honours retry budgets, breaker state, and the breaker's
        // 429-aware backoff so transient-failure semantics match the SB module.
        return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
    }
}

