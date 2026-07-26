using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Aws2Azure.Core.Azure;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Modules.DynamoDb.Internal;
using Xunit;

namespace Aws2Azure.UnitTests.DynamoDb;

public class CosmosRegionRoutingTests
{
    private static readonly Uri Global = new("https://acct.documents.azure.com/");
    private static readonly Uri East = new("https://acct-east.documents.azure.com/");
    private static readonly Uri West = new("https://acct-west.documents.azure.com/");
    private static readonly Uri Central = new("https://acct-central.documents.azure.com/");

    [Fact]
    public void Parse_reads_locations_multi_write_and_consistency()
    {
        var info = CosmosAccountInfoParser.Parse(Encoding.UTF8.GetBytes("""
        {
          "id": "acct",
          "userConsistencyPolicy": { "defaultConsistencyLevel": "Session" },
          "enableMultipleWriteLocations": true,
          "readableLocations": [
            { "name": "East US", "databaseAccountEndpoint": "https://acct-east.documents.azure.com:443/" },
            { "name": "West US", "databaseAccountEndpoint": "https://acct-west.documents.azure.com:443/" }
          ],
          "writableLocations": [
            { "name": "West US", "databaseAccountEndpoint": "https://acct-west.documents.azure.com:443/" }
          ]
        }
        """), Global);

        Assert.Equal(CosmosConsistencyLevel.Session, info.DefaultConsistency);
        Assert.Equal("acct", info.AccountIdentity);
        Assert.True(info.EnableMultipleWriteLocations);
        Assert.Equal(2, info.ReadableLocations.Length);
        Assert.Equal("East US", info.ReadableLocations[0].Name);
        Assert.Equal("https://acct-east.documents.azure.com/", info.ReadableLocations[0].Endpoint.AbsoluteUri);
        Assert.Single(info.WritableLocations);
        Assert.Equal("West US", info.WritableLocations[0].Name);
    }

    [Fact]
    public void Read_selection_honors_preferred_region_order()
    {
        var info = AccountInfo(multiWrite: false);

        var candidates = CosmosRegionRouting.BuildCandidateEndpoints(
            info,
            new[] { "West US", "East US" },
            isRead: true);

        Assert.Equal(West, candidates[0]);
        Assert.Equal(East, candidates[1]);
        Assert.Equal(Central, candidates[2]);
        Assert.Equal(Global, candidates[3]);
    }

    [Fact]
    public void Read_selection_falls_back_when_preferred_region_absent()
    {
        var info = AccountInfo(multiWrite: false);

        var candidates = CosmosRegionRouting.BuildCandidateEndpoints(
            info,
            new[] { "North Europe" },
            isRead: true);

        Assert.Equal(East, candidates[0]);
        Assert.Equal(West, candidates[1]);
        Assert.Equal(Central, candidates[2]);
        Assert.Equal(Global, candidates[3]);
    }

    [Fact]
    public void Write_selection_uses_single_write_region_unless_multi_write_enabled()
    {
        var singleWrite = AccountInfo(multiWrite: false);
        var singleCandidates = CosmosRegionRouting.BuildCandidateEndpoints(
            singleWrite,
            new[] { "West US" },
            isRead: false);
        Assert.Equal(East, singleCandidates[0]);
        Assert.Equal(Global, singleCandidates[1]);

        var multiWrite = AccountInfo(multiWrite: true);
        var multiCandidates = CosmosRegionRouting.BuildCandidateEndpoints(
            multiWrite,
            new[] { "West US" },
            isRead: false);
        Assert.Equal(West, multiCandidates[0]);
        Assert.Equal(East, multiCandidates[1]);
        Assert.Equal(Global, multiCandidates[3]);
    }

    [Fact]
    public void Nontransaction_multi_write_routing_can_use_later_available_region()
    {
        var candidates = CosmosRegionRouting.BuildCandidateEndpoints(
            AccountInfo(multiWrite: true),
            new[] { "West US", "East US" },
            isRead: false,
            endpoint => endpoint != West);

        Assert.Equal(East, candidates[0]);
        Assert.DoesNotContain(West, candidates);
    }

