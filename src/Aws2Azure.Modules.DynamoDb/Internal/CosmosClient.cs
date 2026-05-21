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
/// Cosmos-specific concerns: master-key signing, the
/// <c>x-ms-date</c> / <c>x-ms-version</c> / <c>x-ms-documentdb-*</c>
/// header set, and partition-key threading.
///
/// <para>This is the equivalent of the SQS module's
/// <c>ServiceBusClient</c>. It exposes a single
/// <see cref="SendAsync"/> entry that takes the Cosmos resource
/// triple (verb, resourceType, resourceLink) and the relative request
/// URL, signs the request, sends it through the shared
/// <see cref="AzureHttpClient"/> (so retries / breaker / metrics are
/// reused), and returns the raw response for op handlers to parse.</para>
///
/// <para>Slice 0 ships master-key auth only; the AAD path lands in
/// Slice 1 when CreateTable surfaces it.</para>
/// </summary>
internal sealed class CosmosClient
{
    private readonly AzureHttpClient _http;
    private readonly CosmosCredentials _credentials;
    private readonly Func<DateTimeOffset> _clock;

    public const string ApiVersion = "2018-12-31";

    public CosmosClient(AzureHttpClient http, CosmosCredentials credentials)
        : this(http, credentials, clock: null) { }

    internal CosmosClient(AzureHttpClient http, CosmosCredentials credentials, Func<DateTimeOffset>? clock)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(credentials);
        if (string.IsNullOrEmpty(credentials.Endpoint))
            throw new ArgumentException("Cosmos endpoint must be configured.", nameof(credentials));
        if (string.IsNullOrEmpty(credentials.PrimaryKey))
            throw new ArgumentException("Cosmos primary key must be configured.", nameof(credentials));

        _http = http;
        _credentials = credentials;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Sends a signed Cosmos REST request. The <paramref name="resourceType"/>
    /// and <paramref name="resourceLink"/> are used **only** for signing — the
    /// caller is responsible for putting the request URL together (typically
    /// the resource link with a leading slash, optionally with query string).
    /// </summary>
    /// <param name="method">HTTP verb.</param>
    /// <param name="resourceType">Cosmos resource type singular (<c>dbs</c>, <c>colls</c>, <c>docs</c>, …).</param>
    /// <param name="resourceLink">Resource link without leading slash (<c>dbs/{db}</c>, <c>dbs/{db}/colls/{col}/docs/{id}</c>), or empty string for resource-type root operations.</param>
    /// <param name="requestUri">Path + query relative to the Cosmos endpoint, with leading slash.</param>
    /// <param name="content">Optional request body. Caller sets Content-Type.</param>
    /// <param name="extraHeaders">Optional headers to add (e.g. <c>x-ms-documentdb-partitionkey</c>).</param>
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

        var baseUri = new Uri(_credentials.Endpoint.TrimEnd('/') + "/", UriKind.Absolute);
        using var request = new HttpRequestMessage(method, new Uri(baseUri, requestUri.TrimStart('/')));

        var utcDate = CosmosMasterKeyAuth.GetHttpUtcDate(_clock());
        var auth = CosmosMasterKeyAuth.Build(method.Method, resourceType, resourceLink, utcDate, _credentials.PrimaryKey);

        request.Headers.TryAddWithoutValidation("authorization", auth);
        request.Headers.TryAddWithoutValidation("x-ms-date", utcDate);
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
