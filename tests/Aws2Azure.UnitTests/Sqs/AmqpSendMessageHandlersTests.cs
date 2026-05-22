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
/// Phase 2.7 Slice 2 — AMQP send dispatcher tests. Two layers:
///   * <c>BuildAmqpMessage</c> unit tests assert the SQS → AMQP wire
///     mapping in isolation.
///   * Handler tests stand up an in-process Service Bus broker simulator
///     behind a real <see cref="ServiceBusAmqpSender"/> so the full
///     handler → sender → broker → disposition path is exercised.
/// </summary>
public sealed class AmqpSendMessageHandlersTests
{
    private const string QueueName = "amqp-q";
    private const string FifoQueueName = "amqp-q.fifo";
    private const string Namespace = "ns.servicebus.windows.net";

    // --- BuildAmqpMessage mapping ------------------------------------------

    [Fact]
    public void BuildAmqpMessage_sets_message_id_and_body()
    {
        var msg = AmqpSendMessageHandlers.BuildAmqpMessage(
            body: Encoding.UTF8.GetBytes("hello"),
            attrs: new Dictionary<string, SqsMessageAttribute>(),
            messageId: "id-1",
            groupId: null,
            delaySeconds: 0);

        Assert.Equal("id-1", msg.Properties.MessageId?.ToString());
        Assert.Null(msg.Properties.GroupId);
        Assert.Null(msg.ApplicationProperties);
        Assert.Null(msg.MessageAnnotations);
        Assert.Equal("hello", Encoding.UTF8.GetString(msg.Body.Span));
    }

    [Fact]
    public void BuildAmqpMessage_sets_group_id_for_FIFO()
    {
        var msg = AmqpSendMessageHandlers.BuildAmqpMessage(
            body: Encoding.UTF8.GetBytes("x"),
            attrs: new Dictionary<string, SqsMessageAttribute>(),
            messageId: "dedup-1",
            groupId: "g1",
            delaySeconds: 0);

        Assert.Equal("dedup-1", msg.Properties.MessageId?.ToString());
        Assert.Equal("g1", msg.Properties.GroupId);
    }

    [Fact]
    public void BuildAmqpMessage_encodes_DelaySeconds_as_scheduled_enqueue_time()
    {
        var before = DateTimeOffset.UtcNow;
        var msg = AmqpSendMessageHandlers.BuildAmqpMessage(
            body: Encoding.UTF8.GetBytes("x"),
            attrs: new Dictionary<string, SqsMessageAttribute>(),
            messageId: "m",
            groupId: null,
            delaySeconds: 30);
        var after = DateTimeOffset.UtcNow;

        Assert.NotNull(msg.MessageAnnotations);
        var scheduled = msg.MessageAnnotations!.ScheduledEnqueueTime!.Value;
        Assert.InRange(scheduled,
            before.AddSeconds(30).AddSeconds(-2),
            after.AddSeconds(30).AddSeconds(2));
    }

    [Fact]
    public void BuildAmqpMessage_maps_string_and_binary_attributes_and_emits_type_registry()
    {
        var attrs = new Dictionary<string, SqsMessageAttribute>(StringComparer.Ordinal)
        {
            ["strKey"] = new() { DataType = "String", StringValue = "val" },
            ["numKey"] = new() { DataType = "Number", StringValue = "42" },
            ["binKey"] = new() { DataType = "Binary", BinaryValue = new byte[] { 1, 2, 3 } },
        };

        var msg = AmqpSendMessageHandlers.BuildAmqpMessage(
            body: Encoding.UTF8.GetBytes("x"),
            attrs: attrs,
            messageId: "m",
            groupId: null,
            delaySeconds: 0);

        Assert.NotNull(msg.ApplicationProperties);
        var ap = msg.ApplicationProperties!;
        Assert.Equal("val", (string?)ap["strKey"]);
        Assert.Equal("42", (string?)ap["numKey"]);
        Assert.Equal(Convert.ToBase64String(new byte[] { 1, 2, 3 }), (string?)ap["binKey"]);

        var registry = (string?)ap[SendMessageHandlers.AttrTypesHeader];
        Assert.NotNull(registry);
        // Order-insensitive contains: registry is a comma-separated key=type list.
        Assert.Contains("strKey=String", registry);
        Assert.Contains("numKey=Number", registry);
        Assert.Contains("binKey=Binary", registry);
    }

    // --- Handler dispatch + happy path -------------------------------------