    [Fact]
    public void Transaction_selection_uses_only_first_configured_multi_write_region()
    {
        var reordered = new CosmosAccountInfo(
            Global,
            CosmosConsistencyLevel.Session,
            enableMultipleWriteLocations: true,
            readableLocations:
            [
                new CosmosAccountLocation("East US", East),
                new CosmosAccountLocation("West US", West),
            ],
            writableLocations:
            [
                new CosmosAccountLocation("East US", East),
                new CosmosAccountLocation("West US", West),
            ]);
        var laterOnly = new CosmosAccountInfo(
            Global,
            CosmosConsistencyLevel.Session,
            enableMultipleWriteLocations: true,
            readableLocations:
            [
                new CosmosAccountLocation("East US", East),
            ],
            writableLocations:
            [
                new CosmosAccountLocation("East US", East),
            ]);

        var reorderedStatus =
            CosmosRegionRouting.SelectTransactionEndpoint(
                reordered,
                ["West US", "East US"],
                out var reorderedEndpoint);
        var laterOnlyStatus =
            CosmosRegionRouting.SelectTransactionEndpoint(
                laterOnly,
                ["West US", "East US"],
                out var laterOnlyEndpoint);
        var blankFirstStatus =
            CosmosRegionRouting.SelectTransactionEndpoint(
                reordered,
                [" ", "East US"],
                out var blankFirstEndpoint);

        Assert.Equal(
            CosmosTransactionEndpointSelectionStatus.Ready,
            reorderedStatus);
        Assert.Equal(West, reorderedEndpoint);
        Assert.Equal(
            CosmosTransactionEndpointSelectionStatus
                .AuthoritativeWriteRegionUnavailable,
            laterOnlyStatus);
        Assert.Null(laterOnlyEndpoint);
        Assert.Equal(
            CosmosTransactionEndpointSelectionStatus
                .PreferredWriteRegionRequired,
            blankFirstStatus);
        Assert.Null(blankFirstEndpoint);
    }

    [Fact]
    public void Failover_statuses_match_cosmos_region_triggers()
    {
        using var unavailable = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        Assert.True(CosmosRegionRouting.IsFailoverStatus(unavailable, isWrite: false));

        using var timeout = new HttpResponseMessage(HttpStatusCode.RequestTimeout);
        Assert.True(CosmosRegionRouting.IsFailoverStatus(timeout, isWrite: false));

        // Writes are ambiguous on 503/408 (the write may have committed), so
        // they must NOT auto-fail over on those — only reads do.
        Assert.False(CosmosRegionRouting.IsFailoverStatus(unavailable, isWrite: true));
        Assert.False(CosmosRegionRouting.IsFailoverStatus(timeout, isWrite: true));

        using var writeForbidden = new HttpResponseMessage(HttpStatusCode.Forbidden);
        writeForbidden.Headers.TryAddWithoutValidation("x-ms-substatus", "3");
        Assert.True(CosmosRegionRouting.IsFailoverStatus(writeForbidden, isWrite: true));
        Assert.False(CosmosRegionRouting.IsFailoverStatus(writeForbidden, isWrite: false));

        using var ordinaryForbidden = new HttpResponseMessage(HttpStatusCode.Forbidden);
        Assert.False(CosmosRegionRouting.IsFailoverStatus(ordinaryForbidden, isWrite: true));
    }

    [Fact]
    public void Missing_locations_preserve_single_region_endpoint()
    {
        var info = CosmosAccountInfoParser.Parse(Encoding.UTF8.GetBytes("""
        { "userConsistencyPolicy": { "defaultConsistencyLevel": "Strong" } }
        """), Global);

        var readCandidates = CosmosRegionRouting.BuildCandidateEndpoints(
            info,
            new[] { "West US" },
            isRead: true);
        var writeCandidates = CosmosRegionRouting.BuildCandidateEndpoints(
            info,
            new[] { "West US" },
            isRead: false);

        Assert.Single(readCandidates);
        Assert.Single(writeCandidates);
        Assert.Equal(Global, readCandidates[0]);
        Assert.Equal(Global, writeCandidates[0]);
    }

