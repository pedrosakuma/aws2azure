using Aws2Azure.Amqp.Connection;
using Aws2Azure.Amqp.Framing;
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

        broker.Inbox[receiver.Link.Name] = new Queue<ServiceBusBrokerSimulator.DeliveryToSend>(
        [
            new(Guid.NewGuid().ToByteArray(), Array.Empty<byte>()),
        ]);
        var message = Assert.Single(
            await receiver.ReceiveBatchAsync(1, TimeSpan.FromSeconds(2))
                .WaitAsync(TimeSpan.FromSeconds(5)));
        await receiver.CompleteAsync(message).WaitAsync(TimeSpan.FromSeconds(5));
        var disposition = Assert.Single(broker.Dispositions).Value;
        Assert.False(disposition.Settled);
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
        var attach = Assert.Single(broker.SessionAttachesByLink).Value;
        Assert.Equal(AmqpReceiverSettleMode.Second, attach.ReceiverSettleMode);
        Assert.StartsWith("aws2azure-recv-fifo-queue-session-", attach.TargetAddress);
        Assert.Null(attach.TimeoutMilliseconds);
    }

    [Fact]
    public async Task OpenSessionReceiverAsync_accept_next_sends_server_timeout()
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
            .OpenSessionReceiverAsync(
                "fifo-queue",
                audience,
                sessionId: null,
                prefetchCredit: 0,
                acceptNextTimeout: TimeSpan.FromSeconds(7))
            .WaitAsync(TimeSpan.FromSeconds(10));

        var attach = Assert.Single(broker.SessionAttachesByLink).Value;
        Assert.Equal(7_000u, attach.TimeoutMilliseconds);
    }

    [Fact]
    public async Task Session_settlement_finishes_confirmation_after_caller_cancels()
    {
        var (client, server) = PipePairTransport.CreatePair();
        await using var _ = server;

        var confirmationGate = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var broker = new ServiceBusBrokerSimulator(server)
        {
            SettlementConfirmationGate = confirmationGate,
        };
        broker.Start();

        await using var conn = await ServiceBusAmqpConnection
            .OpenAsync(client, new FakeTokenProvider(), DefaultSettings())
            .WaitAsync(TimeSpan.FromSeconds(10));
        var audience = ServiceBusEndpoint.BuildQueueAudience(
            "ns.servicebus.windows.net", "fifo-queue");
        await using var receiver = await conn
            .OpenSessionReceiverAsync(
                "fifo-queue",
                audience,
                sessionId: "order-42",
                prefetchCredit: 0)
            .WaitAsync(TimeSpan.FromSeconds(10));

        var lockToken = Guid.NewGuid();
        broker.Inbox[receiver.Link.Name] = new Queue<ServiceBusBrokerSimulator.DeliveryToSend>(
        [
            new(lockToken.ToByteArray(), Array.Empty<byte>()),
        ]);
        var message = Assert.Single(
            await receiver.ReceiveBatchAsync(1, TimeSpan.FromSeconds(2))
                .WaitAsync(TimeSpan.FromSeconds(5)));

        using var cancellation = new CancellationTokenSource();
        var settlement = receiver.CompleteAsync(message, cancellation.Token);
        await WaitForDispositionAsync(broker, message.DeliveryId);
        cancellation.Cancel();
        await Task.Delay(50);
        Assert.False(settlement.IsCompleted);

        confirmationGate.TrySetResult();
        await settlement.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, receiver.InFlightCount);
    }

    [Fact]
    public void Broker_accept_next_timeout_is_classified_as_empty_acquisition()
    {
        var exception = new AmqpLinkException("server wait elapsed")
        {
            PeerCondition = AmqpErrorCondition.Timeout,
        };

        Assert.True(ServiceBusAmqpPool.IsBrokerAcceptNextTimeout(exception));
    }

    [Fact]
    public async Task OpenSessionReceiverAsync_throws_when_broker_does_not_bind_a_session()
    {
        var (client, server) = PipePairTransport.CreatePair();
        await using var _ = server;

        var broker = new ServiceBusBrokerSimulator(server) { EchoSessionFilterOnAttach = false };
        broker.Start();

        await using var conn = await ServiceBusAmqpConnection
            .OpenAsync(client, new FakeTokenProvider(), DefaultSettings())
            .WaitAsync(TimeSpan.FromSeconds(10));

        var audience = ServiceBusEndpoint.BuildQueueAudience("ns.servicebus.windows.net", "fifo-queue");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            conn.OpenSessionReceiverAsync("fifo-queue", audience, sessionId: null, prefetchCredit: 0));
    }

    [Fact]
    public async Task OpenReceiverAsync_proactively_renews_authorisation_within_safety_window()
    {
        var (client, server) = PipePairTransport.CreatePair();
        await using var _ = server;

        var broker = new ServiceBusBrokerSimulator(server);
        broker.Start();

        var fakeClock = new global::Aws2Azure.UnitTests.Azure.FakeTimeProvider(
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var tokenProvider = new FakeTokenProvider { Expiry = fakeClock.GetUtcNow().AddMinutes(20) };

        await using var conn = await ServiceBusAmqpConnection
            .OpenAsync(client, tokenProvider, DefaultSettings(),
                clock: fakeClock,
                refreshSafetyWindow: TimeSpan.FromMinutes(5))
            .WaitAsync(TimeSpan.FromSeconds(10));

        var audience = ServiceBusEndpoint.BuildQueueAudience("ns.servicebus.windows.net", "queue1");

        // First open: authorises once.
        await using (var r1 = await conn.OpenReceiverAsync("queue1", audience, 0).WaitAsync(TimeSpan.FromSeconds(10))) { }
        Assert.Single(broker.AuthorizedAudiences, a => a == audience);

        // Advance clock 14 min — token still has 6 min left, outside the 5-min
        // safety window. Cache must hold.
        fakeClock.Advance(TimeSpan.FromMinutes(14));
        await using (var r2 = await conn.OpenReceiverAsync("queue1", audience, 0).WaitAsync(TimeSpan.FromSeconds(10))) { }
        Assert.Single(broker.AuthorizedAudiences, a => a == audience);

        // Advance clock another 2 min — token now has 4 min left, within the
        // safety window. Next open must trigger a put-token refresh.
        fakeClock.Advance(TimeSpan.FromMinutes(2));
        tokenProvider.Expiry = fakeClock.GetUtcNow().AddMinutes(20);
        await using (var r3 = await conn.OpenReceiverAsync("queue1", audience, 0).WaitAsync(TimeSpan.FromSeconds(10))) { }
        Assert.Equal(2, broker.AuthorizedAudiences.Count(a => a == audience));
    }

    [Theory]
    // (cachedExpiry minutes from now, safety window minutes, expected refresh)
    [InlineData(20, 5, false)] // plenty of headroom
    [InlineData(6, 5, false)]  // just outside safety window
    [InlineData(5, 5, true)]   // exactly on the boundary → refresh
    [InlineData(4, 5, true)]   // inside safety window
    [InlineData(-1, 5, true)]  // already expired
    public void ShouldRefreshAuthorization_table(int expiryMinutesFromNow, int safetyMinutes, bool expected)
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var expiry = now.AddMinutes(expiryMinutesFromNow);
        Assert.Equal(expected, ServiceBusAmqpConnection.ShouldRefreshAuthorization(
            expiry, now, TimeSpan.FromMinutes(safetyMinutes)));
    }

    [Fact]
    public void ShouldRefreshAuthorization_null_expiry_never_refreshes()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        Assert.False(ServiceBusAmqpConnection.ShouldRefreshAuthorization(
            cachedExpiry: null, now, TimeSpan.FromMinutes(5)));
    }

    private static async Task WaitForDispositionAsync(
        ServiceBusBrokerSimulator broker,
        uint deliveryId)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (broker.Dispositions.ContainsKey(deliveryId))
            {
                return;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException(
            $"Disposition for delivery-id {deliveryId} did not arrive in time.");
    }
}
