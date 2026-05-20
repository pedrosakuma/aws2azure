using System.Buffers;
using Aws2Azure.Amqp.Connection;
using Aws2Azure.Amqp.Framing;
using Aws2Azure.Amqp.ServiceBus;
using Aws2Azure.UnitTests.Amqp.Transport;

namespace Aws2Azure.UnitTests.Amqp.ServiceBus;

/// <summary>
/// Tests for the receiver wrapper: receive-batch + the three Service Bus
/// settlement outcomes (Complete / Abandon / DeadLetter) and the AMQP
/// dispositions they emit on the wire.
/// </summary>
public sealed class ServiceBusReceiverTests
{
    private static AmqpConnectionSettings DefaultSettings() => new()
    {
        ContainerId = "test-client",
        Hostname = "ns.servicebus.windows.net",
        IdleTimeout = TimeSpan.Zero,
    };

    private static byte[] EncodeMessage(string body)
    {
        var msg = new AmqpMessage { Body = System.Text.Encoding.UTF8.GetBytes(body) };
        var rented = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            msg.Write(rented, out var written);
            return rented.AsSpan(0, written).ToArray();
        }
        finally { ArrayPool<byte>.Shared.Return(rented); }
    }

    private static async Task<(ServiceBusAmqpConnection conn, ServiceBusBrokerSimulator broker, ServiceBusReceiver recv, string linkName)>
        SetupReceiverAsync(string queueName, params byte[][] payloads)
    {
        var (client, server) = PipePairTransport.CreatePair();
        var broker = new ServiceBusBrokerSimulator(server);
        broker.Start();

        var conn = await ServiceBusAmqpConnection
            .OpenAsync(client, new FakeTokenProvider(), DefaultSettings())
            .WaitAsync(TimeSpan.FromSeconds(10));

        var audience = ServiceBusEndpoint.BuildQueueAudience("ns.servicebus.windows.net", queueName);
        var receiver = await conn.OpenReceiverAsync(queueName, audience, prefetchCredit: 0)
            .WaitAsync(TimeSpan.FromSeconds(10));
        // The receiver's link name is generated; recover it from the
        // broker's bookkeeping (the simulator records every attached link).
        var linkName = receiver.Link.Name;
        var queue = new Queue<ServiceBusBrokerSimulator.DeliveryToSend>();
        for (var i = 0; i < payloads.Length; i++)
            queue.Enqueue(new ServiceBusBrokerSimulator.DeliveryToSend(
                new byte[] { (byte)(i + 1) }, payloads[i]));
        broker.Inbox[linkName] = queue;
        return (conn, broker, receiver, linkName);
    }

    [Fact]
    public async Task ReceiveBatchAsync_returns_messages_within_deadline()
    {
        var (conn, broker, receiver, _) = await SetupReceiverAsync("queue1",
            EncodeMessage("hello"),
            EncodeMessage("world"));
        await using var _c = conn;
        await using var _r = receiver;

        var batch = await receiver
            .ReceiveBatchAsync(10, TimeSpan.FromSeconds(5))
            .WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(2, batch.Count);
        Assert.Equal("hello", System.Text.Encoding.UTF8.GetString(batch[0].Body.Span));
        Assert.Equal("world", System.Text.Encoding.UTF8.GetString(batch[1].Body.Span));
    }

    [Fact]
    public async Task CompleteAsync_emits_accepted_disposition()
    {
        var (conn, broker, receiver, _) = await SetupReceiverAsync("q", EncodeMessage("m1"));
        await using var _c = conn;
        await using var _r = receiver;

        var batch = await receiver.ReceiveBatchAsync(1, TimeSpan.FromSeconds(5)).WaitAsync(TimeSpan.FromSeconds(15));
        var msg = Assert.Single(batch);
        await receiver.CompleteAsync(msg).WaitAsync(TimeSpan.FromSeconds(5));

        await WaitForDispositionAsync(broker, msg.DeliveryId);
        Assert.Equal(AmqpDispositionOutcome.Accepted, broker.Dispositions[msg.DeliveryId].Outcome);
    }

    [Fact]
    public async Task AbandonAsync_emits_modified_disposition()
    {
        var (conn, broker, receiver, _) = await SetupReceiverAsync("q", EncodeMessage("m1"));
        await using var _c = conn;
        await using var _r = receiver;

        var batch = await receiver.ReceiveBatchAsync(1, TimeSpan.FromSeconds(5)).WaitAsync(TimeSpan.FromSeconds(15));
        var msg = Assert.Single(batch);
        await receiver.AbandonAsync(msg).WaitAsync(TimeSpan.FromSeconds(5));

        await WaitForDispositionAsync(broker, msg.DeliveryId);
        Assert.Equal(AmqpDispositionOutcome.Modified, broker.Dispositions[msg.DeliveryId].Outcome);
    }

    [Fact]
    public async Task DeadLetterAsync_emits_rejected_disposition()
    {
        var (conn, broker, receiver, _) = await SetupReceiverAsync("q", EncodeMessage("m1"));
        await using var _c = conn;
        await using var _r = receiver;

        var batch = await receiver.ReceiveBatchAsync(1, TimeSpan.FromSeconds(5)).WaitAsync(TimeSpan.FromSeconds(15));
        var msg = Assert.Single(batch);
        await receiver.DeadLetterAsync(msg, reason: "TooManyRetries", description: "exceeded").WaitAsync(TimeSpan.FromSeconds(5));

        await WaitForDispositionAsync(broker, msg.DeliveryId);
        Assert.Equal(AmqpDispositionOutcome.Rejected, broker.Dispositions[msg.DeliveryId].Outcome);
    }

    private static async Task WaitForDispositionAsync(ServiceBusBrokerSimulator broker, uint deliveryId)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (broker.Dispositions.ContainsKey(deliveryId)) return;
            await Task.Delay(20);
        }
        throw new TimeoutException($"Disposition for delivery-id {deliveryId} did not arrive in time.");
    }
}
