using System;
using System.IO;
using System.Threading.Tasks;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Xunit;

namespace Aws2Azure.IntegrationTests.Fixtures;

/// <summary>
/// xUnit collection-level fixture that boots a single Azurite container per
/// run and exposes the Blob endpoint + Azurite's well-known dev credentials.
/// Skipped automatically (via guard checks inside tests) when Docker is not
/// reachable, e.g. in some local sandboxes.
/// </summary>
public sealed class AzuriteFixture : IAsyncLifetime
{
    public const string AccountName = "devstoreaccount1";
    public const string AccountKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    private IContainer? _container;
    public bool DockerAvailable { get; private set; }
    public string? BlobEndpoint { get; private set; }

    public async Task InitializeAsync()
    {
        try
        {
            _container = new ContainerBuilder()
                .WithImage("mcr.microsoft.com/azure-storage/azurite:latest")
                .WithName("aws2azure-it-azurite-" + Guid.NewGuid().ToString("N")[..8])
                .WithPortBinding(10000, true)
                .WithCommand("azurite-blob", "--blobHost", "0.0.0.0", "--skipApiVersionCheck")
                .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(10000))
                .Build();

            await _container.StartAsync();
            var port = _container.GetMappedPublicPort(10000);
            BlobEndpoint = $"http://{_container.Hostname}:{port}/{AccountName}";
            DockerAvailable = true;
        }
        catch (Exception ex) when (
            ex is ArgumentException ||
            ex is InvalidOperationException ||
            ex.GetType().FullName?.StartsWith("Docker", StringComparison.Ordinal) == true ||
            ex.Message.Contains("docker", StringComparison.OrdinalIgnoreCase))
        {
            DockerAvailable = false;
        }
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}

[CollectionDefinition(Name)]
public sealed class AzuriteCollection : ICollectionFixture<AzuriteFixture>
{
    public const string Name = "azurite";
}
