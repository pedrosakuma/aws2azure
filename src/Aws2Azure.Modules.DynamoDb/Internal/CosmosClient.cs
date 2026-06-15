using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Aws2Azure.Core.Azure;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Core.Observability;
using Microsoft.Extensions.Logging;

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
    private readonly IReadOnlyList<string>? _preferredRegions;
    private readonly ILogger? _regionLogger;
    private readonly CosmosAccountCacheEntry _accountCache;
    private readonly bool _cosmosBinaryResponses;

    public const string ApiVersion = "2018-12-31";
    internal const string CosmosBinarySerializationHeader = "x-ms-cosmos-supported-serialization-formats";
    internal const string CosmosBinarySerializationValue = "CosmosBinary";
    private static readonly TimeSpan AccountRefreshTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan EndpointUnavailableTtl = TimeSpan.FromSeconds(30);
    private static readonly ConcurrentDictionary<string, CosmosAccountCacheEntry> AccountCache =
        new(StringComparer.OrdinalIgnoreCase);

    public string DatabaseName { get; }
    
    /// <summary>
    /// Returns the Cosmos account endpoint URL (for cache keying).
    /// </summary>
    public string AccountEndpoint => _endpoint;

    public CosmosClient(
        AzureHttpClient http,
        CosmosCredentials credentials,
        ICosmosAuthenticator authenticator,
        ILogger? regionLogger = null,
        bool cosmosBinaryResponses = false)
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
        _preferredRegions = credentials.PreferredRegions;
        _regionLogger = regionLogger;
        _accountCache = AccountCache.GetOrAdd(_baseUri.AbsoluteUri, _ => new CosmosAccountCacheEntry());
        _cosmosBinaryResponses = cosmosBinaryResponses;
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
        ReadOnlyMemory<byte>? bufferedContent = null;
        HttpContentHeaders? bufferedContentHeaders = null;
        if (content is not null)
        {
            bufferedContent = await content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            bufferedContentHeaders = content.Headers;
        }

        return await SendBufferedAsync(
            method, resourceType, resourceLink, requestUri,
            bufferedContent, bufferedContentHeaders, contentType: null,
            extraHeaders, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Zero-copy request-body overload: sends <paramref name="body"/> as the raw
    /// request payload without the <c>byte[] → string → byte[]</c> round-trip a
    /// <see cref="StringContent"/> would incur. The caller owns
    /// <paramref name="body"/> (typically a pooled
    /// <see cref="PooledByteBufferWriter"/>) and MUST keep it valid until this
    /// call completes — the bytes are read once per attempt (retries / failover).
    /// </summary>
    public Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string resourceType,
        string resourceLink,
        string requestUri,
        ReadOnlyMemory<byte> body,
        string contentType,
        System.Collections.Generic.IReadOnlyList<KeyValuePair<string, string>>? extraHeaders,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(contentType);
        return SendBufferedAsync(
            method, resourceType, resourceLink, requestUri,
            body, bufferedContentHeaders: null, contentType,
            extraHeaders, ct);
    }

    private async Task<HttpResponseMessage> SendBufferedAsync(
        HttpMethod method,
        string resourceType,
        string resourceLink,
        string requestUri,
        ReadOnlyMemory<byte>? bufferedContent,
        HttpContentHeaders? bufferedContentHeaders,
        string? contentType,
        System.Collections.Generic.IReadOnlyList<KeyValuePair<string, string>>? extraHeaders,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(resourceType);
        ArgumentNullException.ThrowIfNull(resourceLink);
        ArgumentException.ThrowIfNullOrEmpty(requestUri);

        var isRead = CosmosRegionRouting.IsReadOperation(method, extraHeaders);
        var account = await GetAccountInfoForRoutingAsync(ct).ConfigureAwait(false);
        var candidates = CosmosRegionRouting.BuildCandidateEndpoints(
            account, _preferredRegions, isRead, IsEndpointAvailable);
        if (candidates.Length == 0)
        {
            candidates = [account.AccountEndpoint];
        }

        if (_regionLogger is not null)
        {
            CosmosRegionLog.SelectedEndpoint(
                _regionLogger,
                isRead ? "read" : "write",
                candidates[0].AbsoluteUri);
        }

        HttpResponseMessage? lastFailoverResponse = null;
        var attemptedEndpoints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            var endpoint = SelectNextCandidate(candidates, attemptedEndpoints);
            if (endpoint is null)
            {
                break;
            }
            attemptedEndpoints.Add(endpoint.AbsoluteUri);

            try
            {
                using var request = new HttpRequestMessage(method, new Uri(endpoint, requestUri.TrimStart('/')));
                HttpContent? attemptContent = null;
                try
                {
                    if (bufferedContent.HasValue)
                    {
                        attemptContent = CreateBufferedContent(bufferedContent.Value, bufferedContentHeaders, contentType);
                        request.Content = attemptContent;
                    }

                    await AuthenticateAndApplyHeadersAsync(
                        request, resourceType, resourceLink, extraHeaders, ct).ConfigureAwait(false);

                    var response = await BackendTimingContext.TimeAsync(
                        () => _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct))
                        .ConfigureAwait(false);

                    if (CosmosRegionRouting.IsFailoverStatus(response, !isRead))
                    {
                        if (!isRead)
                        {
                            // Write 403 substatus 3: this region is not the
                            // current write region. It is still perfectly
                            // readable, so do NOT mark it unavailable (that would
                            // evict it from read routing for the whole TTL).
                            // Re-discover the write region and rebuild candidates.
                            account = await RefreshAccountInfoForRoutingAsync(ct).ConfigureAwait(false);
                            candidates = CosmosRegionRouting.BuildCandidateEndpoints(
                                account, _preferredRegions, isRead, IsEndpointAvailable);
                        }
                        else
                        {
                            // Read 503/408: the region is genuinely impaired —
                            // evict it so subsequent reads route elsewhere.
                            MarkEndpointUnavailable(endpoint);
                        }

                        var next = SelectNextCandidate(candidates, attemptedEndpoints);
                        if (next is not null)
                        {
                            if (_regionLogger is not null)
                            {
                                CosmosRegionLog.Failover(
                                    _regionLogger,
                                    endpoint.AbsoluteUri,
                                    next.AbsoluteUri,
                                    (int)response.StatusCode);
                            }
                            lastFailoverResponse?.Dispose();
                            lastFailoverResponse = response;
                            continue;
                        }
                    }

                    lastFailoverResponse?.Dispose();
                    return response;
                }
                finally
                {
                    attemptContent?.Dispose();
                }
            }
            catch (EntraIdTokenException ex)
            {
                lastFailoverResponse?.Dispose();
                // AAD token acquisition failed before the Cosmos request was sent.
                // Surface a synthetic response carrying the normalised backend status so
                // the existing CosmosOpsShared.WriteCosmosErrorAsync mapping renders the
                // faithful DynamoDB error (token 429 -> ProvisionedThroughputExceededException,
                // transient 503 -> InternalServerError, auth -> AccessDeniedException).
                // Mirrors the open-breaker synthetic-503 (#211); zero per-operation code.
                return new HttpResponseMessage(ex.BackendStatus)
                {
                    RequestMessage = new HttpRequestMessage(method, new Uri(endpoint, requestUri.TrimStart('/'))),
                    Content = new ByteArrayContent([]),
                };
            }
            catch (HttpRequestException)
            {
                MarkEndpointUnavailable(endpoint);
                if (!isRead)
                {
                    // Ambiguous write transport failure: the write may have
                    // committed. Do not replay to another region — return a
                    // synthetic 503 and let the AWS SDK own the retry.
                    lastFailoverResponse?.Dispose();
                    return BuildSyntheticServiceUnavailable(method, endpoint, requestUri);
                }

                var next = SelectNextCandidate(candidates, attemptedEndpoints);
                if (next is null)
                {
                    lastFailoverResponse?.Dispose();
                    return BuildSyntheticServiceUnavailable(method, endpoint, requestUri);
                }

                if (_regionLogger is not null)
                {
                    CosmosRegionLog.Failover(_regionLogger, endpoint.AbsoluteUri, next.AbsoluteUri, 0);
                }
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                MarkEndpointUnavailable(endpoint);
                if (!isRead)
                {
                    // Ambiguous write timeout: the write may have committed.
                    // Do not replay to another region — return a synthetic 503
                    // and let the AWS SDK own the retry.
                    lastFailoverResponse?.Dispose();
                    return BuildSyntheticServiceUnavailable(method, endpoint, requestUri);
                }

                var next = SelectNextCandidate(candidates, attemptedEndpoints);
                if (next is null)
                {
                    lastFailoverResponse?.Dispose();
                    return BuildSyntheticServiceUnavailable(method, endpoint, requestUri);
                }

                if (_regionLogger is not null)
                {
                    CosmosRegionLog.Failover(_regionLogger, endpoint.AbsoluteUri, next.AbsoluteUri, 0);
                }
            }
        }

        if (lastFailoverResponse is not null)
        {
            return lastFailoverResponse;
        }

        return BuildSyntheticServiceUnavailable(method, account.AccountEndpoint, requestUri);
    }

    private static Uri? SelectNextCandidate(Uri[] candidates, HashSet<string> attemptedEndpoints)
    {
        for (int i = 0; i < candidates.Length; i++)
        {
            if (!attemptedEndpoints.Contains(candidates[i].AbsoluteUri))
            {
                return candidates[i];
            }
        }

        return null;
    }

    private async Task AuthenticateAndApplyHeadersAsync(
        HttpRequestMessage request,
        string resourceType,
        string resourceLink,
        IReadOnlyList<KeyValuePair<string, string>>? extraHeaders,
        CancellationToken ct)
    {
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

        // Opt-in CosmosBinary response negotiation (#268): request binary bodies
        // for read ops on documents. Applied per attempt so every failover
        // target carries the header; the response path falls back to text
        // transparently if the server ignores it.
        if (_cosmosBinaryResponses
            && ShouldRequestBinaryResponse(request.Method, resourceType, extraHeaders)
            && !request.Headers.Contains(CosmosBinarySerializationHeader))
        {
            request.Headers.TryAddWithoutValidation(CosmosBinarySerializationHeader, CosmosBinarySerializationValue);
        }
    }

    private async Task<CosmosAccountInfo> GetAccountInfoForRoutingAsync(CancellationToken ct)
    {
        if (!HasPreferredRegions())
        {
            // Region-aware routing is opt-in. Without an explicit preference
            // list, keep the configured account endpoint as the only target.
            return CosmosAccountInfo.Fallback(_baseUri);
        }

        try
        {
            return await GetOrRefreshAccountInfoAsync(forceRefresh: false, strict: false, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return CosmosAccountInfo.Fallback(_baseUri);
        }
    }

    private bool HasPreferredRegions()
    {
        if (_preferredRegions is null)
        {
            return false;
        }

        for (int i = 0; i < _preferredRegions.Count; i++)
        {
            if (!string.IsNullOrWhiteSpace(_preferredRegions[i]))
            {
                return true;
            }
        }

        return false;
    }

    private Task<CosmosAccountInfo> RefreshAccountInfoForRoutingAsync(CancellationToken ct)
        => GetOrRefreshAccountInfoAsync(forceRefresh: true, strict: false, ct);

    private async Task<CosmosAccountInfo> GetOrRefreshAccountInfoAsync(
        bool forceRefresh,
        bool strict,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = _accountCache.Snapshot;
        if (!forceRefresh
            && snapshot is { } cached
            && cached.ExpiresAt > now)
        {
            return cached.Info;
        }

        await _accountCache.RefreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            now = DateTimeOffset.UtcNow;
            snapshot = _accountCache.Snapshot;
            if (!forceRefresh
                && snapshot is { } cachedAfterWait
                && cachedAfterWait.ExpiresAt > now)
            {
                return cachedAfterWait.Info;
            }

            var refreshed = await ReadAccountInfoDirectAsync(ct).ConfigureAwait(false);
            _accountCache.Snapshot = new AccountSnapshot(refreshed, now + AccountRefreshTtl);
            if (_regionLogger is not null)
            {
                CosmosRegionLog.DiscoveredRegions(
                    _regionLogger,
                    _endpoint,
                    CosmosRegionRouting.BuildLocationSummary(refreshed.ReadableLocations),
                    CosmosRegionRouting.BuildLocationSummary(refreshed.WritableLocations),
                    refreshed.EnableMultipleWriteLocations);
            }
            return refreshed;
        }
        catch (Exception ex) when (!strict && ex is not OperationCanceledException)
        {
            if (_accountCache.Snapshot is { } cachedOnFailure)
            {
                return cachedOnFailure.Info;
            }

            var fallback = CosmosAccountInfo.Fallback(_baseUri);
            _accountCache.Snapshot = new AccountSnapshot(
                fallback, DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30));
            return fallback;
        }
        finally
        {
            _accountCache.RefreshGate.Release();
        }
    }

    private async Task<CosmosAccountInfo> ReadAccountInfoDirectAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _baseUri);
        await AuthenticateAndApplyHeadersAsync(
            request,
            resourceType: string.Empty,
            resourceLink: string.Empty,
            extraHeaders: null,
            ct).ConfigureAwait(false);

        using var response = await BackendTimingContext.TimeAsync(
            () => _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct))
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        return CosmosAccountInfoParser.Parse(body, _baseUri);
    }

    private bool IsEndpointAvailable(Uri endpoint)
    {
        var key = endpoint.AbsoluteUri;
        if (!_accountCache.UnavailableUntil.TryGetValue(key, out var until))
        {
            return true;
        }

        if (until <= DateTimeOffset.UtcNow)
        {
            _accountCache.UnavailableUntil.TryRemove(key, out _);
            return true;
        }

        return false;
    }

    private void MarkEndpointUnavailable(Uri endpoint)
    {
        _accountCache.UnavailableUntil[endpoint.AbsoluteUri] = DateTimeOffset.UtcNow + EndpointUnavailableTtl;
    }

    private static HttpContent CreateBufferedContent(
        ReadOnlyMemory<byte> bytes, HttpContentHeaders? headers, string? contentType)
    {
        var content = new ReadOnlyMemoryContent(bytes);
        if (headers is not null)
        {
            foreach (var header in headers)
            {
                content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }
        else if (!string.IsNullOrEmpty(contentType))
        {
            content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        }
        return content;
    }

    private static HttpResponseMessage BuildSyntheticServiceUnavailable(
        HttpMethod method,
        Uri endpoint,
        string requestUri)
    {
        return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            RequestMessage = new HttpRequestMessage(method, new Uri(endpoint, requestUri.TrimStart('/'))),
            Content = new ByteArrayContent([]),
        };
    }

    private static bool ShouldRequestBinaryResponse(
        HttpMethod method,
        string resourceType,
        System.Collections.Generic.IReadOnlyList<KeyValuePair<string, string>>? extraHeaders)
    {
        if (!string.Equals(resourceType, "docs", StringComparison.Ordinal))
        {
            return false;
        }

        if (method == HttpMethod.Get)
        {
            return true;
        }

        if (method != HttpMethod.Post || extraHeaders is null)
        {
            return false;
        }

        for (int i = 0; i < extraHeaders.Count; i++)
        {
            var kv = extraHeaders[i];
            if (string.Equals(kv.Key, "x-ms-documentdb-isquery", StringComparison.OrdinalIgnoreCase)
                && string.Equals(kv.Value, "true", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
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
        var account = await GetOrRefreshAccountInfoAsync(forceRefresh: true, strict: true, ct).ConfigureAwait(false);
        return account.DefaultConsistency;
    }

    private sealed class CosmosAccountCacheEntry
    {
        public SemaphoreSlim RefreshGate { get; } = new(1, 1);
        public ConcurrentDictionary<string, DateTimeOffset> UnavailableUntil { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        // Info + expiry are published together as one immutable reference so the
        // unsynchronised fast-path read can never observe a torn pair (fresh
        // info with a stale expiry, or vice versa). The volatile reference gives
        // atomic publication and acquire/release ordering.
        private volatile AccountSnapshot? _snapshot;
        public AccountSnapshot? Snapshot
        {
            get => _snapshot;
            set => _snapshot = value;
        }
    }

    private sealed class AccountSnapshot
    {
        public AccountSnapshot(CosmosAccountInfo info, DateTimeOffset expiresAt)
        {
            Info = info;
            ExpiresAt = expiresAt;
        }

        public CosmosAccountInfo Info { get; }
        public DateTimeOffset ExpiresAt { get; }
    }
}
