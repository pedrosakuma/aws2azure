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
        var disposition = broker.Dispositions[msg.DeliveryId];
        Assert.Equal(AmqpDispositionOutcome.Modified, disposition.Outcome);
        Assert.Null(disposition.DeliveryFailed);
        Assert.Null(disposition.UndeliverableHere);
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

        // Slice 8c.4: error.info carries DeadLetterReason +
        // DeadLetterErrorDescription as a typed fields map so SB
        // surfaces them on the DLQ copy's app-properties.
        Assert.True(broker.RejectedErrors.TryGetValue(msg.DeliveryId, out var err));
        Assert.Equal("com.microsoft:dead-letter", err.Condition);
        Assert.True(Aws2Azure.Amqp.ServiceBus.ServiceBusDeadLetterInfo.TryDecode(
            err.Info, out var decodedReason, out var decodedDescription));
        Assert.Equal("TooManyRetries", decodedReason);
        Assert.Equal("exceeded", decodedDescription);
    }

    [Fact]
    public async Task ReceiveBatchAsync_caches_messages_with_16_byte_delivery_tags_by_lock_token()
    {
        var (conn, broker, receiver) = await SetupReceiverWithTagsAsync(
            "q",
            (Guid.Parse("11111111-2222-3333-4444-555555555555").ToByteArray(), EncodeMessage("m1")),
            (Guid.Parse("66666666-7777-8888-9999-aaaaaaaaaaaa").ToByteArray(), EncodeMessage("m2")));
        await using var _c = conn;
        await using var _r = receiver;

        var batch = await receiver.ReceiveBatchAsync(2, TimeSpan.FromSeconds(5)).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(2, batch.Count);
        Assert.Equal(2, receiver.InFlightCount);
        Assert.All(batch, m => Assert.NotNull(m.LockToken));
    }

    [Fact]
    public async Task ReceiveBatchAsync_does_not_cache_messages_with_non_guid_delivery_tags()
    {
        // Default SetupReceiverAsync emits single-byte tags → not GUIDs.
        var (conn, _, receiver, _) = await SetupReceiverAsync("q", EncodeMessage("m1"));
        await using var _c = conn;
        await using var _r = receiver;

        var batch = await receiver.ReceiveBatchAsync(1, TimeSpan.FromSeconds(5)).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Single(batch);
        Assert.Equal(0, receiver.InFlightCount);
    }

    [Fact]
    public async Task CompleteAsync_by_lock_token_settles_and_returns_true()
    {
        var tag = Guid.Parse("aabbccdd-eeff-0011-2233-445566778899").ToByteArray();
        var (conn, broker, receiver) = await SetupReceiverWithTagsAsync("q", (tag, EncodeMessage("m1")));
        await using var _c = conn;
        await using var _r = receiver;

        var batch = await receiver.ReceiveBatchAsync(1, TimeSpan.FromSeconds(5)).WaitAsync(TimeSpan.FromSeconds(15));
        var msg = Assert.Single(batch);

        var settled = await receiver.CompleteAsync(new Guid(tag)).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(settled);
        Assert.Equal(0, receiver.InFlightCount);
        await WaitForDispositionAsync(broker, msg.DeliveryId);
        Assert.Equal(AmqpDispositionOutcome.Accepted, broker.Dispositions[msg.DeliveryId].Outcome);
    }

    [Fact]
    public async Task CompleteAsync_by_unknown_lock_token_returns_false_without_disposition()
    {
        var tag = Guid.Parse("aabbccdd-eeff-0011-2233-445566778899").ToByteArray();
        var (conn, broker, receiver) = await SetupReceiverWithTagsAsync("q", (tag, EncodeMessage("m1")));
        await using var _c = conn;
        await using var _r = receiver;

        await receiver.ReceiveBatchAsync(1, TimeSpan.FromSeconds(5)).WaitAsync(TimeSpan.FromSeconds(15));

        var settled = await receiver
            .CompleteAsync(Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"))
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(settled);
        Assert.Equal(1, receiver.InFlightCount);
        Assert.Empty(broker.Dispositions);
    }

    [Fact]
    public async Task Updated_lock_expiry_prevents_pruning_at_the_original_deadline()
    {
        var token = Guid.Parse("12345678-90ab-cdef-1234-567890abcdef");
        var (conn, _, receiver) = await SetupReceiverWithTagsAsync(
            "q", (token.ToByteArray(), EncodeMessage("m1")));
        await using var _c = conn;
        await using var _r = receiver;
        await receiver.ReceiveBatchAsync(1, TimeSpan.FromSeconds(5)).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.True(receiver.UpdateLockExpiry(token, DateTimeOffset.UtcNow.AddSeconds(-1)));
        Assert.True(receiver.UpdateLockExpiry(token, DateTimeOffset.UtcNow.AddMinutes(1)));
        await receiver.ReceiveBatchAsync(1, TimeSpan.Zero).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, receiver.InFlightCount);
    }

    [Fact]
    public async Task Lock_renewal_lease_pins_delivery_until_renewed_expiry_is_recorded()
    {
        var token = Guid.Parse("12345678-90ab-cdef-1234-567890abcdef");
        var (conn, _, receiver) = await SetupReceiverWithTagsAsync(
            "q", (token.ToByteArray(), EncodeMessage("m1")));
        await using var _c = conn;
        await using var _r = receiver;
        await receiver.ReceiveBatchAsync(1, TimeSpan.FromSeconds(5)).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.True(receiver.UpdateLockExpiry(token, DateTimeOffset.UtcNow.AddMilliseconds(25)));
        using (var renewal = Assert.IsType<ServiceBusReceiver.LockRenewalLease>(
                   receiver.TryBeginLockRenewal(token)))
        {
            await Task.Delay(50);
            await receiver.ReceiveBatchAsync(1, TimeSpan.Zero).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(1, receiver.InFlightCount);
            Assert.True(renewal.Complete(DateTimeOffset.UtcNow.AddMinutes(1), updateSession: false));
        }

        await receiver.ReceiveBatchAsync(1, TimeSpan.Zero).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, receiver.InFlightCount);
    }

    [Fact]
    public async Task Concurrent_session_renewal_responses_never_regress_tracked_expiry()
    {
        var first = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var second = Guid.Parse("66666666-7777-8888-9999-aaaaaaaaaaaa");
        var (conn, _, receiver) = await SetupReceiverWithTagsAsync(
            "q",
            (first.ToByteArray(), EncodeMessage("m1")),
            (second.ToByteArray(), EncodeMessage("m2")));
        await using var _c = conn;
        await using var _r = receiver;
        await receiver.ReceiveBatchAsync(2, TimeSpan.FromSeconds(5)).WaitAsync(TimeSpan.FromSeconds(15));

        using var newer = Assert.IsType<ServiceBusReceiver.LockRenewalLease>(
            receiver.TryBeginLockRenewal(first));
        using var older = Assert.IsType<ServiceBusReceiver.LockRenewalLease>(
            receiver.TryBeginLockRenewal(second));
        Assert.True(newer.Complete(DateTimeOffset.UtcNow.AddMinutes(1), updateSession: true));
        newer.Dispose();
        Assert.True(older.Complete(DateTimeOffset.UtcNow.AddSeconds(-1), updateSession: true));
        older.Dispose();

        await receiver.ReceiveBatchAsync(1, TimeSpan.Zero).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, receiver.InFlightCount);
    }

    [Fact]
    public async Task Failed_concurrent_session_renewal_observes_successful_sibling_expiry()
    {
        var successfulToken = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var failedToken = Guid.Parse("66666666-7777-8888-9999-aaaaaaaaaaaa");
        var (conn, _, receiver) = await SetupReceiverWithTagsAsync(
            "q",
            (successfulToken.ToByteArray(), EncodeMessage("m1")),
            (failedToken.ToByteArray(), EncodeMessage("m2")));
        await using var _c = conn;
        await using var _r = receiver;
        await receiver.ReceiveBatchAsync(2, TimeSpan.FromSeconds(5)).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(receiver.UpdateLockExpiry(
            failedToken, DateTimeOffset.UtcNow.AddMilliseconds(25)));

        using var successful = Assert.IsType<ServiceBusReceiver.LockRenewalLease>(
            receiver.TryBeginLockRenewal(successfulToken));
        using var failed = Assert.IsType<ServiceBusReceiver.LockRenewalLease>(
            receiver.TryBeginLockRenewal(failedToken));
        Assert.True(successful.Complete(
            DateTimeOffset.UtcNow.AddMinutes(1), updateSession: true));
        successful.Dispose();
        await Task.Delay(50);
        failed.Dispose();

        await receiver.ReceiveBatchAsync(1, TimeSpan.Zero).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, receiver.InFlightCount);
        Assert.True(receiver.ContainsLockToken(failedToken));
    }

    [Fact]
    public async Task AbandonAsync_and_DeadLetterAsync_by_lock_token_emit_correct_dispositions()
    {
        var tag1 = Guid.Parse("11111111-1111-1111-1111-111111111111").ToByteArray();
        var tag2 = Guid.Parse("22222222-2222-2222-2222-222222222222").ToByteArray();
        var (conn, broker, receiver) = await SetupReceiverWithTagsAsync(
            "q",
            (tag1, EncodeMessage("m1")),
            (tag2, EncodeMessage("m2")));
        await using var _c = conn;
        await using var _r = receiver;

        var batch = await receiver.ReceiveBatchAsync(2, TimeSpan.FromSeconds(5)).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(2, batch.Count);

        var abandoned = await receiver.AbandonAsync(new Guid(tag1)).WaitAsync(TimeSpan.FromSeconds(5));
        var deadLettered = await receiver.DeadLetterAsync(new Guid(tag2), "TooMany", "exceeded").WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(abandoned);
        Assert.True(deadLettered);
        Assert.Equal(0, receiver.InFlightCount);

        await WaitForDispositionAsync(broker, batch[0].DeliveryId);
        await WaitForDispositionAsync(broker, batch[1].DeliveryId);
        Assert.Equal(AmqpDispositionOutcome.Modified, broker.Dispositions[batch[0].DeliveryId].Outcome);
        Assert.Equal(AmqpDispositionOutcome.Rejected, broker.Dispositions[batch[1].DeliveryId].Outcome);
    }

    [Fact]
    public async Task CompleteAsync_with_message_object_evicts_from_in_flight_cache()
    {
        var tag = Guid.Parse("dededede-dede-dede-dede-dededededede").ToByteArray();
        var (conn, broker, receiver) = await SetupReceiverWithTagsAsync("q", (tag, EncodeMessage("m1")));
        await using var _c = conn;
        await using var _r = receiver;

        var batch = await receiver.ReceiveBatchAsync(1, TimeSpan.FromSeconds(5)).WaitAsync(TimeSpan.FromSeconds(15));
        var msg = Assert.Single(batch);
        Assert.Equal(1, receiver.InFlightCount);

        await receiver.CompleteAsync(msg).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, receiver.InFlightCount);
        await WaitForDispositionAsync(broker, msg.DeliveryId);
    }

    [Fact]
    public async Task Settling_after_lock_token_winner_removed_entry_does_not_double_dispose()
    {
        // Race scenario: CompleteAsync(Guid) wins the TryRemove, then a stale caller
        // re-settles using the message object. The second call must not emit a second
        // disposition against the broker.
        var tag = Guid.Parse("cafecafe-cafe-cafe-cafe-cafecafecafe").ToByteArray();
        var (conn, broker, receiver) = await SetupReceiverWithTagsAsync("q", (tag, EncodeMessage("m1")));
        await using var _c = conn;
        await using var _r = receiver;

        var batch = await receiver.ReceiveBatchAsync(1, TimeSpan.FromSeconds(5)).WaitAsync(TimeSpan.FromSeconds(15));
        var msg = Assert.Single(batch);

        Assert.True(await receiver.CompleteAsync(new Guid(tag)).WaitAsync(TimeSpan.FromSeconds(5)));
        await WaitForDispositionAsync(broker, msg.DeliveryId);

        // Stale double-settle attempts via every message-instance overload — all no-op.
        await receiver.CompleteAsync(msg).WaitAsync(TimeSpan.FromSeconds(5));
        await receiver.AbandonAsync(msg).WaitAsync(TimeSpan.FromSeconds(5));
        await receiver.DeadLetterAsync(msg, "x", "y").WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Single(broker.Dispositions);
        Assert.Equal(AmqpDispositionOutcome.Accepted, broker.Dispositions[msg.DeliveryId].Outcome);
    }

    [Fact]
    public async Task ReceiveBatchAsync_does_not_over_grant_credit_on_repeated_empty_calls()
    {
        // Regression: a shared pooled receiver used to call
        // AmqpLink.GrantCreditAsync(maxMessages) on every receive →
        // empty timeouts left credit outstanding, and subsequent calls
        // additively granted more credit. The fix tops up to maxMessages
        // total in-flight credit instead. We assert the second call does
        // not emit any new flow frame because credit is already adequate.
        var (conn, _broker, receiver, linkName) = await SetupReceiverAsync("q");
        await using var _c = conn;
        await using var _r = receiver;

        var first = await receiver.ReceiveBatchAsync(10, TimeSpan.FromMilliseconds(50)).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(first);

        var flowsAfterFirst = _broker.FlowCreditsByLink.TryGetValue(linkName, out var l) ? l.Count : 0;

        var second = await receiver.ReceiveBatchAsync(10, TimeSpan.FromMilliseconds(50)).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(second);

        var flowsAfterSecond = _broker.FlowCreditsByLink[linkName].Count;
        Assert.Equal(flowsAfterFirst, flowsAfterSecond);
        Assert.Equal(10u, _broker.FlowCreditsByLink[linkName][^1]);
    }

    private static async Task<(ServiceBusAmqpConnection conn, ServiceBusBrokerSimulator broker, ServiceBusReceiver recv)>
        SetupReceiverWithTagsAsync(string queueName, params (byte[] tag, byte[] payload)[] messages)
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
        foreach (var (tag, payload) in messages)
            queue.Enqueue(new ServiceBusBrokerSimulator.DeliveryToSend(tag, payload));
        broker.Inbox[receiver.Link.Name] = queue;
        return (conn, broker, receiver);
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
