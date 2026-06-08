using System.IO;
using System.Net;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Aws2Azure.Conformance.S3;

/// <summary>
/// Tier-2 (LocalStack differential, Docker-gated, nightly/labeled, non-blocking)
/// fixture. Boots three things in one collection:
/// <list type="number">
///   <item>Azurite (Testcontainers) — the real Azure Blob backend;</item>
///   <item>the proxy in-process (<see cref="WebApplicationFactory{T}"/>) configured
///   against that Azurite, so a request that misses the backend exercises the
///   proxy's Azure→S3 error translation (<c>S3ErrorMapping</c>);</item>
///   <item>LocalStack S3 (Testcontainers) — an authoritative real-S3
///   implementation used as the differential reference.</item>
/// </list>
/// The same signed request is sent to both the proxy and LocalStack; the two
/// responses are canonicalized and diffed so any unfaithful divergence in the
/// proxy's backend-error mapping surfaces (unless documented in a gap doc).
/// Skipped automatically when Docker is not reachable.
/// </summary>
public sealed class S3BackendDifferentialFixture : IAsyncLifetime
{
    private const string AzuriteImage = "mcr.microsoft.com/azure-storage/azurite:latest";
    private const string LocalStackImage = "localstack/localstack:3";

    private const string AzuriteAccountName = "devstoreaccount1";
    private const string AzuriteAccountKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    private IContainer? _azurite;
    private IContainer? _localStack;
    private WebApplicationFactory<Program>? _factory;
    private string? _configFile;

    public bool DockerAvailable { get; private set; }

    /// <summary>Routes to the in-process proxy; Host = <c>s3.us-east-1.amazonaws.com</c>.</summary>
    public HttpClient ProxyClient { get; private set; } = default!;

    /// <summary>Talks directly to LocalStack S3 (path-style).</summary>
    public HttpClient LocalStackClient { get; private set; } = default!;

    /// <summary>Absolute base of the proxy host (for signing the proxy request).</summary>
    public Uri ProxyBaseUri { get; } = new("http://s3.us-east-1.amazonaws.com/");

    /// <summary>Absolute base of the LocalStack S3 endpoint (for signing the LocalStack request).</summary>
    public Uri LocalStackBaseUri { get; private set; } = default!;

    // The proxy authenticates against these; LocalStack ignores the signature
    // entirely, so the same credentials produce a valid-auth request at both.
    public string AccessKeyId => "DEVSTOREACCOUNT1";
    public string Secret => AzuriteAccountKey;

    public async Task InitializeAsync()
    {
        string blobEndpoint;
        try
        {
            _azurite = new ContainerBuilder()
                .WithImage(AzuriteImage)
                .WithName("aws2azure-conf-azurite-" + Guid.NewGuid().ToString("N")[..8])
                .WithPortBinding(10000, true)
                .WithCommand("azurite-blob", "--blobHost", "0.0.0.0", "--skipApiVersionCheck")
                .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(10000))
                .Build();

            _localStack = new ContainerBuilder()
                .WithImage(LocalStackImage)
                .WithName("aws2azure-conf-localstack-" + Guid.NewGuid().ToString("N")[..8])
                .WithEnvironment("SERVICES", "s3")
                .WithEnvironment("EAGER_SERVICE_LOADING", "1")
                .WithPortBinding(4566, true)
                .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Ready."))
                .Build();

            await _azurite.StartAsync();
            await _localStack.StartAsync();

            var blobPort = _azurite.GetMappedPublicPort(10000);
            blobEndpoint = $"http://{_azurite.Hostname}:{blobPort}/{AzuriteAccountName}";

            var lsPort = _localStack.GetMappedPublicPort(4566);
            LocalStackBaseUri = new Uri($"http://{_localStack.Hostname}:{lsPort}/");
            DockerAvailable = true;
        }
        catch
        {
            DockerAvailable = false;
            return;
        }

        _configFile = Path.Combine(Path.GetTempPath(),
            "aws2azure-conf-backend-" + Guid.NewGuid().ToString("N") + ".json");
        var config = $$"""
        {
          "services": { "s3": { "enabled": true } },
          "credentials": [
            {
              "awsAccessKeyId": "{{AccessKeyId}}",
              "awsSecretAccessKey": "{{Secret}}",
              "azure": {
                "blob": {
                  "accountName": "{{AzuriteAccountName}}",
                  "accountKey":  "{{AzuriteAccountKey}}",
                  "serviceEndpoint": "{{blobEndpoint}}"
                }
              }
            }
          ]
        }
        """;
        await File.WriteAllTextAsync(_configFile, config);
        Environment.SetEnvironmentVariable("AWS2AZURE_CONFIG_FILE", _configFile);

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b.UseEnvironment("Testing"));
        ProxyClient = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = ProxyBaseUri,
        });

        LocalStackClient = new HttpClient { BaseAddress = LocalStackBaseUri };
    }

    /// <summary>
    /// Provisions the same bucket on both backends (through the proxy → Azurite,
    /// and directly on LocalStack) so a subsequent missing-key GET maps to
    /// <c>NoSuchKey</c> on both rather than <c>NoSuchBucket</c>.
    /// </summary>
    public async Task CreateBucketOnBothAsync(string bucket)
    {
        await PutBucketAsync(ProxyClient, ProxyBaseUri, bucket);
        await PutBucketAsync(LocalStackClient, LocalStackBaseUri, bucket);
    }

    private async Task PutBucketAsync(HttpClient client, Uri baseUri, string bucket)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Put, new Uri(baseUri, $"/{bucket}"));
        ConformanceSigV4Signer.SignHeader(
            request, Array.Empty<byte>(), AccessKeyId, Secret);
        using var response = await client.SendAsync(request);
        if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.NoContent
            or HttpStatusCode.Conflict))
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException(
                $"Bucket provisioning failed at {baseUri.Host}: {(int)response.StatusCode} {body}");
        }
    }

    public async Task DisposeAsync()
    {
        ProxyClient?.Dispose();
        LocalStackClient?.Dispose();
        _factory?.Dispose();
        if (_localStack is not null)
        {
            await _localStack.DisposeAsync();
        }
        if (_azurite is not null)
        {
            await _azurite.DisposeAsync();
        }
        if (_configFile is not null)
        {
            try { File.Delete(_configFile); } catch { /* best-effort */ }
        }
    }
}

[CollectionDefinition(Name)]
public sealed class S3BackendDifferentialCollection
    : ICollectionFixture<S3BackendDifferentialFixture>
{
    public const string Name = "s3-backend-differential";
}
