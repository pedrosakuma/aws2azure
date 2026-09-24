using Aws2Azure.Amqp.Connection;
using Aws2Azure.Amqp.Framing;
using Aws2Azure.Amqp.Security;
using Aws2Azure.Amqp.ServiceBus;
using Aws2Azure.UnitTests.Amqp.Transport;
using Aws2Azure.UnitTests.Azure;

namespace Aws2Azure.UnitTests.Amqp.ServiceBus;

public sealed class ServiceBusCachedAuthorizationTests
{
    private static readonly ServiceBusAmqpEndpoint Endpoint = ServiceBusAmqpEndpoint.Tls("ns.servicebus.windows.net");
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cached_resources_renew_on_use_without_replacing_links(bool session)
    {
        await using var factory = new ClockedFactory();
        await using var pool = new ServiceBusAmqpPool(factory);
        var receiver = session
            ? await pool.GetSessionReceiverAsync(Endpoint, "Root", "key", "queue", "group").WaitAsync(Timeout)
            : await pool.GetReceiverAsync(Endpoint, "Root", "key", "queue").WaitAsync(Timeout);
        var sender = await pool.GetSenderAsync(Endpoint, "Root", "key", "queue").WaitAsync(Timeout);
        Assert.Single(factory.Broker!.AuthorizedAudiences);

        for (var minute = 1; minute <= 31; minute++)
        {
            factory.Clock.Advance(TimeSpan.FromMinutes(1));
            await sender.SendAsync(new AmqpMessage { Body = new byte[] { 1 } }).WaitAsync(Timeout);
            var message = await ReceiveAsync(factory, receiver);
            await receiver.CompleteAsync(message).WaitAsync(Timeout);
            Assert.Equal(1 + minute / 15, factory.Broker.AuthorizedAudiences.Count);
        }

        var cached = session
            ? pool.TryGetExistingSessionReceiver(Endpoint, "Root", "queue", "group")
            : pool.TryGetExistingReceiver(Endpoint, "Root", "queue");
        Assert.Same(receiver, cached);
        Assert.Same(sender, await pool.GetSenderAsync(Endpoint, "Root", "key", "queue"));
        Assert.False(receiver.IsClosed);
        Assert.Equal(0, receiver.InFlightCount);
        Assert.Equal(1, factory.CreateCount);
    }

    [Fact]
    public async Task Receive_on_retained_reference_renews_after_idle_expiry()
    {
        await using var factory = new ClockedFactory();
        await using var pool = new ServiceBusAmqpPool(factory);
        var receiver = await pool.GetReceiverAsync(Endpoint, "Root", "key", "queue").WaitAsync(Timeout);
        factory.Clock.Advance(TimeSpan.FromMinutes(31));

        var message = await ReceiveAsync(factory, receiver);

        Assert.Equal(2, factory.Broker!.AuthorizedAudiences.Count);
        Assert.True(receiver.ContainsLockToken(message.LockToken!.Value));
        Assert.False(receiver.IsClosed);
    }

    [Theory]
    [InlineData("complete")]
    [InlineData("abandon")]
    [InlineData("deadletter")]
    [InlineData("complete-message")]
    [InlineData("abandon-message")]
    [InlineData("deadletter-message")]
    [InlineData("release")]
    public async Task Fifo_settlement_lease_renews_and_preserves_inflight_receipts(string operation)
    {
        await using var factory = new ClockedFactory();
        await using var pool = new ServiceBusAmqpPool(factory);
        var acquired = await pool.AcquireBrokerAssignedSessionReceiverAsync(
            Endpoint, "Root", "key", "queue", TimeSpan.FromSeconds(5)).WaitAsync(Timeout);
        var receiver = Assert.IsType<ServiceBusReceiver>(acquired.Receiver);
        var message = await ReceiveAsync(factory, receiver);
        factory.Clock.Advance(TimeSpan.FromMinutes(16));

        using var lease = pool.TryAcquireExistingSessionReceiver(Endpoint, "Root", "queue", receiver.SessionId!);
        Assert.NotNull(lease);
        Assert.Same(receiver, lease.Receiver);
        await SettleAsync(lease.Receiver, message, operation).WaitAsync(Timeout);

        Assert.Equal(2, factory.Broker!.AuthorizedAudiences.Count);
        Assert.False(receiver.IsClosed);
        Assert.Equal(0, receiver.InFlightCount);
        await factory.Broker.WaitForDispositionAsync(message.Delivery.DeliveryId, Timeout);
    }