    [Fact]
    public async Task Client_fails_over_read_after_503()
    {
        var handler = new SequenceHandler(
            (Func<HttpRequestMessage, bool>)(req => req.RequestUri!.Host == "acct-region-read.documents.azure.com"),
            (Func<HttpResponseMessage>)(() => JsonResponse(HttpStatusCode.OK, AccountJson(readableFirst: East, writableFirst: East, multiWrite: false))),
            (Func<HttpRequestMessage, bool>)(req => req.RequestUri!.Host == "acct-west.documents.azure.com"),
            (Func<HttpResponseMessage>)(() => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)),
            (Func<HttpRequestMessage, bool>)(req => req.RequestUri!.Host == "acct-east.documents.azure.com"),
            (Func<HttpResponseMessage>)(() => JsonResponse(HttpStatusCode.OK, "{}")));
        using var http = new AzureHttpClient(handler, ownsHandler: false, NoRetryOptions());
        var creds = Credentials(endpoint: "https://acct-region-read.documents.azure.com/");
        creds.PreferredRegions = new List<string> { "West US", "East US" };
        var client = new CosmosClient(http, creds, new MasterKeyCosmosAuthenticator(creds.PrimaryKey));

        using var resp = await client.SendAsync(
            HttpMethod.Get,
            "docs",
            "dbs/main/colls/t/docs/1",
            "/dbs/main/colls/t/docs/1",
            content: null,
            extraHeaders: null,
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("acct-east.documents.azure.com", handler.Requests[2].RequestUri!.Host);
    }

