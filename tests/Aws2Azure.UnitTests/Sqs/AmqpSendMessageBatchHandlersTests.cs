using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Aws2Azure.Amqp.Connection;
using Aws2Azure.Amqp.Framing;
using Aws2Azure.Amqp.ServiceBus;
using Aws2Azure.Modules.Sqs;
using Aws2Azure.Modules.Sqs.Internal;
using Aws2Azure.Modules.Sqs.Operations;
using Aws2Azure.Modules.Sqs.WireProtocol;
using Aws2Azure.UnitTests.Amqp.ServiceBus;
using Aws2Azure.UnitTests.Amqp.Transport;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Aws2Azure.UnitTests.Sqs;

/// <summary>
/// Phase 2.7 Slice 3 — AMQP send-batch handler tests. Exercises the
/// per-entry disposition path via the in-process broker simulator and
/// asserts the SQS response shape (Successful / Failed split).
/// </summary>
[Collection(SqsAmqpTestCollection.Name)]
public sealed class AmqpSendMessageBatchHandlersTests
{
    private const string QueueName = "amqp-q";
    private const string FifoQueueName = "amqp-q.fifo";
    private const string Namespace = "ns.servicebus.windows.net";

    [Fact]
    public async Task SendMessageBatch_publishes_every_entry_and_returns_one_result_per_id()
    {
        await using var harness = await SenderHarness.OpenAsync(QueueName);
        var ctx = NewCtx();
        var parsed = QueryParsed(SqsOperation.SendMessageBatch,
            ("QueueUrl", $"https://sqs.us-east-1.amazonaws.com/000000000000/{QueueName}"),
            ("SendMessageBatchRequestEntry.1.Id", "e1"),
            ("SendMessageBatchRequestEntry.1.MessageBody", "first"),
            ("SendMessageBatchRequestEntry.2.Id", "e2"),
            ("SendMessageBatchRequestEntry.2.MessageBody", "second"),
            ("SendMessageBatchRequestEntry.3.Id", "e3"),
            ("SendMessageBatchRequestEntry.3.MessageBody", "third"));

        await AmqpSendMessageBatchHandlers.HandleAsync(ctx, parsed, harness.Provider, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        var body = ReadBody(ctx);
        Assert.Contains("<SendMessageBatchResponse", body);
        // All three should be in <SendMessageBatchResultEntry>.
        Assert.Contains("<Id>e1</Id>", body);
        Assert.Contains("<Id>e2</Id>", body);
        Assert.Contains("<Id>e3</Id>", body);
        Assert.DoesNotContain("<BatchResultErrorEntry>", body);

        var received = await harness.WaitForMessagesAsync(3);
        var payloads = received.ConvertAll(m => Encoding.UTF8.GetString(m.Body.Span));
        Assert.Contains("first", payloads);
        Assert.Contains("second", payloads);
        Assert.Contains("third", payloads);
    }

    [Fact]
    public async Task SendMessageBatch_partial_failure_marks_only_rejected_entry_failed()
    {
        await using var harness = await SenderHarness.OpenAsync(QueueName);

        // Reject the FIRST transfer on the link. The simulator's
        // RejectNextTransferByLink is consumed once, so subsequent
        // transfers go through as Accepted — giving us a 1-fail / 2-pass
        // batch.
        harness.Broker.RejectNextTransferByLink[harness.Sender.Link.Name] = new AmqpError
        {
            Condition = "amqp:internal-error",
            Description = "rejected first",
        };

        var ctx = NewCtx();
        var parsed = QueryParsed(SqsOperation.SendMessageBatch,
            ("QueueUrl", $"https://sqs.us-east-1.amazonaws.com/000000000000/{QueueName}"),
            ("SendMessageBatchRequestEntry.1.Id", "e1"),
            ("SendMessageBatchRequestEntry.1.MessageBody", "first"),
            ("SendMessageBatchRequestEntry.2.Id", "e2"),
            ("SendMessageBatchRequestEntry.2.MessageBody", "second"),
            ("SendMessageBatchRequestEntry.3.Id", "e3"),
            ("SendMessageBatchRequestEntry.3.MessageBody", "third"));

        await AmqpSendMessageBatchHandlers.HandleAsync(ctx, parsed, harness.Provider, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        var body = ReadBody(ctx);
        // The rejected entry surfaces as a BatchResultErrorEntry with
        // its original id; the other two remain successful.
        Assert.Contains("<BatchResultErrorEntry>", body);
        Assert.Contains("Service Bus rejected", body);
        Assert.Contains("<Id>e2</Id>", body);
        Assert.Contains("<Id>e3</Id>", body);
    }

    [Fact]
    public async Task SendMessageBatch_FIFO_uses_dedup_id_per_entry_as_message_id()
    {
        await using var harness = await SenderHarness.OpenAsync(FifoQueueName);
        var ctx = NewCtx();
        var parsed = QueryParsed(SqsOperation.SendMessageBatch,
            ("QueueUrl", $"https://sqs.us-east-1.amazonaws.com/000000000000/{FifoQueueName}"),
            ("SendMessageBatchRequestEntry.1.Id", "e1"),
            ("SendMessageBatchRequestEntry.1.MessageBody", "first"),
            ("SendMessageBatchRequestEntry.1.MessageGroupId", "g"),
            ("SendMessageBatchRequestEntry.1.MessageDeduplicationId", "dedup-1"),
            ("SendMessageBatchRequestEntry.2.Id", "e2"),
            ("SendMessageBatchRequestEntry.2.MessageBody", "second"),
            ("SendMessageBatchRequestEntry.2.MessageGroupId", "g"),
            ("SendMessageBatchRequestEntry.2.MessageDeduplicationId", "dedup-2"));

        await AmqpSendMessageBatchHandlers.HandleAsync(ctx, parsed, harness.Provider, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        var body = ReadBody(ctx);
        Assert.Contains("<MessageId>dedup-1</MessageId>", body);
        Assert.Contains("<MessageId>dedup-2</MessageId>", body);

        var received = await harness.WaitForMessagesAsync(2);
        var messageIds = received.ConvertAll(m => m.Properties.MessageId?.ToString());
        Assert.Equal(new[] { "dedup-1", "dedup-2" }, messageIds);
        Assert.All(received, m => Assert.Equal("g", m.Properties.GroupId));
    }

    [Fact]
    public async Task SendMessageBatch_rejects_empty_batch()
    {
        await using var harness = await SenderHarness.OpenAsync(QueueName);
        var ctx = NewCtx();
        var parsed = QueryParsed(SqsOperation.SendMessageBatch,
            ("QueueUrl", $"https://sqs.us-east-1.amazonaws.com/000000000000/{QueueName}"));

        await AmqpSendMessageBatchHandlers.HandleAsync(ctx, parsed, harness.Provider, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(StatusCodes.Status400BadRequest, ctx.Response.StatusCode);
        Assert.Contains("EmptyBatchRequest", ReadBody(ctx));
    }

    [Fact]
    public async Task SendMessageBatch_rejects_duplicate_entry_ids()
    {
        await using var harness = await SenderHarness.OpenAsync(QueueName);
        var ctx = NewCtx();
        var parsed = QueryParsed(SqsOperation.SendMessageBatch,
            ("QueueUrl", $"https://sqs.us-east-1.amazonaws.com/000000000000/{QueueName}"),
            ("SendMessageBatchRequestEntry.1.Id", "same"),
            ("SendMessageBatchRequestEntry.1.MessageBody", "a"),
            ("SendMessageBatchRequestEntry.2.Id", "same"),
            ("SendMessageBatchRequestEntry.2.MessageBody", "b"));

        await AmqpSendMessageBatchHandlers.HandleAsync(ctx, parsed, harness.Provider, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(StatusCodes.Status400BadRequest, ctx.Response.StatusCode);
        Assert.Contains("BatchEntryIdsNotDistinct", ReadBody(ctx));
    }

    [Fact]
    public async Task SendMessageBatch_rejects_FIFO_entry_without_MessageGroupId()
    {
        await using var harness = await SenderHarness.OpenAsync(FifoQueueName);
        var ctx = NewCtx();
        var parsed = QueryParsed(SqsOperation.SendMessageBatch,
            ("QueueUrl", $"https://sqs.us-east-1.amazonaws.com/000000000000/{FifoQueueName}"),
            ("SendMessageBatchRequestEntry.1.Id", "e1"),
            ("SendMessageBatchRequestEntry.1.MessageBody", "a"),
            ("SendMessageBatchRequestEntry.1.MessageDeduplicationId", "d1"));

        await AmqpSendMessageBatchHandlers.HandleAsync(ctx, parsed, harness.Provider, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(StatusCodes.Status400BadRequest, ctx.Response.StatusCode);
        Assert.Contains("MessageGroupId", ReadBody(ctx));
    }

    // --- harness + fakes ---------------------------------------------------

    private sealed class SenderHarness : IAsyncDisposable
    {
        public required ServiceBusBrokerSimulator Broker { get; init; }
        public required ServiceBusAmqpConnection Connection { get; init; }
        public required ServiceBusAmqpSender Sender { get; init; }
        public required FakeAmqpSenderProvider Provider { get; init; }

        public static async Task<SenderHarness> OpenAsync(string queueName)
        {
            var (client, server) = PipePairTransport.CreatePair();
            var broker = new ServiceBusBrokerSimulator(server);
            broker.Start();

            var conn = await ServiceBusAmqpConnection
                .OpenAsync(client, new FakeTokenProvider(), new AmqpConnectionSettings
                {
                    ContainerId = "test-client",
                    Hostname = Namespace,
                    IdleTimeout = TimeSpan.Zero,
                })
                .WaitAsync(TimeSpan.FromSeconds(10));

            var audience = ServiceBusEndpoint.BuildQueueAudience(Namespace, queueName);
            var sender = await conn.OpenSenderAsync(queueName, audience)
                .WaitAsync(TimeSpan.FromSeconds(10));

            return new SenderHarness
            {
                Broker = broker,
                Connection = conn,
                Sender = sender,
                Provider = new FakeAmqpSenderProvider(queueName, sender),
            };
        }

        public async Task<List<AmqpMessage>> WaitForMessagesAsync(int expected)
        {
            var linkName = Sender.Link.Name;
            for (var i = 0; i < 200; i++)
            {
                if (Broker.ReceivedTransfers.TryGetValue(linkName, out var got) && got.Count >= expected)
                    return got;
                await Task.Delay(20);
            }
            throw new TimeoutException(
                $"Broker did not receive {expected} transfers on link {linkName}");
        }

        public async ValueTask DisposeAsync()
        {
            await Sender.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }

    private sealed class FakeAmqpSenderProvider : IAmqpSenderProvider
    {
        private readonly string _expectedQueue;
        private readonly ServiceBusAmqpSender _sender;
        public int InvalidateCount { get; private set; }

        public FakeAmqpSenderProvider(string queueName, ServiceBusAmqpSender sender)
        {
            _expectedQueue = queueName;
            _sender = sender;
        }

        public Task<ServiceBusAmqpSender> GetSenderAsync(string queueName, CancellationToken cancellationToken)
        {
            Assert.Equal(_expectedQueue, queueName);
            return Task.FromResult(_sender);
        }

        public Task InvalidateSenderAsync(string queueName, bool closeConnection)
        {
            InvalidateCount++;
            return Task.CompletedTask;
        }
    }

    // --- shared helpers ----------------------------------------------------

    private static HttpContext NewCtx()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "POST";
        ctx.Request.Scheme = "https";
        ctx.Request.Host = new HostString("sqs.us-east-1.amazonaws.com");
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    private static SqsParseResult QueryParsed(SqsOperation op, params (string Name, string Value)[] kv)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in kv) dict[k] = v;
        return new SqsParseResult(SqsWireProtocol.Query, op, dict, JsonBody: null, Error: null);
    }

    private static string ReadBody(HttpContext ctx)
    {
        ctx.Response.Body.Position = 0;
        using var reader = new StreamReader(ctx.Response.Body);
        return reader.ReadToEnd();
    }
}
