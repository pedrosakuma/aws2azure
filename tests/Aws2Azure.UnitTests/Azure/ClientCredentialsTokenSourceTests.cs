using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Aws2Azure.Core.Azure;

namespace Aws2Azure.UnitTests.Azure;

public sealed class ClientCredentialsTokenSourceTests
{
    [Fact]
    public async Task GetTokenAsync_UnauthorizedThenSuccess_RetriesWithoutSleepingForRealTime()
    {
        var delayCalls = 0;
        TimeSpan? observedDelay = null;
        var handler = new ScriptedHandler();
        var http = new AzureHttpClient(handler, ownsHandler: true);
        var source = new ClientCredentialsTokenSource(
            http,
            "tenant",
            "client-id",
            "secret",
            authority: new Uri("https://login.test/"),
            clock: null,
            delayAsync: (delay, _) =>
            {
                delayCalls++;
                observedDelay = delay;
                return ValueTask.CompletedTask;
            });

        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("{\"error\":\"transient\"}", Encoding.UTF8, "application/json")
        });
        handler.Enqueue(MakeToken("access-token", expiresIn: 3600));

        var started = Stopwatch.StartNew();
        var token = await source.GetTokenAsync("https://storage.azure.com/.default");
        started.Stop();

        Assert.Equal("access-token", token);
        Assert.Equal(2, handler.CallCount);
        Assert.Equal(1, delayCalls);
        Assert.Equal(EntraIdTokenEndpointRetry.UnauthorizedRetryDelay, observedDelay);
        Assert.True(
            started.Elapsed < TimeSpan.FromMilliseconds(500),
            "Injected retry delay should keep the test below the real 750ms sleep budget.");
    }

    [Fact]
    public async Task GetTokenAsync_UnauthorizedAfterRetryBudget_ThrowsTerminalException()
    {
        var delayCalls = 0;
        var handler = new ScriptedHandler();
        var http = new AzureHttpClient(handler, ownsHandler: true);
        var source = new ClientCredentialsTokenSource(
            http,
            "tenant",
            "client-id",
            "secret",
            authority: new Uri("https://login.test/"),
            clock: null,
            delayAsync: (_, _) =>
            {
                delayCalls++;
                return ValueTask.CompletedTask;
            });

        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("{\"error\":\"first\"}", Encoding.UTF8, "application/json")
        });
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("{\"error\":\"second\"}", Encoding.UTF8, "application/json")
        });

        var ex = await Assert.ThrowsAsync<EntraIdTokenException>(() =>
            source.GetTokenAsync("https://storage.azure.com/.default").AsTask());

        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, ex.BackendStatus);
        Assert.Equal(2, handler.CallCount);
        Assert.Equal(1, delayCalls);
    }

    [Fact]
    public async Task GetTokenAsync_BadRequest_IsNotRetried()
    {
        var delayCalls = 0;
        var handler = new ScriptedHandler();
        var http = new AzureHttpClient(handler, ownsHandler: true);
        var source = new ClientCredentialsTokenSource(
            http,
            "tenant",
            "client-id",
            "secret",
            authority: new Uri("https://login.test/"),
            clock: null,
            delayAsync: (_, _) =>
            {
                delayCalls++;
                return ValueTask.CompletedTask;
            });

        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("{\"error\":\"invalid_request\"}", Encoding.UTF8, "application/json")
        });

        var ex = await Assert.ThrowsAsync<EntraIdTokenException>(() =>
            source.GetTokenAsync("https://storage.azure.com/.default").AsTask());

        Assert.Equal(HttpStatusCode.BadRequest, ex.StatusCode);
        Assert.Equal(1, handler.CallCount);
        Assert.Equal(0, delayCalls);
    }

    [Fact]
    public async Task GetTokenAsync_FirstAttemptSuccess_DoesNotRetryOrDelay()
    {
        var delayCalls = 0;
        var handler = new ScriptedHandler();
        var http = new AzureHttpClient(handler, ownsHandler: true);
        var source = new ClientCredentialsTokenSource(
            http,
            "tenant",
            "client-id",
            "secret",
            authority: new Uri("https://login.test/"),
            clock: null,
            delayAsync: (_, _) =>
            {
                delayCalls++;
                return ValueTask.CompletedTask;
            });

        handler.Enqueue(MakeToken("access-token", expiresIn: 3600));

        var token = await source.GetTokenAsync("https://storage.azure.com/.default");

        Assert.Equal("access-token", token);
        Assert.Equal(1, handler.CallCount);
        Assert.Equal(0, delayCalls);
    }

    private static HttpResponseMessage MakeToken(string token, int expiresIn)
    {
        var payload = "{\"access_token\":\"" + token + "\",\"token_type\":\"Bearer\",\"expires_in\":" + expiresIn + "}";
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _queue = new();
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public void Enqueue(HttpResponseMessage response) => _queue.Enqueue(response);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            return Task.FromResult(_queue.Dequeue());
        }
    }
}