    [Fact]
    public async Task Client_refreshes_locations_and_fails_over_write_forbidden()
    {
        var rootReads = 0;
        var handler = new SequenceHandler(
            (Func<HttpRequestMessage, bool>)(req => req.RequestUri!.Host == "acct-refresh.documents.azure.com"),
            (Func<HttpResponseMessage>)(() =>
            {
                rootReads++;
                return JsonResponse(HttpStatusCode.OK,
                    rootReads == 1
                        ? AccountJson(readableFirst: East, writableFirst: East, multiWrite: false)
                        : AccountJson(readableFirst: West, writableFirst: West, multiWrite: false));
            }),
            (Func<HttpRequestMessage, bool>)(req => req.RequestUri!.Host == "acct-east.documents.azure.com"),
            (Func<HttpResponseMessage>)(() =>
            {
                var resp = new HttpResponseMessage(HttpStatusCode.Forbidden);
                resp.Headers.TryAddWithoutValidation("x-ms-substatus", "3");
                return resp;
            }),
            (Func<HttpRequestMessage, bool>)(req => req.RequestUri!.Host == "acct-west.documents.azure.com"),
            (Func<HttpResponseMessage>)(() => JsonResponse(HttpStatusCode.Created, "{}")));
        using var http = new AzureHttpClient(handler, ownsHandler: false, NoRetryOptions());
        var creds = Credentials(endpoint: "https://acct-refresh.documents.azure.com/");
        creds.PreferredRegions = new List<string> { "East US" };
        var client = new CosmosClient(http, creds, new MasterKeyCosmosAuthenticator(creds.PrimaryKey));

        using var content = new StringContent("{}", Encoding.UTF8, "application/json");
        using var resp = await client.SendAsync(
            HttpMethod.Post,
            "docs",
            "dbs/main/colls/t",
            "/dbs/main/colls/t/docs",
            content,
            extraHeaders: null,
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        Assert.Equal(2, rootReads);
        Assert.Equal("acct-west.documents.azure.com", handler.Requests[^1].RequestUri!.Host);
    }

    [Fact]
    public async Task Strict_transaction_discovery_refreshes_statusless_fallback()
    {
        var global = new Uri(
            "https://acct-transaction-strict.documents.azure.com/");
        var rootReads = 0;
        var handler = new SequenceHandler(
            (Func<HttpRequestMessage, bool>)(request =>
                request.RequestUri == global),
            (Func<HttpResponseMessage>)(() =>
            {
                rootReads++;
                return JsonResponse(HttpStatusCode.TooManyRequests, "{}");
            }),
            (Func<HttpRequestMessage, bool>)(request =>
                request.RequestUri!.AbsolutePath.EndsWith(
                    "/docs/1",
                    StringComparison.Ordinal)),
            (Func<HttpResponseMessage>)(() => JsonResponse(
                HttpStatusCode.OK,
                "{}")));
        using var http = new AzureHttpClient(
            handler,
            ownsHandler: false,
            NoRetryOptions());
        var credentials = Credentials(global.AbsoluteUri);
        credentials.PreferredRegions = ["West US"];
        var client = new CosmosClient(
            http,
            credentials,
            new MasterKeyCosmosAuthenticator(credentials.PrimaryKey));

        using var read = await client.SendAsync(
            HttpMethod.Get,
            "docs",
            "dbs/main/colls/orders/docs/1",
            "/dbs/main/colls/orders/docs/1",
            content: null,
            extraHeaders: null,
            CancellationToken.None);
        var resolution = await client.ResolveTransactionRouteAsync(
            "orders",
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(2, rootReads);
        Assert.Equal(
            CosmosTransactionRouteResolutionStatus.Unavailable,
            resolution.Status);
        Assert.Equal(
            HttpStatusCode.TooManyRequests,
            resolution.BackendStatus);
    }

    [Fact]
    public async Task Fresh_clients_use_first_configured_transaction_authority_or_fail()
    {
        var completeConfiguredEndpoint = new Uri(
            "https://acct-transaction-complete.documents.azure.com/");
        var reorderedConfiguredEndpoint = new Uri(
            "https://acct-transaction-reordered.documents.azure.com/");
        var laterOnlyConfiguredEndpoint = new Uri(
            "https://acct-transaction-later-only.documents.azure.com/");
        var completeHandler = new SequenceHandler(
            (Func<HttpRequestMessage, bool>)(request =>
                request.RequestUri == completeConfiguredEndpoint),
            (Func<HttpResponseMessage>)(() => JsonResponse(
                HttpStatusCode.OK,
                MultiWriteAccountJson(
                    new CosmosAccountLocation("West US", West),
                    new CosmosAccountLocation("East US", East)))));
        var reorderedHandler = new SequenceHandler(
            (Func<HttpRequestMessage, bool>)(request =>
                request.RequestUri == reorderedConfiguredEndpoint),
            (Func<HttpResponseMessage>)(() => JsonResponse(
                HttpStatusCode.OK,
                MultiWriteAccountJson(
                    new CosmosAccountLocation("East US", East),
                    new CosmosAccountLocation("West US", West)))));
        var laterOnlyHandler = new SequenceHandler(
            (Func<HttpRequestMessage, bool>)(request =>
                request.RequestUri == laterOnlyConfiguredEndpoint),
            (Func<HttpResponseMessage>)(() => JsonResponse(
                HttpStatusCode.OK,
                MultiWriteAccountJson(
                    new CosmosAccountLocation("East US", East)))));
        using var completeHttp = new AzureHttpClient(
            completeHandler,
            ownsHandler: false,
            NoRetryOptions());
        using var reorderedHttp = new AzureHttpClient(
            reorderedHandler,
            ownsHandler: false,
            NoRetryOptions());
        using var laterOnlyHttp = new AzureHttpClient(
            laterOnlyHandler,
            ownsHandler: false,
            NoRetryOptions());
        var completeCredentials = Credentials(
            completeConfiguredEndpoint.AbsoluteUri);
        completeCredentials.PreferredRegions = ["West US", "East US"];
        var reorderedCredentials = Credentials(
            reorderedConfiguredEndpoint.AbsoluteUri);
        reorderedCredentials.PreferredRegions = ["West US", "East US"];
        var laterOnlyCredentials = Credentials(
            laterOnlyConfiguredEndpoint.AbsoluteUri);
        laterOnlyCredentials.PreferredRegions = ["West US", "East US"];
        var completeClient = new CosmosClient(
            completeHttp,
            completeCredentials,
            new MasterKeyCosmosAuthenticator(
                completeCredentials.PrimaryKey));
        var reorderedClient = new CosmosClient(
            reorderedHttp,
            reorderedCredentials,
            new MasterKeyCosmosAuthenticator(
                reorderedCredentials.PrimaryKey));
        var laterOnlyClient = new CosmosClient(
            laterOnlyHttp,
            laterOnlyCredentials,
            new MasterKeyCosmosAuthenticator(
                laterOnlyCredentials.PrimaryKey));

        var complete = await completeClient.ResolveTransactionRouteAsync(
            "orders",
            CancellationToken.None);
        var reordered = await reorderedClient.ResolveTransactionRouteAsync(
            "orders",
            CancellationToken.None);
        var laterOnly = await laterOnlyClient.ResolveTransactionRouteAsync(
            "orders",
            CancellationToken.None);

        Assert.Equal(
            CosmosTransactionRouteResolutionStatus.Ready,
            complete.Status);
        Assert.Equal(West, complete.Route.Endpoint);
        Assert.Equal(
            CosmosTransactionRouteResolutionStatus.Ready,
            reordered.Status);
        Assert.Equal(West, reordered.Route.Endpoint);
        Assert.Equal(
            CosmosTransactionRouteResolutionStatus.Unavailable,
            laterOnly.Status);
        Assert.Contains(
            "West US",
            laterOnly.Error,
            StringComparison.Ordinal);
        Assert.Contains(
            "not executed in a later preferred region",
            laterOnly.Error,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fresh_single_write_clients_use_discovered_authoritative_endpoint()
    {
        var westConfiguredEndpoint = new Uri(
            "https://acct-topology-west-config.documents.azure.com/");
        var eastConfiguredEndpoint = new Uri(
            "https://acct-topology-east-config.documents.azure.com/");
        var westHandler = new SequenceHandler(
            (Func<HttpRequestMessage, bool>)(request =>
                request.RequestUri == westConfiguredEndpoint),
            (Func<HttpResponseMessage>)(() => JsonResponse(
                HttpStatusCode.OK,
                SingleWriteAccountJson(
                    "shared-topology-account",
                    West,
                    East))));
        var eastHandler = new SequenceHandler(
            (Func<HttpRequestMessage, bool>)(request =>
                request.RequestUri == eastConfiguredEndpoint),
            (Func<HttpResponseMessage>)(() => JsonResponse(
                HttpStatusCode.OK,
                SingleWriteAccountJson(
                    "shared-topology-account",
                    East,
                    West))));
        using var westHttp = new AzureHttpClient(
            westHandler,
            ownsHandler: false,
            NoRetryOptions());
        using var eastHttp = new AzureHttpClient(
            eastHandler,
            ownsHandler: false,
            NoRetryOptions());
        var westCredentials = Credentials(westConfiguredEndpoint.AbsoluteUri);
        var eastCredentials = Credentials(eastConfiguredEndpoint.AbsoluteUri);
        var westClient = new CosmosClient(
            westHttp,
            westCredentials,
            new MasterKeyCosmosAuthenticator(westCredentials.PrimaryKey));
        var eastClient = new CosmosClient(
            eastHttp,
            eastCredentials,
            new MasterKeyCosmosAuthenticator(eastCredentials.PrimaryKey));

        var west = await westClient.ResolveTransactionRouteAsync(
            "orders",
            CancellationToken.None);
        var east = await eastClient.ResolveTransactionRouteAsync(
            "orders",
            CancellationToken.None);

        Assert.Equal(
            CosmosTransactionRouteResolutionStatus.Ready,
            west.Status);
        Assert.Equal(West, west.Route.Endpoint);
        Assert.Equal(
            CosmosTransactionRouteResolutionStatus.Ready,
            east.Status);
        Assert.Equal(East, east.Route.Endpoint);
    }

    private static CosmosAccountInfo AccountInfo(bool multiWrite)
    {
        return new CosmosAccountInfo(
            Global,
            CosmosConsistencyLevel.Session,
            multiWrite,
            new[]
            {
                new CosmosAccountLocation("East US", East),
                new CosmosAccountLocation("West US", West),
                new CosmosAccountLocation("Central US", Central),
            },
            new[]
            {
                new CosmosAccountLocation("East US", East),
                new CosmosAccountLocation("West US", West),
                new CosmosAccountLocation("Central US", Central),
            });
    }

    private static CosmosCredentials Credentials(string endpoint = "https://acct.documents.azure.com/")
        => new()
        {
            Endpoint = endpoint,
            DatabaseName = "main",
            PrimaryKey = "MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=",
        };

    private static AzureHttpClientOptions NoRetryOptions()
        => new()
        {
            MaxAttempts = 1,
            CircuitBreaker = { Enabled = false },
        };

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string body)
        => new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    private static string AccountJson(Uri readableFirst, Uri writableFirst, bool multiWrite)
    {
        var readableName = readableFirst == West ? "West US" : "East US";
        var writableName = writableFirst == West ? "West US" : "East US";
        return $$"""
        {
          "id": "shared-transaction-account",
          "userConsistencyPolicy": { "defaultConsistencyLevel": "Session" },
          "enableMultipleWriteLocations": {{multiWrite.ToString().ToLowerInvariant()}},
          "readableLocations": [
            { "name": "{{readableName}}", "databaseAccountEndpoint": "{{readableFirst.AbsoluteUri}}" },
            { "name": "West US", "databaseAccountEndpoint": "{{West.AbsoluteUri}}" },
            { "name": "East US", "databaseAccountEndpoint": "{{East.AbsoluteUri}}" }
          ],
          "writableLocations": [
            { "name": "{{writableName}}", "databaseAccountEndpoint": "{{writableFirst.AbsoluteUri}}" }
          ]
        }
        """;
    }

    private static string MultiWriteAccountJson(
        params CosmosAccountLocation[] locations)
    {
        var locationJson = new StringBuilder();
        for (var index = 0; index < locations.Length; index++)
        {
            if (index > 0)
            {
                locationJson.Append(',');
            }
            locationJson.Append("{\"name\":\"")
                .Append(locations[index].Name)
                .Append("\",\"databaseAccountEndpoint\":\"")
                .Append(locations[index].Endpoint.AbsoluteUri)
                .Append("\"}");
        }

        return
            "{\"id\":\"shared-transaction-account\"," +
            "\"userConsistencyPolicy\":{\"defaultConsistencyLevel\":\"Session\"}," +
            "\"enableMultipleWriteLocations\":true," +
            "\"readableLocations\":[" + locationJson + "]," +
            "\"writableLocations\":[" + locationJson + "]}";
    }

    private static string SingleWriteAccountJson(
        string accountIdentity,
        Uri first,
        Uri second) =>
        $$"""
        {
          "id": "{{accountIdentity}}",
          "userConsistencyPolicy": { "defaultConsistencyLevel": "Session" },
          "enableMultipleWriteLocations": false,
          "readableLocations": [
            { "name": "First", "databaseAccountEndpoint": "{{first.AbsoluteUri}}" },
            { "name": "Second", "databaseAccountEndpoint": "{{second.AbsoluteUri}}" }
          ],
          "writableLocations": [
            { "name": "First", "databaseAccountEndpoint": "{{first.AbsoluteUri}}" }
          ]
        }
        """;

    private sealed class SequenceHandler : HttpMessageHandler
    {
        private readonly List<Route> _routes = new();
        public List<HttpRequestMessage> Requests { get; } = new();

        public SequenceHandler(params object[] routePairs)
        {
            for (int i = 0; i < routePairs.Length; i += 2)
            {
                _routes.Add(new Route(
                    (Func<HttpRequestMessage, bool>)routePairs[i],
                    (Func<HttpResponseMessage>)routePairs[i + 1]));
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(CloneForAssert(request));
            for (int i = 0; i < _routes.Count; i++)
            {
                if (_routes[i].Predicate(request))
                {
                    return Task.FromResult(_routes[i].Factory());
                }
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpRequestMessage CloneForAssert(HttpRequestMessage request)
            => new(request.Method, request.RequestUri);

        private sealed record Route(
            Func<HttpRequestMessage, bool> Predicate,
            Func<HttpResponseMessage> Factory);
    }
}
