using Aws2Azure.Amqp.Connection;
using Aws2Azure.Amqp.ServiceBus;
using Aws2Azure.UnitTests.Amqp.Transport;

namespace Aws2Azure.UnitTests.Amqp.ServiceBus;

/// <summary>
/// End-to-end tests for the SB AMQP orchestrator. The
/// <see cref="ServiceBusBrokerSimulator"/> impersonates Service Bus over
/// an in-process duplex transport, so these tests exercise the full
/// connection / session / CBS / link state machine without leaving the
/// process.
/// </summary>
public sealed class ServiceBusAmqpConnectionTests
{
    private static AmqpConnectionSettings DefaultSettings() => new()
    {
        ContainerId = "test-client",
        Hostname = "ns.servicebus.windows.net",
        IdleTimeout = TimeSpan.Zero,
    };

    [Fact]
    public async Task OpenAsync_completes_open_handshake_plus_cbs_session()
    {
        var (client, server) = PipePairTransport.CreatePair();
        await using var _ = server;

        var broker = new ServiceBusBrokerSimulator(server);
        broker.Start();

        await using var conn = await ServiceBusAmqpConnection
            .OpenAsync(client, new FakeTokenProvider(), DefaultSettings())
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.NotNull(conn.Connection);
    }

    [Fact]
    public async Task OpenReceiverAsync_authorises_audience_via_cbs_then_attaches_link()
    {
        var (client, server) = PipePairTransport.CreatePair();
        await using var _ = server;

        var broker = new ServiceBusBrokerSimulator(server);
        broker.Start();

        await using var conn = await ServiceBusAmqpConnection
            .OpenAsync(client, new FakeTokenProvider(), DefaultSettings())
            .WaitAsync(TimeSpan.FromSeconds(10));

        var audience = ServiceBusEndpoint.BuildQueueAudience("ns.servicebus.windows.net", "queue1");
        await using var receiver = await conn
            .OpenReceiverAsync("queue1", audience, prefetchCredit: 0)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("queue1", receiver.QueueName);
        Assert.Contains(audience, broker.AuthorizedAudiences);
    }

    [Fact]
    public async Task OpenReceiverAsync_caches_audience_authorisation_across_calls()
    {
        var (client, server) = PipePairTransport.CreatePair();
        await using var _ = server;

        var broker = new ServiceBusBrokerSimulator(server);
        broker.Start();

        await using var conn = await ServiceBusAmqpConnection
            .OpenAsync(client, new FakeTokenProvider(), DefaultSettings())
            .WaitAsync(TimeSpan.FromSeconds(10));

        var audience = ServiceBusEndpoint.BuildQueueAudience("ns.servicebus.windows.net", "queue1");
        await using (var r1 = await conn.OpenReceiverAsync("queue1", audience, 0).WaitAsync(TimeSpan.FromSeconds(10))) { }
        await using (var r2 = await conn.OpenReceiverAsync("queue1", audience, 0).WaitAsync(TimeSpan.FromSeconds(10))) { }

        // Cache must have prevented a second put-token for the same audience.
        Assert.Single(broker.AuthorizedAudiences, a => a == audience);
    }

    [Fact]
    public async Task OpenAsync_throws_when_cbs_returns_non_2xx()
    {
        var (client, server) = PipePairTransport.CreatePair();
        await using var _ = server;

        var broker = new ServiceBusBrokerSimulator(server) { CbsStatus = 401, CbsDescription = "Unauthorized" };
        broker.Start();

        // OpenAsync itself does not put-token (no audience yet); the failure
        // surfaces on the first OpenReceiverAsync call.
        await using var conn = await ServiceBusAmqpConnection
            .OpenAsync(client, new FakeTokenProvider(), DefaultSettings())
            .WaitAsync(TimeSpan.FromSeconds(10));

        var audience = ServiceBusEndpoint.BuildQueueAudience("ns.servicebus.windows.net", "queue1");
        await Assert.ThrowsAsync<global::Aws2Azure.Amqp.Security.CbsAuthenticationException>(
            () => conn.OpenReceiverAsync("queue1", audience, 0));
    }

    [Fact]
    public async Task OpenSessionReceiverAsync_binds_to_requested_session_id()
    {
        var (client, server) = PipePairTransport.CreatePair();
        await using var _ = server;

        var broker = new ServiceBusBrokerSimulator(server);
        broker.Start();

        await using var conn = await ServiceBusAmqpConnection
            .OpenAsync(client, new FakeTokenProvider(), DefaultSettings())
            .WaitAsync(TimeSpan.FromSeconds(10));

        var audience = ServiceBusEndpoint.BuildQueueAudience("ns.servicebus.windows.net", "fifo-queue");
        await using var receiver = await conn
            .OpenSessionReceiverAsync("fifo-queue", audience, sessionId: "order-42", prefetchCredit: 0)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("fifo-queue", receiver.QueueName);
        Assert.Equal("order-42", receiver.SessionId);
        // Broker must have observed exactly one session-filtered attach
        // with the requested session-id.
        var observed = Assert.Single(broker.SessionFiltersByLink);
        Assert.Equal("order-42", observed.Value);
    }

    [Fact]
    public async Task OpenSessionReceiverAsync_with_null_session_id_uses_broker_assigned()
    {
        var (client, server) = PipePairTransport.CreatePair();
        await using var _ = server;

        var broker = new ServiceBusBrokerSimulator(server);
        broker.Start();

        await using var conn = await ServiceBusAmqpConnection
            .OpenAsync(client, new FakeTokenProvider(), DefaultSettings())
            .WaitAsync(TimeSpan.FromSeconds(10));

        var audience = ServiceBusEndpoint.BuildQueueAudience("ns.servicebus.windows.net", "fifo-queue");
        await using var receiver = await conn
            .OpenSessionReceiverAsync("fifo-queue", audience, sessionId: null, prefetchCredit: 0)
            .WaitAsync(TimeSpan.FromSeconds(10));

        // The broker assigned the default placeholder when the client
        // asked for "any available session"; the receiver must surface
        // that bound id (not the null that was requested).
        Assert.Equal("broker-assigned-session", receiver.SessionId);
        var observed = Assert.Single(broker.SessionFiltersByLink);
        Assert.Null(observed.Value);
    }
}
