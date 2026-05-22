using System;
using System.Net.Sockets;
using System.Threading.Tasks;
using Xunit;

namespace Aws2Azure.IntegrationTests.Sqs;

/// <summary>
/// Phase 2.7 Slice 4 — fixture smoke. Proves the SB-emulator
/// Testcontainers fixture either:
///   * boots both containers, builds the derived emulator image with
///     the SQS-shaped Config.json baked in, observes the readiness log
///     marker, and assigns a mapped AMQP port; or
///   * cleanly self-skips when Docker is not available (fork PRs and
///     local sandboxes without Docker).
///
/// <para>The emulator image only publishes AMQP 5672 — no HTTP
/// management surface — so post-init checks are limited to verifying
/// the port is assigned and TCP-connectable. The fixture's own
/// readiness gate (log-marker poll) is the real proof that the
/// emulator finished entity sync; this test just observes the side
/// effects.</para>
/// </summary>
[Trait("Category", "Integration")]
[Collection(ServiceBusEmulatorCollection.Name)]
public sealed class ServiceBusEmulatorFixtureSmoke
{
    private readonly ServiceBusEmulatorFixture _fixture;

    public ServiceBusEmulatorFixtureSmoke(ServiceBusEmulatorFixture fixture)
    {
        _fixture = fixture;
    }

    [SkippableFact]
    public async Task Emulator_starts_and_amqp_port_is_reachable()
    {
        Skip.IfNot(_fixture.DockerAvailable,
            "Docker not available — skipping SB emulator integration smoke. " +
            "Expected on fork PRs / sandboxes without Docker.");

        Assert.True(_fixture.AmqpPort > 0, "AMQP port should be assigned.");

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(_fixture.AmqpHost, _fixture.AmqpPort)
            .WaitAsync(TimeSpan.FromSeconds(10))
            .ConfigureAwait(false);
        Assert.True(tcp.Connected, "TCP connect to SB emulator AMQP port failed.");
    }
}