    [Fact]
    public async Task SendMessage_standard_queue_publishes_to_broker_and_returns_md5()
    {
        await using var harness = await SenderHarness.OpenAsync(QueueName);
        var ctx = NewCtx();
        var parsed = QueryParsed(SqsOperation.SendMessage,
            ("QueueUrl", $"https://sqs.us-east-1.amazonaws.com/000000000000/{QueueName}"),
            ("MessageBody", "hello-amqp"));

        await AmqpSendMessageHandlers.HandleAsync(ctx, parsed, harness.Provider, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        var body = ReadBody(ctx);
        Assert.Contains("<SendMessageResponse", body);
        Assert.Contains("<MD5OfMessageBody>", body);
        Assert.Contains("<MessageId>", body);

        var received = await harness.WaitForMessageAsync();
        Assert.Equal("hello-amqp", Encoding.UTF8.GetString(received.Body.Span));
    }

    [Fact]
    public async Task SendMessage_FIFO_uses_dedup_id_as_message_id_and_sets_group_id()
    {
        await using var harness = await SenderHarness.OpenAsync(FifoQueueName);
        var ctx = NewCtx();
        var parsed = QueryParsed(SqsOperation.SendMessage,
            ("QueueUrl", $"https://sqs.us-east-1.amazonaws.com/000000000000/{FifoQueueName}"),
            ("MessageBody", "ordered"),
            ("MessageGroupId", "g1"),
            ("MessageDeduplicationId", "dedup-abc"));

        await AmqpSendMessageHandlers.HandleAsync(ctx, parsed, harness.Provider, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        Assert.Contains("<MessageId>dedup-abc</MessageId>", ReadBody(ctx));

        var received = await harness.WaitForMessageAsync();
        Assert.Equal("dedup-abc", received.Properties.MessageId?.ToString());
        Assert.Equal("g1", received.Properties.GroupId);
    }

    [Fact]
    public async Task SendMessage_rejects_missing_MessageBody()
    {
        await using var harness = await SenderHarness.OpenAsync(QueueName);
        var ctx = NewCtx();
        var parsed = QueryParsed(SqsOperation.SendMessage,
            ("QueueUrl", $"https://sqs.us-east-1.amazonaws.com/000000000000/{QueueName}"));

        await AmqpSendMessageHandlers.HandleAsync(ctx, parsed, harness.Provider, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(StatusCodes.Status400BadRequest, ctx.Response.StatusCode);
        Assert.Contains("MissingParameter", ReadBody(ctx));
    }

    [Fact]
    public async Task SendMessage_FIFO_without_MessageGroupId_returns_MissingParameter()
    {
        await using var harness = await SenderHarness.OpenAsync(FifoQueueName);
        var ctx = NewCtx();
        var parsed = QueryParsed(SqsOperation.SendMessage,
            ("QueueUrl", $"https://sqs.us-east-1.amazonaws.com/000000000000/{FifoQueueName}"),
            ("MessageBody", "x"),
            ("MessageDeduplicationId", "d"));

        await AmqpSendMessageHandlers.HandleAsync(ctx, parsed, harness.Provider, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(StatusCodes.Status400BadRequest, ctx.Response.StatusCode);
        Assert.Contains("MessageGroupId", ReadBody(ctx));
    }

    [Fact]
    public async Task SendMessage_rejects_invalid_DelaySeconds()
    {
        await using var harness = await SenderHarness.OpenAsync(QueueName);
        var ctx = NewCtx();
        var parsed = QueryParsed(SqsOperation.SendMessage,
            ("QueueUrl", $"https://sqs.us-east-1.amazonaws.com/000000000000/{QueueName}"),
            ("MessageBody", "x"),
            ("DelaySeconds", "9999"));

        await AmqpSendMessageHandlers.HandleAsync(ctx, parsed, harness.Provider, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(StatusCodes.Status400BadRequest, ctx.Response.StatusCode);
        Assert.Contains("DelaySeconds", ReadBody(ctx));
    }

    [Fact]
    public async Task SendMessage_propagates_broker_reject_as_error()
    {
        await using var harness = await SenderHarness.OpenAsync(QueueName);
        var linkName = harness.Sender.Link.Name;
        harness.Broker.RejectNextTransferByLink[linkName] = new AmqpError
        {
            Condition = "amqp:internal-error",
            Description = "test reject",
        };

        var ctx = NewCtx();
        var parsed = QueryParsed(SqsOperation.SendMessage,
            ("QueueUrl", $"https://sqs.us-east-1.amazonaws.com/000000000000/{QueueName}"),
            ("MessageBody", "x"));

        await AmqpSendMessageHandlers.HandleAsync(ctx, parsed, harness.Provider, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(StatusCodes.Status400BadRequest, ctx.Response.StatusCode);
        Assert.Contains("Service Bus rejected", ReadBody(ctx));
    }

    // --- harness + fakes ---------------------------------------------------

    private sealed class SenderHarness : IAsyncDisposable
    {
        public required ServiceBusBrokerSimulator Broker { get; init; }
        public required ServiceBusAmqpConnection Connection { get; init; }
        public required ServiceBusAmqpSender Sender { get; init; }
        public required FakeAmqpSenderProvider Provider { get; init; }
        public required string QueueName { get; init; }

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
                QueueName = queueName,
            };
        }

        public async Task<AmqpMessage> WaitForMessageAsync()
        {
            var linkName = Sender.Link.Name;
            for (var i = 0; i < 100; i++)
            {
                if (Broker.ReceivedTransfers.TryGetValue(linkName, out var got) && got.Count > 0)
                    return got[0];
                await Task.Delay(20);
            }
            throw new TimeoutException($"Broker did not receive a transfer on link {linkName}");
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
