using System.IO;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Aws2Azure.IntegrationTests.S3;

/// <summary>
/// Boots Azurite (Testcontainers) + the proxy (in-process WebApplicationFactory)
/// configured with BlobCredentials pointing at the running container. The
/// fixture owns its own Azurite instance so that it can write the proxy's
/// appsettings.json before the host starts.
/// </summary>
public sealed class S3IntegrationFixture : IAsyncLifetime
{
    private const string ContainerImage = "mcr.microsoft.com/azure-storage/azurite:latest";

    private IContainer? _container;
    private WebApplicationFactory<Program>? _factory;
    private string? _configFile;

    public bool DockerAvailable { get; private set; }
    public string BlobEndpoint { get; private set; } = string.Empty;
    public HttpClient Client { get; private set; } = default!;

    public string AccessKeyId => "DEVSTOREACCOUNT1";
    public string Secret => Fixtures.AzuriteFixture.AccountKey;

    public async Task InitializeAsync()
    {
        try
        {
            _container = new ContainerBuilder()
                .WithImage(ContainerImage)
                .WithName("aws2azure-s3-it-" + Guid.NewGuid().ToString("N")[..8])
                .WithPortBinding(10000, true)
                .WithCommand("azurite-blob", "--blobHost", "0.0.0.0", "--skipApiVersionCheck")
                .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(10000))
                .Build();

            await _container.StartAsync();
            var port = _container.GetMappedPublicPort(10000);
            BlobEndpoint = $"http://{_container.Hostname}:{port}/{Fixtures.AzuriteFixture.AccountName}";
            DockerAvailable = true;
        }
        catch (Exception ex) when (IsDockerUnavailable(ex))
        {
            if (_container is not null)
            {
                await _container.DisposeAsync();
                _container = null;
            }
            DockerAvailable = false;
            return;
        }
        catch (Exception)
        {
            if (_container is not null)
            {
                await _container.DisposeAsync();
                _container = null;
            }
            throw;
        }

        _configFile = Path.Combine(Path.GetTempPath(),
            "aws2azure-s3-it-" + Guid.NewGuid().ToString("N") + ".json");
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
                    "accountName": "{{Fixtures.AzuriteFixture.AccountName}}",
                    "endpoint": "{{BlobEndpoint}}"
                  },
                  "auth": {
                    "mode": "sharedKey",
                    "key":  "{{Fixtures.AzuriteFixture.AccountKey}}"
                  }
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
        Client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("http://s3.us-east-1.amazonaws.com/"),
        });
    }

    public async Task DisposeAsync()
    {
        _factory?.Dispose();
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
        if (_configFile is not null)
        {
            try { File.Delete(_configFile); } catch { /* best-effort */ }
        }
    }

    private static bool IsDockerUnavailable(Exception ex)
        => ex is ArgumentException
           || ex is InvalidOperationException
           || ex.GetType().FullName?.StartsWith("Docker", StringComparison.Ordinal) == true
           || ex.Message.Contains("docker", StringComparison.OrdinalIgnoreCase);
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class S3IntegrationCollection : ICollectionFixture<S3IntegrationFixture>
{
    public const string Name = "s3-integration";
}
