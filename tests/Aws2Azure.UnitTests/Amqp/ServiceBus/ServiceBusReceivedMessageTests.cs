using System.Buffers;
using System.Text;
using Aws2Azure.Amqp.Connection;
using Aws2Azure.Amqp.Framing;
using Aws2Azure.Amqp.ServiceBus;
using Aws2Azure.UnitTests.Amqp.Framing;
using Aws2Azure.UnitTests.Amqp.Transport;

namespace Aws2Azure.UnitTests.Amqp.ServiceBus;

/// <summary>
/// Tests for the SB-specific accessors layered on top of the parsed
/// bare-message: <see cref="ServiceBusReceivedMessage.Annotations"/>,
/// <see cref="ServiceBusReceivedMessage.DeliveryCount"/> and
/// <see cref="ServiceBusReceivedMessage.LockToken"/>. Slice 8b.4a wires
/// these so the SQS receive handler can map them onto receipt-handles
/// and the <c>ApproximateReceiveCount</c> / <c>SentTimestamp</c> /
/// <c>SequenceNumber</c> system attributes.
/// </summary>
public sealed class ServiceBusReceivedMessageTests
{
    private static AmqpConnectionSettings DefaultSettings() => new()
    {
        ContainerId = "test-client",
        Hostname = "ns.servicebus.windows.net",
        IdleTimeout = TimeSpan.Zero,
    };

    private static byte[] EncodeMessageWithAnnotationsAndHeader(
        string body, long sequenceNumber, DateTimeOffset lockedUntil,
        DateTimeOffset enqueuedTime, uint deliveryCount)
    {
        var w = new SectionWriter();
        // header — only delivery-count populated (5 fields, rest null).
        w.WriteDescribed(MessageSectionDescriptor.Header);
        w.BeginList8(count: 5);
        w.WriteNull(); w.WriteNull(); w.WriteNull(); w.WriteNull();
        w.WriteUInt(deliveryCount);
        w.EndList8();

        w.WriteDescribed(MessageSectionDescriptor.MessageAnnotations);
        w.BeginMap8(pairCount: 3);
        w.WriteSymbol(AmqpMessageAnnotations.KeySequenceNumber);
        w.WriteLong(sequenceNumber);
        w.WriteSymbol(AmqpMessageAnnotations.KeyLockedUntil);
        w.WriteTimestamp(lockedUntil.ToUnixTimeMilliseconds());
        w.WriteSymbol(AmqpMessageAnnotations.KeyEnqueuedTime);
        w.WriteTimestamp(enqueuedTime.ToUnixTimeMilliseconds());
        w.EndMap8();

        // properties — message-id only.
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var props = new AmqpProperties { MessageId = "msg-123" };
        var msg = new AmqpMessage { Properties = props, Body = bodyBytes };
        var rented = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            msg.Write(rented, out var written);
            var head = w.ToArray();
            var result = new byte[head.Length + written];
            head.CopyTo(result, 0);
            rented.AsSpan(0, written).CopyTo(result.AsSpan(head.Length));
            return result;
        }
        finally { ArrayPool<byte>.Shared.Return(rented); }
    }

    private static async Task<(ServiceBusAmqpConnection conn, ServiceBusReceiver recv)>
        SetupReceiverAsync(string queueName, byte[] deliveryTag, byte[] payload)
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

        var queue = new Queue<ServiceBusBrokerSimulator.DeliveryToSend>();
        queue.Enqueue(new ServiceBusBrokerSimulator.DeliveryToSend(deliveryTag, payload));
        broker.Inbox[receiver.Link.Name] = queue;
        return (conn, receiver);
    }

    [Fact]
    public async Task Accessors_surface_annotations_header_and_delivery_tag()
    {
        var sequenceNumber = 42L;
        var lockedUntil = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000);
        var enqueuedTime = DateTimeOffset.FromUnixTimeMilliseconds(1_699_999_990_000);
        var deliveryTag = Guid.Parse("11223344-5566-7788-99aa-bbccddeeff00").ToByteArray();
        var payload = EncodeMessageWithAnnotationsAndHeader(
            "hello", sequenceNumber, lockedUntil, enqueuedTime, deliveryCount: 3);

        var (conn, receiver) = await SetupReceiverAsync("q", deliveryTag, payload);
        await using var _c = conn;
        await using var _r = receiver;

        var batch = await receiver
            .ReceiveBatchAsync(1, TimeSpan.FromSeconds(5))
            .WaitAsync(TimeSpan.FromSeconds(15));

        var msg = Assert.Single(batch);
        Assert.Equal("hello", Encoding.UTF8.GetString(msg.Body.Span));
        Assert.Equal("msg-123", msg.MessageId);

        Assert.NotNull(msg.Annotations);
        Assert.Equal(sequenceNumber, msg.Annotations!.SequenceNumber);
        Assert.Equal(lockedUntil, msg.Annotations.LockedUntil);
        Assert.Equal(enqueuedTime, msg.Annotations.EnqueuedTime);

        Assert.Equal(3u, msg.DeliveryCount);

        Assert.Equal(new Guid(deliveryTag), msg.LockToken);
    }

    [Fact]
    public async Task LockToken_is_null_when_delivery_tag_is_not_sixteen_bytes()
    {
        var payload = EncodeMessageWithAnnotationsAndHeader(
            "x", 1L, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, deliveryCount: 0);
        var deliveryTag = new byte[] { 0x01 };

        var (conn, receiver) = await SetupReceiverAsync("q", deliveryTag, payload);
        await using var _c = conn;
        await using var _r = receiver;

        var batch = await receiver
            .ReceiveBatchAsync(1, TimeSpan.FromSeconds(5))
            .WaitAsync(TimeSpan.FromSeconds(15));

        var msg = Assert.Single(batch);
        Assert.Null(msg.LockToken);
        // The raw delivery-tag is still exposed for callers that don't
        // rely on the GUID interpretation.
        Assert.Equal(deliveryTag, msg.DeliveryTag.ToArray());
    }

    [Fact]
    public async Task Accessors_are_null_when_broker_omits_header_and_annotations()
    {
        // Body-only payload (matches the slice-8a EncodeMessage helper).
        var msg = new AmqpMessage { Body = Encoding.UTF8.GetBytes("body") };
        var rented = ArrayPool<byte>.Shared.Rent(256);
        byte[] payload;
        try
        {
            msg.Write(rented, out var written);
            payload = rented.AsSpan(0, written).ToArray();
        }
        finally { ArrayPool<byte>.Shared.Return(rented); }

        var deliveryTag = Guid.NewGuid().ToByteArray();
        var (conn, receiver) = await SetupReceiverAsync("q", deliveryTag, payload);
        await using var _c = conn;
        await using var _r = receiver;

        var batch = await receiver
            .ReceiveBatchAsync(1, TimeSpan.FromSeconds(5))
            .WaitAsync(TimeSpan.FromSeconds(15));

        var received = Assert.Single(batch);
        Assert.Null(received.Annotations);
        Assert.Null(received.DeliveryCount);
        Assert.Equal(new Guid(deliveryTag), received.LockToken);
    }
}
