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
/// xUnit collection-level fixture that boots the Azure Service Bus
/// emulator (plus its SQL Edge sidecar) once per integration-test run.
/// Mirrors <see cref="AzuriteFixture"/>: the fixture self-skips
/// (<see cref="DockerAvailable"/>=false) when Docker isn't reachable so
/// fork PRs and local sandboxes that lack Docker don't break the build.
///
/// <para>The emulator requires <b>two</b> containers wired on the same
/// bridge network — the emulator container plus an Azure SQL Edge sidecar
/// it uses for entity storage. Instead of bind-mounting the emulator's
/// <c>Config.json</c> from the host (fragile under WSL/Docker Desktop:
/// path translation, file-lock and uid/gid surprises) we build a small
/// derived image inline (see <c>ServiceBusEmulator.Dockerfile</c>) that
/// <c>COPY</c>s the config into <c>/ServiceBus_Emulator/ConfigFiles/Config.json</c>.
/// The image build is cached by tag, so subsequent runs reuse the layer.</para>
///
/// <para>Our config declares four queues sized for the SQS retrofit:</para>
/// <list type="bullet">
///   <item><b>sqs-std</b> — vanilla standard queue (LockDuration=30s,
///         MaxDeliveryCount=3).</item>
///   <item><b>sqs-fifo.fifo</b> — RequiresSession + RequiresDuplicateDetection
///         so the proxy's FIFO interlock can be exercised end-to-end. Name
///         carries the SQS-style <c>.fifo</c> suffix because the proxy's
///         <c>SqsTransportResolver</c> + <c>SendMessage</c> validation
///         keys off it.</item>
///   <item><b>sqs-dlq-source</b> + <b>sqs-dlq-target</b> — a forward-DLQ
///         pair with MaxDeliveryCount=2 and TTL=10s so DLQ smokes can
///         provoke dead-lettering quickly (Slice 7).</item>
/// </list>
///
/// <para>The emulator publishes a deterministic SAS key
/// (<see cref="WellKnownSasKey"/>) that the official sample apps embed
/// verbatim; we ship it as the credential the integration tests hand
/// to the proxy. The Service Bus emulator runs CBS but does not
/// validate token signatures, so a self-issued SAS token signed with
/// this key is accepted.</para>
///
/// <para>Readiness is gated host-side by polling the container's stdout
/// for the emulator's "Successfully Up" log line. A naive TCP probe to
/// the mapped port is not reliable because Docker Desktop's port proxy
/// opens the listen socket as soon as the port is published, accepting
/// connections before the AMQP listener inside the container is alive.</para>
///
/// <para>The TCP endpoint (<see cref="AmqpHost"/>+<see cref="AmqpPort"/>)
/// is the address tests should hand to the proxy under test. The emulator
/// image exposes only AMQP 5672 — there is no HTTP management surface.</para>
/// </summary>
public sealed class ServiceBusEmulatorFixture : IAsyncLifetime
{
    public const string Namespace = "sbemulatorns";
    public const string SasKeyName = "RootManageSharedAccessKey";

    /// <summary>
    /// Well-known SAS key used by the SB emulator samples (the literal
    /// placeholder <c>SAS_KEY_VALUE</c> the published samples embed).
    /// The emulator does not validate the signed CBS token, so any
    /// stable string works — keeping the sample value makes our setup
    /// recognisable to anyone familiar with the SB emulator docs.
    /// </summary>
    public const string WellKnownSasKey = "SAS_KEY_VALUE";

    /// <summary>Standard-queue SB entity name (mirrors SQS-side name).</summary>
    public const string StandardQueue = "sqs-std";

    /// <summary>FIFO-flavoured SB entity name (RequiresSession=true).</summary>
    public const string FifoQueue = "sqs-fifo.fifo";

    /// <summary>DLQ source — small TTL + MaxDeliveryCount=2 for quick dead-letter.</summary>
    public const string DlqSourceQueue = "sqs-dlq-source";

    /// <summary>Forward-DLQ target receiving dead-lettered messages.</summary>
    public const string DlqTargetQueue = "sqs-dlq-target";

    /// <summary>
    /// Image tag for the derived emulator image. Stable so the daemon's
    /// build cache survives across test runs; bumped manually when the
    /// Config.json or Dockerfile materially changes.
    /// </summary>
    private const string EmulatorImageTag = "aws2azure-it/sb-emulator:3";

