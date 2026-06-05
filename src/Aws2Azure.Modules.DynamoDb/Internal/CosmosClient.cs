using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Aws2Azure.Core.Azure;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Core.Observability;

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
    private readonly Uri _baseUri;
    private readonly ICosmosAuthenticator _authenticator;

    public const string ApiVersion = "2018-12-31";

    public string DatabaseName { get; }
    
    /// <summary>
    /// Returns the Cosmos account endpoint URL (for cache keying).
    /// </summary>
    public string AccountEndpoint => _endpoint;

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
        // The account base URI is constant per client; parsing it per request
        // (new Uri over endpoint.TrimEnd('/') + "/") was pure hot-path waste.
        // Hoisted here so SendAsync only pays the per-request relative resolve.
        _baseUri = new Uri(_endpoint.TrimEnd('/') + "/", UriKind.Absolute);
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

        using var request = new HttpRequestMessage(method, new Uri(_baseUri, requestUri.TrimStart('/')));

        try
        {
            await _authenticator.AuthenticateAsync(request, resourceType, resourceLink, ct).ConfigureAwait(false);
        }
        catch (EntraIdTokenException ex)
        {
            // AAD token acquisition failed before the Cosmos request was sent.
            // Surface a synthetic response carrying the normalised backend status so
            // the existing CosmosOpsShared.WriteCosmosErrorAsync mapping renders the
            // faithful DynamoDB error (token 429 -> ProvisionedThroughputExceededException,
            // transient 503 -> InternalServerError, auth -> AccessDeniedException).
            // Mirrors the open-breaker synthetic-503 (#211); zero per-operation code.
            return new HttpResponseMessage(ex.BackendStatus)
            {
                RequestMessage = request,
                Content = new ByteArrayContent([]),
            };
        }

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
        // shared client honours retry budgets and breaker state. HTTP 429
        // (RU throttling) is passed straight through without internal retry —
        // the DynamoDB error mapper surfaces it as
        // ProvisionedThroughputExceededException so the AWS SDK owns the
        // back-off (see AzureHttpClient.SendAsync).
        return await BackendTimingContext.TimeAsync(
            () => _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct))
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the Cosmos DatabaseAccount resource (<c>GET /</c>) and returns its
    /// default consistency level. Used by the #204 startup probe to detect
    /// accounts that cannot honor DynamoDB <c>ConsistentRead</c>. Account-level
    /// reads sign with an empty resource type and link. Throws on a non-success
    /// status so the caller can distinguish a probe failure from a determinable
    /// (but weak) level.
    /// </summary>
    public async Task<CosmosConsistencyLevel> ReadAccountConsistencyAsync(CancellationToken ct)
    {
        using var resp = await SendAsync(
            HttpMethod.Get,
            resourceType: string.Empty,
            resourceLink: string.Empty,
            requestUri: "/",
            content: null,
            extraHeaders: null,
            ct).ConfigureAwait(false);

        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        return CosmosConsistency.ParseDefaultConsistency(body);
    }
}

