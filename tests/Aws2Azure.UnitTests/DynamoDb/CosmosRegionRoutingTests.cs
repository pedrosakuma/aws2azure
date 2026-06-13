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
    public void Failover_statuses_match_cosmos_region_triggers()
    {
        using var unavailable = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        Assert.True(CosmosRegionRouting.IsFailoverStatus(unavailable, isWrite: false));

        using var timeout = new HttpResponseMessage(HttpStatusCode.RequestTimeout);
        Assert.True(CosmosRegionRouting.IsFailoverStatus(timeout, isWrite: true));

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
