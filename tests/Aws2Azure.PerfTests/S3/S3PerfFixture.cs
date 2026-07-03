using Amazon.S3;
using Aws2Azure.IntegrationTests.Fixtures;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Xunit;

namespace Aws2Azure.PerfTests;

public sealed class S3PerfFixture : IAsyncLifetime
{
    public string AccessKeyId => "DEVSTOREACCOUNT1";
    public string Secret => AzuriteFixture.AccountKey;

    private IContainer? _container;
    private readonly PerfProxyProcess _proxy = new();

    public bool Ready { get; private set; }
    public string? SkipReason { get; private set; }
    public string ServiceUrl => _proxy.ServiceUrlForHost("s3");
    public string ProxyOutput => _proxy.Output;
    public ProxyMemoryProbe CreateMemoryProbe() => _proxy.CreateMemoryProbe();
    public string Bucket { get; } = "perf-bkt-" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// Azurite blob endpoint exposed for the Azure SDK direct-baseline
    /// scenarios — the proxy is left idle for those measurements.
    /// </summary>
    public string BlobEndpoint { get; private set; } = string.Empty;

    public AmazonS3Client CreateClient() => new(
        AccessKeyId,
        Secret,
        new AmazonS3Config
        {
            ServiceURL = ServiceUrl,
            AuthenticationRegion = "us-east-1",
            UseHttp = true,
            ForcePathStyle = true,
        });

    public async Task InitializeAsync()
    {
        if (!PerfGate.Enabled)
        {
            SkipReason = "AWS2AZURE_PERF=1 not set.";
            return;
        }

        try
        {
            _container = new ContainerBuilder()
                .WithImage("mcr.microsoft.com/azure-storage/azurite:latest")
                .WithName("aws2azure-perf-azurite-" + Guid.NewGuid().ToString("N")[..8])
                .WithPortBinding(10000, true)
                .WithCommand("azurite-blob", "--blobHost", "0.0.0.0", "--skipApiVersionCheck")
                .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(10000))
                .Build();
            await _container.StartAsync().ConfigureAwait(false);
            var blobPort = _container.GetMappedPublicPort(10000);
            var blobEndpoint = $"http://{_container.Hostname}:{blobPort}/{AzuriteFixture.AccountName}";
            BlobEndpoint = blobEndpoint;

            var config = $$"""
                {
                  "services": { "s3": { "enabled": true } },
                  "bindings": [
                    {
                      "aws": {
                        "accessKeyId": "{{AccessKeyId}}",
                        "secretAccessKey": "{{Secret}}"
                      },
                      "azure": {
                        "s3": {
                          "kind": "blob",
                          "target": {
                            "accountName": "{{AzuriteFixture.AccountName}}",
                            "endpoint": "{{blobEndpoint}}"
                          },
                          "auth": {
                            "mode": "sharedKey",
                            "key": "{{AzuriteFixture.AccountKey}}"
                          }
                        }
                      }
                    }
                  ]
                }
                """;
            await _proxy.StartAsync(config, TimeSpan.FromMinutes(2)).ConfigureAwait(false);

            using var s3 = CreateClient();
            await s3.PutBucketAsync(Bucket).ConfigureAwait(false);
            Ready = true;
        }
        catch (Exception ex)
        {
            SkipReason = $"Fixture init failed: {ex.Message}";
        }
    }

    public async Task DisposeAsync()
    {
        await _proxy.DisposeAsync().ConfigureAwait(false);
        if (_container is not null)
        {
            await _container.DisposeAsync().ConfigureAwait(false);
        }
    }
}

[CollectionDefinition(Name)]
public sealed class S3PerfCollection : ICollectionFixture<S3PerfFixture>
{
    public const string Name = "s3-perf";
}
