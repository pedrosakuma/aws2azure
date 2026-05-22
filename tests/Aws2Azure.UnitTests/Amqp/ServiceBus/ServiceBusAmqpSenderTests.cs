using Aws2Azure.Amqp.Connection;
using Aws2Azure.Amqp.Framing;
using Aws2Azure.Amqp.ServiceBus;
using Aws2Azure.UnitTests.Amqp.Transport;

namespace Aws2Azure.UnitTests.Amqp.ServiceBus;

/// <summary>
/// Tests for <see cref="ServiceBusAmqpSender"/> and the
/// <see cref="ServiceBusAmqpConnection.OpenSenderAsync"/> orchestration.
/// </summary>
public sealed class ServiceBusAmqpSenderTests
{
    private static AmqpConnectionSettings DefaultSettings() => new()
    {
        ContainerId = "test-client",
        Hostname = "ns.servicebus.windows.net",
        IdleTimeout = TimeSpan.Zero,
    };

    [Fact]
    public async Task OpenSenderAsync_authorises_audience_via_cbs_then_attaches_sender_link()
    {
        var (client, server) = PipePairTransport.CreatePair();
        await using var _ = server;
        var broker = new ServiceBusBrokerSimulator(server);
        broker.Start();

        await using var conn = await ServiceBusAmqpConnection
            .OpenAsync(client, new FakeTokenProvider(), DefaultSettings())
            .WaitAsync(TimeSpan.FromSeconds(10));

        var audience = ServiceBusEndpoint.BuildQueueAudience("ns.servicebus.windows.net", "queue1");
        await using var sender = await conn
            .OpenSenderAsync("queue1", audience)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("queue1", sender.QueueName);
        Assert.Contains(audience, broker.AuthorizedAudiences);
    }

    [Fact]
    public async Task SendAsync_single_message_completes_on_accepted_disposition()
    {
        var (client, server) = PipePairTransport.CreatePair();
        await using var _ = server;
        var broker = new ServiceBusBrokerSimulator(server);
        broker.Start();

        await using var conn = await ServiceBusAmqpConnection
            .OpenAsync(client, new FakeTokenProvider(), DefaultSettings())
            .WaitAsync(TimeSpan.FromSeconds(10));

        var audience = ServiceBusEndpoint.BuildQueueAudience("ns.servicebus.windows.net", "queue1");
        await using var sender = await conn
            .OpenSenderAsync("queue1", audience)
            .WaitAsync(TimeSpan.FromSeconds(10));

        var msg = new AmqpMessage
        {
            Properties = new AmqpProperties { MessageId = "m1" },
            BodyValueString = "hello",
        };

        await sender.SendAsync(msg).WaitAsync(TimeSpan.FromSeconds(10));

        // Wait briefly for the broker loop to process the inbound transfer
        // (broker runs on a separate task; transfer arrives async even
        // though the settled=false send already awaited the disposition).
        var linkName = sender.Link.Name;
        for (var i = 0; i < 50 && !broker.ReceivedTransfers.ContainsKey(linkName); i++)
            await Task.Delay(20);
        Assert.True(broker.ReceivedTransfers.TryGetValue(linkName, out var got));
        Assert.Single(got);
        Assert.Equal("m1", got[0].Properties.MessageId?.ToString());
    }

    [Fact]
    public async Task SendAsync_propagates_rejected_outcome_as_ServiceBusSendException()
    {
        var (client, server) = PipePairTransport.CreatePair();
        await using var _ = server;
        var broker = new ServiceBusBrokerSimulator(server);
        broker.Start();

        await using var conn = await ServiceBusAmqpConnection
            .OpenAsync(client, new FakeTokenProvider(), DefaultSettings())
            .WaitAsync(TimeSpan.FromSeconds(10));

        var audience = ServiceBusEndpoint.BuildQueueAudience("ns.servicebus.windows.net", "queue1");
        await using var sender = await conn
            .OpenSenderAsync("queue1", audience)
            .WaitAsync(TimeSpan.FromSeconds(10));

        var linkName = sender.Link.Name;
        broker.RejectNextTransferByLink[linkName] = new AmqpError
        {
            Condition = "amqp:internal-error",
            Description = "test reject",
        };

        var msg = new AmqpMessage
        {
            Properties = new AmqpProperties { MessageId = "m1" },
            BodyValueString = "hello",
        };

        var ex = await Assert.ThrowsAsync<ServiceBusSendException>(
            async () => await sender.SendAsync(msg).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(AmqpDispositionOutcome.Rejected, ex.Outcome);
    }

    [Fact]
    public async Task SendAsync_after_dispose_throws_ObjectDisposed()
    {
        var (client, server) = PipePairTransport.CreatePair();
        await using var _ = server;
        var broker = new ServiceBusBrokerSimulator(server);
        broker.Start();

        await using var conn = await ServiceBusAmqpConnection
            .OpenAsync(client, new FakeTokenProvider(), DefaultSettings())
            .WaitAsync(TimeSpan.FromSeconds(10));

        var audience = ServiceBusEndpoint.BuildQueueAudience("ns.servicebus.windows.net", "queue1");
        var sender = await conn
            .OpenSenderAsync("queue1", audience)
            .WaitAsync(TimeSpan.FromSeconds(10));

        await sender.DisposeAsync();

        var msg = new AmqpMessage { BodyValueString = "x" };
        await Assert.ThrowsAsync<ObjectDisposedException>(
            async () => await sender.SendAsync(msg));
    }

    [Fact]
    public async Task SendAsync_caches_audience_authorisation_across_sender_opens()
    {
        var (client, server) = PipePairTransport.CreatePair();
        await using var _ = server;
        var broker = new ServiceBusBrokerSimulator(server);
        broker.Start();

        await using var conn = await ServiceBusAmqpConnection
            .OpenAsync(client, new FakeTokenProvider(), DefaultSettings())
            .WaitAsync(TimeSpan.FromSeconds(10));

        var audience = ServiceBusEndpoint.BuildQueueAudience("ns.servicebus.windows.net", "queue1");
        await using (var s1 = await conn.OpenSenderAsync("queue1", audience).WaitAsync(TimeSpan.FromSeconds(10))) { }
        await using (var s2 = await conn.OpenSenderAsync("queue1", audience).WaitAsync(TimeSpan.FromSeconds(10))) { }

        // CBS authorise once per audience (cached). Second open reuses the token.
        Assert.Equal(1, broker.AuthorizedAudiences.Count(a => a == audience));
    }
}