    [Fact]
    public async Task Rejected_renewal_does_not_send_or_lose_a_claimed_delivery()
    {
        await using var factory = new ClockedFactory();
        await using var pool = new ServiceBusAmqpPool(factory);
        var receiver = await pool.GetReceiverAsync(Endpoint, "Root", "key", "queue").WaitAsync(Timeout);
        var sender = await pool.GetSenderAsync(Endpoint, "Root", "key", "queue").WaitAsync(Timeout);
        var message = await ReceiveAsync(factory, receiver);
        factory.Clock.Advance(TimeSpan.FromMinutes(16));
        factory.Broker!.CbsStatus = 401;

        await Assert.ThrowsAsync<CbsAuthenticationException>(
            () => receiver.CompleteAsync(message.LockToken!.Value).WaitAsync(Timeout));
        await Assert.ThrowsAsync<CbsAuthenticationException>(
            () => sender.SendAsync(new AmqpMessage()).WaitAsync(Timeout));
        Assert.Empty(factory.Broker.Dispositions);
        Assert.Empty(factory.Broker.ReceivedTransfers);
        Assert.True(receiver.ContainsLockToken(message.LockToken!.Value));
        Assert.False(receiver.IsClosed);

        factory.Broker.CbsStatus = 202;
        Assert.True(await receiver.CompleteAsync(message.LockToken!.Value).WaitAsync(Timeout));
        Assert.Equal(4, factory.Broker.AuthorizedAudiences.Count);
        Assert.Equal(0, receiver.InFlightCount);
    }

