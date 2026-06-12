using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Aws2Azure.Core.Azure;

namespace Aws2Azure.UnitTests.Azure;

public class WorkloadIdentityTokenSourceTests
{
    [Fact]
    public async Task GetTokenAsync_PostsFederatedAssertionFromFile()
    {
        var tokenFile = CreateTokenFile("jwt-assertion-1");
        try
        {
            var handler = new CapturingScriptedHandler();
            var http = new AzureHttpClient(handler, ownsHandler: true);
            var source = new WorkloadIdentityTokenSource(
                http,
                "tenant",
                "client-id",
                tokenFile,
                authority: new Uri("https://login.test/"));

            handler.Enqueue(MakeToken("access-token", expiresIn: 3600));

            var token = await source.GetTokenAsync("https://storage.azure.com/.default");

            Assert.Equal("access-token", token);
            Assert.Single(handler.RequestBodies);
            var form = DecodeForm(handler.RequestBodies[0]);
            Assert.Equal("client_credentials", form["grant_type"]);
            Assert.Equal("client-id", form["client_id"]);
            Assert.Equal("https://storage.azure.com/.default", form["scope"]);
            Assert.Equal("urn:ietf:params:oauth:client-assertion-type:jwt-bearer", form["client_assertion_type"]);
            Assert.Equal("jwt-assertion-1", form["client_assertion"]);
        }
        finally
        {
            DeleteTokenFile(tokenFile);
        }
    }

    [Fact]
    public async Task GetTokenAsync_ReReadsFederatedTokenFileOnRefresh()
    {
        var fakeClock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var tokenFile = CreateTokenFile("jwt-assertion-1");
        try
        {
            var handler = new CapturingScriptedHandler();
            var http = new AzureHttpClient(handler, ownsHandler: true);
            var source = new WorkloadIdentityTokenSource(
                http,
                "tenant",
                "client-id",
                tokenFile,
                authority: new Uri("https://login.test/"),
                clock: fakeClock);

            handler.Enqueue(MakeToken("token-1", expiresIn: 3600));
            Assert.Equal("token-1", await source.GetTokenAsync("https://storage.azure.com/.default"));

            await File.WriteAllTextAsync(tokenFile, "jwt-assertion-2");
            fakeClock.Advance(TimeSpan.FromSeconds(3540));
            handler.Enqueue(MakeToken("token-2", expiresIn: 3600));

            Assert.Equal("token-2", await source.GetTokenAsync("https://storage.azure.com/.default"));

            Assert.Equal(2, handler.RequestBodies.Count);
            var first = DecodeForm(handler.RequestBodies[0]);
            var second = DecodeForm(handler.RequestBodies[1]);
            Assert.Equal("jwt-assertion-1", first["client_assertion"]);
            Assert.Equal("jwt-assertion-2", second["client_assertion"]);
        }
        finally
        {
            DeleteTokenFile(tokenFile);
        }
    }

    [Fact]
    public async Task GetTokenAsync_CachesWithinSafetyWindow()
    {
        var fakeClock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var tokenFile = CreateTokenFile("jwt-assertion");
        try
        {
            var handler = new CapturingScriptedHandler();
            var http = new AzureHttpClient(handler, ownsHandler: true);
            var source = new WorkloadIdentityTokenSource(
                http,
                "tenant",
                "client-id",
                tokenFile,
                authority: new Uri("https://login.test/"),
                clock: fakeClock);

            handler.Enqueue(MakeToken("token-1", expiresIn: 3600));
            var first = await source.GetTokenAsync("https://storage.azure.com/.default");
            fakeClock.Advance(TimeSpan.FromMinutes(30));
            var second = await source.GetTokenAsync("https://storage.azure.com/.default");

            Assert.Equal("token-1", first);
            Assert.Equal("token-1", second);
            Assert.Equal(1, handler.CallCount);
        }
        finally
        {
            DeleteTokenFile(tokenFile);
        }
    }

    [Fact]
    public async Task GetTokenAsync_NonSuccessThrowsStatusPreservingException()
    {
        var tokenFile = CreateTokenFile("jwt-assertion");
        try
        {
            var handler = new CapturingScriptedHandler();
            var http = new AzureHttpClient(handler, ownsHandler: true);
            var source = new WorkloadIdentityTokenSource(
                http,
                "tenant",
                "client-id",
                tokenFile,
                authority: new Uri("https://login.test/"));

            handler.Enqueue(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("{\"error\":\"throttled\"}")
            });

            var ex = await Assert.ThrowsAsync<EntraIdTokenException>(() =>
                source.GetTokenAsync("https://storage.azure.com/.default").AsTask());

            Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);
            Assert.Equal(HttpStatusCode.TooManyRequests, ex.BackendStatus);
        }
        finally
        {
            DeleteTokenFile(tokenFile);
        }
    }

    [Fact]
    public async Task GetTokenAsync_MissingFederatedTokenFileThrowsInvalidOperationException()
    {
        var missingPath = Path.Combine(TestFileDirectory, "missing-" + Guid.NewGuid().ToString("N") + ".txt");
        var handler = new CapturingScriptedHandler();
        var http = new AzureHttpClient(handler, ownsHandler: true);
        var source = new WorkloadIdentityTokenSource(
            http,
            "tenant",
            "client-id",
            missingPath,
            authority: new Uri("https://login.test/"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            source.GetTokenAsync("https://storage.azure.com/.default").AsTask());

        Assert.Contains("could not be read", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public void FromEnvironment_MissingRequiredValuesListsVariables()
    {
        using var http = new AzureHttpClient(new CapturingScriptedHandler(), ownsHandler: true);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            WorkloadIdentityTokenSource.FromEnvironmentValues(http, null, " ", string.Empty, null));

        Assert.Contains("AZURE_TENANT_ID", ex.Message, StringComparison.Ordinal);
        Assert.Contains("AZURE_CLIENT_ID", ex.Message, StringComparison.Ordinal);
        Assert.Contains("AZURE_FEDERATED_TOKEN_FILE", ex.Message, StringComparison.Ordinal);
    }

    private static string TestFileDirectory => Path.Combine(AppContext.BaseDirectory, "workload-identity-token-tests");

    private static string CreateTokenFile(string contents)
    {
        Directory.CreateDirectory(TestFileDirectory);
        var path = Path.Combine(TestFileDirectory, "token-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(path, contents);
        return path;
    }

    private static void DeleteTokenFile(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static HttpResponseMessage MakeToken(string token, int expiresIn)
    {
        var payload = "{\"access_token\":\"" + token + "\",\"token_type\":\"Bearer\",\"expires_in\":" + expiresIn + "}";
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
    }

    private static Dictionary<string, string> DecodeForm(string body)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var parts = body.Split('&', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            var pair = parts[i];
            var separator = pair.IndexOf('=');
            var name = separator < 0 ? pair : pair[..separator];
            var value = separator < 0 ? string.Empty : pair[(separator + 1)..];
            result[DecodeFormComponent(name)] = DecodeFormComponent(value);
        }
        return result;
    }

    private static string DecodeFormComponent(string value)
        => Uri.UnescapeDataString(value.Replace("+", " ", StringComparison.Ordinal));

    private sealed class CapturingScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _queue = new();
        private readonly List<string> _requestBodies = new();
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);
        public IReadOnlyList<string> RequestBodies => _requestBodies;
        public void Enqueue(HttpResponseMessage response) => _queue.Enqueue(response);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            _requestBodies.Add(request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            return _queue.Dequeue();
        }
    }
}