    /// <summary>
    /// SA password handed to both containers. Hard-coded for the test
    /// fixture only — the emulator + SQL Edge are torn down at the end
    /// of the run and never expose the port outside the container
    /// network. Must satisfy the SQL Server strong-password policy.
    /// </summary>
    private const string SqlEdgePassword = "Aws2AzureEmulator!42";

    private const string SqlEdgeNetworkAlias = "sqledge";
    private const ushort EmulatorAmqpPort = 5672;
    private const ushort SqlEdgePort = 1433;

    /// <summary>
    /// Marker logged by the emulator after entity sync completes. Used
    /// as the host-side readiness gate; the literal text is documented
    /// in the official emulator samples.
    /// </summary>
    private const string ReadinessLogMarker = "Emulator Service is Successfully Up";

    private INetwork? _network;
    private IContainer? _sqlEdge;
    private IContainer? _emulator;
    private IFutureDockerImage? _emulatorImage;

    public bool DockerAvailable { get; private set; }
    public string AmqpHost { get; private set; } = "localhost";
    public int AmqpPort { get; private set; }

    public async Task InitializeAsync()
    {
        try
        {
            var dockerfileDir = Path.Combine(AppContext.BaseDirectory, "Fixtures");
            if (!File.Exists(Path.Combine(dockerfileDir, "ServiceBusEmulator.Dockerfile")) ||
                !File.Exists(Path.Combine(dockerfileDir, "ServiceBusEmulatorConfig.json")))
            {
                throw new FileNotFoundException(
                    "ServiceBusEmulator.Dockerfile / ServiceBusEmulatorConfig.json " +
                    $"not found under '{dockerfileDir}'. Ensure the .csproj copies " +
                    "both Fixtures/* files to the build output.");
            }

            _emulatorImage = new ImageFromDockerfileBuilder()
                .WithName(EmulatorImageTag)
                .WithDockerfileDirectory(dockerfileDir)
                .WithDockerfile("ServiceBusEmulator.Dockerfile")
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
                // The emulator polls SQL Edge for up to SQL_WAIT_INTERVAL
                // seconds before failing fast; the sidecar takes ~10-15s
                // to accept TDS on first boot, so 30s is the safer floor
                // for slow CI runners.
                .WithEnvironment("SQL_WAIT_INTERVAL", "30")
                .WithPortBinding(EmulatorAmqpPort, true)
                // The emulator image is distroless — no /bin/sh — so
                // Testcontainers' in-container UntilPortIsAvailable /
                // UntilMessageIsLogged strategies can't exec. We start
                // with an empty wait strategy and gate readiness
                // host-side via WaitForReadinessLogAsync below.
                .Build();
            await _emulator.StartAsync().ConfigureAwait(false);

            AmqpHost = _emulator.Hostname;
            AmqpPort = _emulator.GetMappedPublicPort(EmulatorAmqpPort);

            await WaitForReadinessLogAsync(_emulator,
                TimeSpan.FromMinutes(2)).ConfigureAwait(false);

            DockerAvailable = true;
        }
        catch (Exception ex) when (IsDockerUnavailable(ex))
        {
            DockerAvailable = false;
        }
    }

    /// <summary>
    /// Heuristic: did this exception come from Docker being unavailable
    /// (rather than a real fixture/test bug)? Mirrors the same swallow
    /// pattern used by <see cref="AzuriteFixture"/> so fork PRs and
    /// sandboxes without Docker self-skip instead of failing the build.
    /// </summary>
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
        // _emulatorImage is built WithCleanUp(false) so the layer cache
        // survives across runs; do not dispose it here.
    }

    /// <summary>
    /// Polls the emulator container's stdout for the readiness marker.
    /// More reliable than a TCP probe because Docker Desktop's port
    /// proxy accepts connections before the AMQP listener inside the
    /// container is actually live.
    /// </summary>
    private static async Task WaitForReadinessLogAsync(IContainer container, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var (stdout, stderr) = await container.GetLogsAsync().ConfigureAwait(false);
            if ((stdout?.Contains(ReadinessLogMarker, StringComparison.Ordinal) ?? false) ||
                (stderr?.Contains(ReadinessLogMarker, StringComparison.Ordinal) ?? false))
            {
                return;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
        }
        throw new TimeoutException(
            $"SB emulator did not log '{ReadinessLogMarker}' within {timeout.TotalSeconds:N0}s.");
    }
}

[CollectionDefinition(Name)]
public sealed class ServiceBusEmulatorCollection : ICollectionFixture<ServiceBusEmulatorFixture>
{
    public const string Name = "servicebus-emulator";
}