    [Theory]
    [InlineData("complete-message")]
    [InlineData("abandon-message")]
    [InlineData("deadletter-message")]
    [InlineData("release")]
    public async Task Settlement_without_lock_token_also_renews(string operation)
    {
        await using var factory = new ClockedFactory();
        await using var pool = new ServiceBusAmqpPool(factory);
        var receiver = await pool.GetReceiverAsync(Endpoint, "Root", "key", "queue").WaitAsync(Timeout);
        factory.Broker!.Inbox[receiver.Link.Name] = new Queue<ServiceBusBrokerSimulator.DeliveryToSend>(
            [new(new byte[] { 1 }, Array.Empty<byte>())]);
        var message = Assert.Single(await receiver.ReceiveBatchAsync(1, TimeSpan.FromSeconds(2)).WaitAsync(Timeout));
        Assert.Null(message.LockToken);
        factory.Clock.Advance(TimeSpan.FromMinutes(16));

        await SettleAsync(receiver, message, operation).WaitAsync(Timeout);

        Assert.Equal(2, factory.Broker.AuthorizedAudiences.Count);
        await factory.Broker.WaitForDispositionAsync(message.Delivery.DeliveryId, Timeout);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cached_management_operation_checks_its_own_audience(bool session)
    {
        await using var factory = new ClockedFactory();
        await using var pool = new ServiceBusAmqpPool(factory);
        var management = await pool.GetManagementClientAsync(Endpoint, "Root", "key", "queue").WaitAsync(Timeout);
        factory.Clock.Advance(TimeSpan.FromMinutes(16));
        factory.Broker!.CbsStatus = 403;

        await Assert.ThrowsAsync<CbsAuthenticationException>(() => session
            ? management.RenewSessionLockAsync("group", "link").WaitAsync(Timeout)
            : management.RenewLockAsync(Guid.NewGuid()).WaitAsync(Timeout));

        Assert.Equal(2, factory.Broker.AuthorizedAudiences.Count);
        Assert.All(factory.Broker.AuthorizedAudiences, audience =>
            Assert.Equal(ServiceBusEndpoint.BuildManagementAudience(Endpoint.LogicalNamespace, "queue"), audience));
        Assert.Empty(factory.Broker.ReceivedTransfers);
        Assert.Same(management, await pool.GetManagementClientAsync(Endpoint, "Root", "key", "queue"));
        Assert.False(management.IsClosed);
    }

    [Fact]
    public async Task Concurrent_operations_coalesce_renewal_per_audience_with_supplied_provider()
    {
        await using var factory = new ClockedFactory();
        var provider = new ControlledProvider(factory.Clock);
        await using var pool = new ServiceBusAmqpPool(factory);
        var audience = "amqps://ns.servicebus.windows.net/topic";
        var sender = await pool.GetSenderAsync(Endpoint, "oauth", provider, "topic", audience).WaitAsync(Timeout);
        factory.Clock.Advance(TimeSpan.FromMinutes(16));
        provider.BlockNext();
        var first = sender.SendAsync(new AmqpMessage());
        await provider.Entered.Task.WaitAsync(Timeout);
        var others = Enumerable.Range(0, 12).Select(_ => sender.SendAsync(new AmqpMessage())).ToArray();
        Assert.Equal(2, provider.Calls);
        provider.Release.TrySetResult();
        await Task.WhenAll(others.Append(first)).WaitAsync(Timeout);

        Assert.Equal(2, factory.Broker!.AuthorizedAudiences.Count);
        Assert.Equal(2, provider.Calls);
        Assert.Equal(13, factory.Broker.ReceivedTransfers[sender.Link.Name].Count);

        var otherAudience = "amqps://ns.servicebus.windows.net/other";
        var other = await pool.GetSenderAsync(Endpoint, "oauth", provider, "other", otherAudience);
        factory.Clock.Advance(TimeSpan.FromMinutes(16));
        await sender.SendAsync(new AmqpMessage()).WaitAsync(Timeout);
        Assert.Equal(4, provider.Calls);
        await other.SendAsync(new AmqpMessage()).WaitAsync(Timeout);
        Assert.Equal(5, provider.Calls);
        Assert.Equal(3, factory.Broker.AuthorizedAudiences.Count(a => a == audience));
        Assert.Equal(2, factory.Broker.AuthorizedAudiences.Count(a => a == otherAudience));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancelled_renewal_or_waiter_does_not_poison_cached_sender(bool cancelWaiter)
    {
        await using var factory = new ClockedFactory();
        var provider = new ControlledProvider(factory.Clock);
        await using var pool = new ServiceBusAmqpPool(factory);
        var sender = await pool.GetSenderAsync(Endpoint, "oauth", provider, "topic", "topic-audience");
        factory.Clock.Advance(TimeSpan.FromMinutes(16));
        provider.BlockNext();
        using var cancellation = new CancellationTokenSource();
        var first = sender.SendAsync(new AmqpMessage(),
            cancellationToken: cancelWaiter ? CancellationToken.None : cancellation.Token);
        await provider.Entered.Task.WaitAsync(Timeout);
        var cancelled = cancelWaiter
            ? sender.SendAsync(new AmqpMessage(), cancellationToken: cancellation.Token)
            : first;
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.WaitAsync(Timeout));
        Assert.Empty(factory.Broker!.ReceivedTransfers);
        provider.Release.TrySetResult();
        if (cancelWaiter)
            await first.WaitAsync(Timeout);
        await sender.SendAsync(new AmqpMessage()).WaitAsync(Timeout);

        Assert.Equal(2, factory.Broker.AuthorizedAudiences.Count);
        Assert.Equal(cancelWaiter ? 2 : 3, provider.Calls);
        Assert.False(sender.IsClosed);
    }

    [Fact]
    public async Task Unknown_expiry_keeps_existing_provider_contract_without_reauthorizing_each_operation()
    {
        await using var factory = new ClockedFactory();
        var provider = new ControlledProvider(factory.Clock) { OmitExpiry = true };
        await using var pool = new ServiceBusAmqpPool(factory);
        var sender = await pool.GetSenderAsync(Endpoint, "opaque", provider, "topic", "topic-audience");
        factory.Clock.Advance(TimeSpan.FromHours(2));
        for (var i = 0; i < 5; i++)
            await sender.SendAsync(new AmqpMessage()).WaitAsync(Timeout);
        Assert.Single(factory.Broker!.AuthorizedAudiences);
        Assert.Equal(1, provider.Calls);
    }

    private static async Task<ServiceBusReceivedMessage> ReceiveAsync(
        ClockedFactory factory, ServiceBusReceiver receiver)
    {
        factory.Broker!.Inbox[receiver.Link.Name] = new Queue<ServiceBusBrokerSimulator.DeliveryToSend>(
            [new(Guid.NewGuid().ToByteArray(), Array.Empty<byte>())]);
        return Assert.Single(await receiver.ReceiveBatchAsync(1, TimeSpan.FromSeconds(2)).WaitAsync(Timeout));
    }

    private static async Task SettleAsync(
        ServiceBusReceiver receiver, ServiceBusReceivedMessage message, string operation)
    {
        switch (operation)
        {
            case "complete": Assert.True(await receiver.CompleteAsync(message.LockToken!.Value)); break;
            case "abandon": Assert.True(await receiver.AbandonAsync(message.LockToken!.Value)); break;
            case "deadletter": Assert.True(await receiver.DeadLetterAsync(message.LockToken!.Value)); break;
            case "complete-message": await receiver.CompleteAsync(message); break;
            case "abandon-message": await receiver.AbandonAsync(message); break;
            case "deadletter-message": await receiver.DeadLetterAsync(message); break;
            case "release": await receiver.ReleaseAsync(message); break;
            default: throw new ArgumentOutOfRangeException(nameof(operation));
        }
    }

    private sealed class ControlledProvider(FakeTimeProvider clock) : IAmqpTokenProvider
    {
        private int _calls;
        private int _block;
        public int Calls => Volatile.Read(ref _calls);
        public bool OmitExpiry { get; init; }
        public string TokenType => "servicebus.windows.net:sastoken";
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void BlockNext() => Interlocked.Exchange(ref _block, 1);
        public AmqpToken GetToken(string audience) => throw new InvalidOperationException("Use async acquisition.");
        public async ValueTask<AmqpToken> GetTokenAsync(string audience, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            if (Interlocked.Exchange(ref _block, 0) != 0)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return new AmqpToken("test-token", OmitExpiry ? null : clock.GetUtcNow().AddMinutes(20));
        }
    }

    private sealed class ClockedFactory : IServiceBusAmqpConnectionFactory, IAsyncDisposable
    {
        public FakeTimeProvider Clock { get; } = new(DateTimeOffset.UtcNow);
        public ServiceBusBrokerSimulator? Broker { get; private set; }
        public int CreateCount { get; private set; }
        private IAsyncDisposable? _server;

        public Task<ServiceBusAmqpConnection> CreateAsync(
            ServiceBusAmqpEndpoint endpoint, string name, string key, CancellationToken cancellationToken) =>
            CreateAsync(endpoint, name, new ServiceBusSasTokenProvider(name, key, clock: Clock), cancellationToken);

        public async Task<ServiceBusAmqpConnection> CreateAsync(
            ServiceBusAmqpEndpoint endpoint, string name, IAmqpTokenProvider provider, CancellationToken cancellationToken)
        {
            CreateCount++;
            var (client, server) = PipePairTransport.CreatePair();
            _server = server;
            Broker = new ServiceBusBrokerSimulator(server);
            Broker.Start();
            return await ServiceBusAmqpConnection.OpenAsync(client, provider, new AmqpConnectionSettings
            {
                ContainerId = "cached-renewal-tests", Hostname = endpoint.LogicalNamespace, IdleTimeout = TimeSpan.Zero,
            }, Clock, TimeSpan.FromMinutes(5), cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            if (_server is not null) await _server.DisposeAsync();
        }
    }
}
