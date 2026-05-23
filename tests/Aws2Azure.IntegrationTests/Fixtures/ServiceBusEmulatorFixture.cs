using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;
using DotNet.Testcontainers.Networks;
using Xunit;

namespace Aws2Azure.IntegrationTests.Fixtures;

/// <summary>
/// Boots the Azure Service Bus emulator plus its SQL Edge sidecar once per
/// integration-test run. The emulator image is built from the repository's
/// baked-config Dockerfile so tests and local docker-compose use the same
/// namespace/queue baseline without fragile bind mounts.
/// </summary>
public sealed class ServiceBusEmulatorFixture : IAsyncLifetime
{
    public const string Namespace = "sbemulatorns";
    public const string SasKeyName = "RootManageSharedAccessKey";
    public const string WellKnownSasKey = "SAS_KEY_VALUE";
    public const string StandardQueue = "sqs-std";
    public const string FifoQueue = "sqs-fifo.fifo";
    public const string DlqSourceQueue = "sqs-dlq-source";
    public const string DlqTargetQueue = "sqs-dlq-target";

    private const string EmulatorImageTag = "aws2azure-it/sb-emulator:4";
    private const string SqlEdgePassword = "Aws2AzureEmulator!42";
    private const string SqlEdgeNetworkAlias = "sqledge";
    private const ushort EmulatorAmqpPort = 5672;
    private const ushort EmulatorHttpPort = 5300;
    private const ushort SqlEdgePort = 1433;

    private INetwork? _network;
    private IContainer? _sqlEdge;
    private IContainer? _emulator;
    private IFutureDockerImage? _emulatorImage;

    public bool DockerAvailable { get; private set; }
    public string AmqpHost { get; private set; } = "localhost";
    public int AmqpPort { get; private set; }
    public int HttpPort { get; private set; }

    public async Task InitializeAsync()
    {
        try
        {
            var dockerfileDir = Path.Combine(AppContext.BaseDirectory, "deploy", "emulators", "servicebus");
            if (!File.Exists(Path.Combine(dockerfileDir, "Dockerfile")) ||
                !File.Exists(Path.Combine(dockerfileDir, "Config.json")))
            {
                throw new FileNotFoundException(
                    $"Service Bus emulator assets not found under '{dockerfileDir}'. " +
                    "Ensure Aws2Azure.IntegrationTests.csproj copies deploy/emulators/servicebus/* to the build output.");
            }

            _emulatorImage = new ImageFromDockerfileBuilder()
                .WithName(EmulatorImageTag)
                .WithDockerfileDirectory(dockerfileDir)
                .WithDockerfile("Dockerfile")
                .WithCleanUp(false)
                .Build();
            await _emulatorImage.CreateAsync().ConfigureAwait(false);

            _network = new NetworkBuilder()
                .WithName("aws2azure-it-sbnet-" + Guid.NewGuid().ToString("N")[..8])
                .Build();
            await _network.CreateAsync().ConfigureAwait(false);

            _sqlEdge = new ContainerBuilder()
                .WithImage("mcr.microsoft.com/azure-sql-edge:latest")
                .WithName("aws2azure-it-sqledge-" + Guid.NewGuid().ToString("N")[..8])
                .WithNetwork(_network)
                .WithNetworkAliases(SqlEdgeNetworkAlias)
                .WithEnvironment("ACCEPT_EULA", "Y")
                .WithEnvironment("MSSQL_SA_PASSWORD", SqlEdgePassword)
                .WithPortBinding(SqlEdgePort, true)
                .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(SqlEdgePort))
                .Build();
            await _sqlEdge.StartAsync().ConfigureAwait(false);

            _emulator = new ContainerBuilder()
                .WithImage(_emulatorImage)
                .WithName("aws2azure-it-sbemu-" + Guid.NewGuid().ToString("N")[..8])
                .WithNetwork(_network)
                .WithEnvironment("ACCEPT_EULA", "Y")
                .WithEnvironment("MSSQL_SA_PASSWORD", SqlEdgePassword)
                .WithEnvironment("SQL_SERVER", SqlEdgeNetworkAlias)
                .WithEnvironment("SQL_WAIT_INTERVAL", "30")
                .WithPortBinding(EmulatorAmqpPort, true)
                .WithPortBinding(EmulatorHttpPort, true)
                .Build();
            await _emulator.StartAsync().ConfigureAwait(false);

            AmqpHost = _emulator.Hostname;
            AmqpPort = _emulator.GetMappedPublicPort(EmulatorAmqpPort);
            HttpPort = _emulator.GetMappedPublicPort(EmulatorHttpPort);

            await WaitForHealthAsync(AmqpHost, HttpPort, TimeSpan.FromMinutes(2)).ConfigureAwait(false);
            DockerAvailable = true;
        }
        catch (Exception ex) when (IsDockerUnavailable(ex))
        {
            DockerAvailable = false;
        }
    }

    private static bool IsDockerUnavailable(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            var typeName = current.GetType().FullName ?? string.Empty;
            if (typeName.StartsWith("Docker", StringComparison.Ordinal)) return true;

            var msg = current.Message ?? string.Empty;
            if (msg.Contains("docker daemon", StringComparison.OrdinalIgnoreCase)) return true;
            if (msg.Contains("Cannot connect to the Docker daemon", StringComparison.OrdinalIgnoreCase)) return true;
            if (msg.Contains("no such host", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    public async Task DisposeAsync()
    {
        if (_emulator is not null) await _emulator.DisposeAsync().ConfigureAwait(false);
        if (_sqlEdge is not null) await _sqlEdge.DisposeAsync().ConfigureAwait(false);
        if (_network is not null) await _network.DisposeAsync().ConfigureAwait(false);
    }

    private static async Task WaitForHealthAsync(string host, int port, TimeSpan timeout)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var deadline = DateTime.UtcNow + timeout;
        var uri = new Uri($"http://{host}:{port}/health", UriKind.Absolute);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var response = await http.GetAsync(uri).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch
            {
                // The emulator starts publishing the port before the health
                // endpoint is live; keep polling until the timeout elapses.
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
        }

        throw new TimeoutException($"SB emulator did not become healthy at '{uri}' within {timeout.TotalSeconds:N0}s.");
    }
}

[CollectionDefinition(Name)]
public sealed class ServiceBusEmulatorCollection : ICollectionFixture<ServiceBusEmulatorFixture>
{
    public const string Name = "servicebus-emulator";
}
