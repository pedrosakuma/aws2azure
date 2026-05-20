using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Aws2Azure.Amqp.Connection;
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
/// Slice 8b.4c — AMQP-backed dispatcher tests. Spins up an in-process
/// broker simulator behind a real <see cref="ServiceBusReceiver"/> so
/// the handler exercises the full receive → settle path (including the
/// 8b.4b lock-token cache), then asserts the SQS-shaped responses.
/// </summary>
public sealed class AmqpReceiveMessageHandlersTests
{
    private const string QueueName = "amqp-q";

    [Fact]
    public async Task ReceiveMessage_returns_messages_with_AMQP_receipt_handles()
    {
        await using var harness = await TestHarness.OpenAsync(QueueName,
            (Guid.Parse("11111111-2222-3333-4444-555555555555").ToByteArray(), EncodeMessage("hello-1")),
            (Guid.Parse("66666666-7777-8888-9999-aaaaaaaaaaaa").ToByteArray(), EncodeMessage("hello-2")));

        var ctx = NewCtx();
        var parsed = QueryParsed(SqsOperation.ReceiveMessage,
            ("QueueUrl", $"https://sqs.us-east-1.amazonaws.com/000000000000/{QueueName}"),
            ("MaxNumberOfMessages", "2"));

        await AmqpReceiveMessageHandlers.HandleAsync(ctx, parsed, harness.Provider, CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        var body = ReadBody(ctx);
        Assert.Contains("hello-1", body);
        Assert.Contains("hello-2", body);
        Assert.Contains("<ReceiptHandle>Mjo", body); // v2 AMQP handle prefix
        Assert.Equal(2, harness.Receiver.InFlightCount);
    }

    [Fact]
    public async Task ReceiveMessage_returns_empty_list_when_queue_is_empty()
    {
        await using var harness = await TestHarness.OpenAsync(QueueName);

        var ctx = NewCtx();
        var parsed = QueryParsed(SqsOperation.ReceiveMessage,
            ("QueueUrl", $"https://sqs.us-east-1.amazonaws.com/000000000000/{QueueName}"));

        await AmqpReceiveMessageHandlers.HandleAsync(ctx, parsed, harness.Provider, CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        var body = ReadBody(ctx);
        // ReceiveMessageResponse with no <Message> children.
        Assert.Contains("<ReceiveMessageResponse", body);
        Assert.DoesNotContain("<Message>", body);
    }

    [Fact]
    public async Task ReceiveMessage_rejects_invalid_MaxNumberOfMessages()
    {
        await using var harness = await TestHarness.OpenAsync(QueueName);

        var ctx = NewCtx();
        var parsed = QueryParsed(SqsOperation.ReceiveMessage,
            ("QueueUrl", $"https://sqs.us-east-1.amazonaws.com/000000000000/{QueueName}"),
            ("MaxNumberOfMessages", "999"));

        await AmqpReceiveMessageHandlers.HandleAsync(ctx, parsed, harness.Provider, CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, ctx.Response.StatusCode);
        Assert.Contains("InvalidParameterValue", ReadBody(ctx));
    }

    [Fact]
    public async Task DeleteMessage_settles_in_flight_delivery_via_lock_token_cache()
    {
        var tag = Guid.Parse("aabbccdd-eeff-0011-2233-445566778899").ToByteArray();
        await using var harness = await TestHarness.OpenAsync(QueueName,
            (tag, EncodeMessage("to-delete")));

        // 1) Receive — populates the in-flight cache.
        var receiveCtx = NewCtx();
        await AmqpReceiveMessageHandlers.HandleAsync(receiveCtx,
            QueryParsed(SqsOperation.ReceiveMessage,
                ("QueueUrl", $"https://sqs.us-east-1.amazonaws.com/000000000000/{QueueName}")),
            harness.Provider, CancellationToken.None);
        var handle = ExtractReceiptHandle(ReadBody(receiveCtx));
        Assert.False(string.IsNullOrEmpty(handle));
        Assert.Equal(1, harness.Receiver.InFlightCount);

        // 2) Delete — settles via the cache → returns 200.
        var deleteCtx = NewCtx();
        await AmqpReceiveMessageHandlers.HandleAsync(deleteCtx,
            QueryParsed(SqsOperation.DeleteMessage,
                ("QueueUrl", $"https://sqs.us-east-1.amazonaws.com/000000000000/{QueueName}"),
                ("ReceiptHandle", handle)),
            harness.Provider, CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, deleteCtx.Response.StatusCode);
        Assert.Contains("DeleteMessageResponse", ReadBody(deleteCtx));
        Assert.Equal(0, harness.Receiver.InFlightCount);
    }

    [Fact]
    public async Task DeleteMessage_returns_ReceiptHandleIsInvalid_on_cache_miss()
    {
        await using var harness = await TestHarness.OpenAsync(QueueName);

        var stale = AmqpReceiptHandle.Encode(QueueName, Guid.NewGuid(), DateTimeOffset.UtcNow);
        var ctx = NewCtx();
        await AmqpReceiveMessageHandlers.HandleAsync(ctx,
            QueryParsed(SqsOperation.DeleteMessage,
                ("QueueUrl", $"https://sqs.us-east-1.amazonaws.com/000000000000/{QueueName}"),
                ("ReceiptHandle", stale)),
            harness.Provider, CancellationToken.None);

        Assert.Equal(StatusCodes.Status404NotFound, ctx.Response.StatusCode);
        Assert.Contains("ReceiptHandleIsInvalid", ReadBody(ctx));
    }

    [Fact]
    public async Task DeleteMessage_rejects_handle_minted_against_a_different_queue()
    {
        await using var harness = await TestHarness.OpenAsync(QueueName);

        var crossQueueHandle = AmqpReceiptHandle.Encode("some-other-queue", Guid.NewGuid(), DateTimeOffset.UtcNow);
        var ctx = NewCtx();
        await AmqpReceiveMessageHandlers.HandleAsync(ctx,
            QueryParsed(SqsOperation.DeleteMessage,
                ("QueueUrl", $"https://sqs.us-east-1.amazonaws.com/000000000000/{QueueName}"),
                ("ReceiptHandle", crossQueueHandle)),
            harness.Provider, CancellationToken.None);

        Assert.Equal(StatusCodes.Status404NotFound, ctx.Response.StatusCode);
        Assert.Contains("ReceiptHandleIsInvalid", ReadBody(ctx));
    }

    [Fact]
    public async Task DeleteMessage_rejects_REST_v1_handle()
    {
        await using var harness = await TestHarness.OpenAsync(QueueName);

        var v1Handle = ReceiptHandle.Encode("msg", "tok", "1", DateTimeOffset.UtcNow);
        var ctx = NewCtx();
        await AmqpReceiveMessageHandlers.HandleAsync(ctx,
            QueryParsed(SqsOperation.DeleteMessage,
                ("QueueUrl", $"https://sqs.us-east-1.amazonaws.com/000000000000/{QueueName}"),
                ("ReceiptHandle", v1Handle)),
            harness.Provider, CancellationToken.None);

        Assert.Equal(StatusCodes.Status404NotFound, ctx.Response.StatusCode);
        Assert.Contains("ReceiptHandleIsInvalid", ReadBody(ctx));
    }

    [Fact]
    public async Task ChangeMessageVisibility_with_renewlock_emits_clamp_header_when_granted_differs()
    {
        // Broker grants a ~30s expiry; client requests 300 → divergence
        // surfaces via the Aws2Azure-VisibilityClamped header.
        var grantedExpiry = DateTimeOffset.UtcNow.AddSeconds(30);
        grantedExpiry = DateTimeOffset.FromUnixTimeMilliseconds(grantedExpiry.ToUnixTimeMilliseconds());
        await using var harness = await TestHarness.OpenWithManagementAsync(
            QueueName, renewExpiry: grantedExpiry);

        var handle = AmqpReceiptHandle.Encode(QueueName, Guid.NewGuid(), DateTimeOffset.UtcNow);
        var ctx = NewCtx();
        await AmqpReceiveMessageHandlers.HandleAsync(ctx,
            QueryParsed(SqsOperation.ChangeMessageVisibility,
                ("QueueUrl", $"https://sqs.us-east-1.amazonaws.com/000000000000/{QueueName}"),
                ("ReceiptHandle", handle),
                ("VisibilityTimeout", "300")),
            harness.Provider, CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        var clamp = ctx.Response.Headers["Aws2Azure-VisibilityClamped"].ToString();
        Assert.StartsWith("requested=300;granted=", clamp);
        Assert.Contains("ChangeMessageVisibilityResponse", ReadBody(ctx));
    }

    [Fact]
    public async Task ChangeMessageVisibility_maps_lock_lost_to_MessageNotInflight()
    {
        await using var harness = await TestHarness.OpenWithManagementAsync(
            QueueName,
            renewExpiry: DateTimeOffset.UtcNow,
            statusCode: 410,
            statusDescription: "MessageLockLost",
            errorCondition: "com.microsoft:message-lock-lost");

        var handle = AmqpReceiptHandle.Encode(QueueName, Guid.NewGuid(), DateTimeOffset.UtcNow);
        var ctx = NewCtx();
        await AmqpReceiveMessageHandlers.HandleAsync(ctx,
            QueryParsed(SqsOperation.ChangeMessageVisibility,
                ("QueueUrl", $"https://sqs.us-east-1.amazonaws.com/000000000000/{QueueName}"),
                ("ReceiptHandle", handle),
                ("VisibilityTimeout", "30")),
            harness.Provider, CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, ctx.Response.StatusCode);
        Assert.Contains("MessageNotInflight", ReadBody(ctx));
    }

    [Fact]
    public async Task ChangeMessageVisibility_zero_calls_Abandon_on_receiver()
    {
        // visibility=0 → no $management round-trip; goes straight to
        // ServiceBusReceiver.AbandonAsync. Receive first so the lock
        // token lives in the receiver's in-flight cache; otherwise
        // Abandon returns false and we'd see MessageNotInflight.
        var tag = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee").ToByteArray();
        await using var harness = await TestHarness.OpenAsync(QueueName,
            (tag, EncodeMessage("to-abandon")));

        var rcv = NewCtx();
        await AmqpReceiveMessageHandlers.HandleAsync(rcv,
            QueryParsed(SqsOperation.ReceiveMessage,
                ("QueueUrl", $"https://sqs.us-east-1.amazonaws.com/000000000000/{QueueName}")),
            harness.Provider, CancellationToken.None);
        var handle = ExtractReceiptHandle(ReadBody(rcv));
        Assert.Equal(1, harness.Receiver.InFlightCount);

        var ctx = NewCtx();
        await AmqpReceiveMessageHandlers.HandleAsync(ctx,
            QueryParsed(SqsOperation.ChangeMessageVisibility,
                ("QueueUrl", $"https://sqs.us-east-1.amazonaws.com/000000000000/{QueueName}"),
                ("ReceiptHandle", handle),
                ("VisibilityTimeout", "0")),
            harness.Provider, CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        Assert.False(ctx.Response.Headers.ContainsKey("Aws2Azure-VisibilityClamped"));
        Assert.Equal(0, harness.Receiver.InFlightCount); // Abandon removed it.
        Assert.Contains("ChangeMessageVisibilityResponse", ReadBody(ctx));
    }

    [Fact]
    public async Task ChangeMessageVisibility_rejects_invalid_VisibilityTimeout()
    {
        await using var harness = await TestHarness.OpenAsync(QueueName);

        var handle = AmqpReceiptHandle.Encode(QueueName, Guid.NewGuid(), DateTimeOffset.UtcNow);
        var ctx = NewCtx();
        await AmqpReceiveMessageHandlers.HandleAsync(ctx,
            QueryParsed(SqsOperation.ChangeMessageVisibility,
                ("QueueUrl", $"https://sqs.us-east-1.amazonaws.com/000000000000/{QueueName}"),
                ("ReceiptHandle", handle),
                ("VisibilityTimeout", "99999")),
            harness.Provider, CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, ctx.Response.StatusCode);
        Assert.Contains("InvalidParameterValue", ReadBody(ctx));
    }

    // --- FIFO (session-bound) receive ---------------------------------

    private const string FifoQueueName = "orders.fifo";

    [Fact]
    public async Task ReceiveMessage_on_fifo_queue_acquires_broker_assigned_session_and_mints_v3_handle()
    {
        await using var harness = await TestHarness.OpenSessionAsync(
            FifoQueueName, sessionId: "group-A",
            (Guid.Parse("11111111-2222-3333-4444-555555555555").ToByteArray(), EncodeMessage("fifo-msg-1", groupId: "group-A")));

        var ctx = NewCtx();
        var parsed = QueryParsed(SqsOperation.ReceiveMessage,
            ("QueueUrl", $"https://sqs.us-east-1.amazonaws.com/000000000000/{FifoQueueName}"),
            ("AttributeName.1", "All"));

        await AmqpReceiveMessageHandlers.HandleAsync(ctx, parsed, harness.Provider, CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        Assert.Equal(1, harness.Provider.AcquireSessionCount);
        var body = ReadBody(ctx);
        Assert.Contains("fifo-msg-1", body);
        // v3 receipt handle (session-bound) — base64 prefix "Mzo".
        Assert.Contains("<ReceiptHandle>Mzo", body);
        // MessageGroupId surfaced from the message's properties.group-id.
        Assert.Contains("<Name>MessageGroupId</Name><Value>group-A</Value>", body);
    }

    [Fact]
    public async Task ReceiveMessage_on_fifo_queue_falls_back_to_receiver_session_id_when_group_id_absent()
    {
        // Producer that does not stamp properties.group-id explicitly:
        // the bound session-id on the receiver is still the right
        // MessageGroupId for SQS clients (SB couples the two anyway).
        await using var harness = await TestHarness.OpenSessionAsync(
            FifoQueueName, sessionId: "group-B",
            (Guid.Parse("22222222-3333-4444-5555-666666666666").ToByteArray(), EncodeMessage("fifo-msg-2")));

        var ctx = NewCtx();
        var parsed = QueryParsed(SqsOperation.ReceiveMessage,
            ("QueueUrl", $"https://sqs.us-east-1.amazonaws.com/000000000000/{FifoQueueName}"),
            ("AttributeName.1", "MessageGroupId"));

        await AmqpReceiveMessageHandlers.HandleAsync(ctx, parsed, harness.Provider, CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        var body = ReadBody(ctx);
        Assert.Contains("<Name>MessageGroupId</Name><Value>group-B</Value>", body);
    }

    [Fact]
    public async Task ReceiveMessage_on_non_fifo_queue_does_not_call_session_acquire()
    {
        await using var harness = await TestHarness.OpenAsync(QueueName,
            (Guid.Parse("33333333-4444-5555-6666-777777777777").ToByteArray(), EncodeMessage("std-msg")));

        var ctx = NewCtx();
        var parsed = QueryParsed(SqsOperation.ReceiveMessage,
            ("QueueUrl", $"https://sqs.us-east-1.amazonaws.com/000000000000/{QueueName}"));

        await AmqpReceiveMessageHandlers.HandleAsync(ctx, parsed, harness.Provider, CancellationToken.None);

        Assert.Equal(0, harness.Provider.AcquireSessionCount);
        var body = ReadBody(ctx);
        // Non-FIFO keeps emitting the v2 (non-session) receipt handle prefix.
        Assert.Contains("<ReceiptHandle>Mjo", body);
        Assert.DoesNotContain("Mzo", body);
    }

    // --- harness -------------------------------------------------------

    private sealed class TestHarness : IAsyncDisposable
    {
        public required ServiceBusAmqpConnection Connection { get; init; }
        public required ServiceBusReceiver Receiver { get; init; }
        public required FakeAmqpReceiverProvider Provider { get; init; }
        public required ServiceBusBrokerSimulator Broker { get; init; }
        public Aws2Azure.Amqp.Connection.AmqpConnection? MgmtConnection { get; init; }
        public Aws2Azure.Amqp.Connection.AmqpSession? MgmtSession { get; init; }
        public ServiceBusManagementClient? Management { get; init; }
        public Task<Guid[]?>? MgmtBrokerTask { get; init; }
        public Aws2Azure.Amqp.Transport.IAmqpTransport? MgmtServer { get; init; }

        public static async Task<TestHarness> OpenAsync(string queueName,
            params (byte[] tag, byte[] payload)[] messages)
            => await OpenInternalAsync(queueName, sessionId: null, messages);

        /// <summary>
        /// Opens a session-bound receiver against the broker simulator.
        /// The simulator binds the requested session-id verbatim, so the
        /// returned <see cref="ServiceBusReceiver.SessionId"/> equals
        /// <paramref name="sessionId"/>. Use this to exercise the FIFO
        /// receive path (slice 7c.3c).
        /// </summary>
        public static async Task<TestHarness> OpenSessionAsync(string queueName, string sessionId,
            params (byte[] tag, byte[] payload)[] messages)
            => await OpenInternalAsync(queueName, sessionId: sessionId, messages);

        private static async Task<TestHarness> OpenInternalAsync(string queueName, string? sessionId,
            (byte[] tag, byte[] payload)[] messages)
        {
            var (client, server) = PipePairTransport.CreatePair();
            var broker = new ServiceBusBrokerSimulator(server);
            broker.Start();
            var conn = await ServiceBusAmqpConnection
                .OpenAsync(client, new FakeTokenProvider(), new AmqpConnectionSettings
                {
                    ContainerId = "test-client",
                    Hostname = "ns.servicebus.windows.net",
                    IdleTimeout = TimeSpan.Zero,
                })
                .WaitAsync(TimeSpan.FromSeconds(10));
            var audience = ServiceBusEndpoint.BuildQueueAudience("ns.servicebus.windows.net", queueName);

            ServiceBusReceiver receiver;
            if (sessionId is null)
            {
                receiver = await conn.OpenReceiverAsync(queueName, audience, prefetchCredit: 0)
                    .WaitAsync(TimeSpan.FromSeconds(10));
            }
            else
            {
                receiver = await conn.OpenSessionReceiverAsync(queueName, audience, sessionId, prefetchCredit: 0)
                    .WaitAsync(TimeSpan.FromSeconds(10));
            }

            var inbox = new Queue<ServiceBusBrokerSimulator.DeliveryToSend>();
            foreach (var (tag, payload) in messages)
                inbox.Enqueue(new ServiceBusBrokerSimulator.DeliveryToSend(tag, payload));
            broker.Inbox[receiver.Link.Name] = inbox;

            return new TestHarness
            {
                Connection = conn,
                Receiver = receiver,
                Broker = broker,
                Provider = new FakeAmqpReceiverProvider(queueName, receiver,
                    brokerAssignedSessionReceiver: sessionId is null ? null : receiver),
            };
        }

        /// <summary>
        /// Opens a queue harness plus a dedicated management-client
        /// fixture (separate pipe-pair + AmqpConnection driven by
        /// <see cref="Aws2Azure.UnitTests.Amqp.ServiceBus.ManagementBrokerSimulator"/>).
        /// </summary>
        public static async Task<TestHarness> OpenWithManagementAsync(
            string queueName,
            DateTimeOffset renewExpiry,
            int statusCode = 200,
            string? statusDescription = "OK",
            string? errorCondition = null,
            params (byte[] tag, byte[] payload)[] messages)
        {
            var baseHarness = await OpenAsync(queueName, messages);

            var (mgmtClient, mgmtServer) = PipePairTransport.CreatePair();
            var mgmtBrokerTask = Task.Run(async () =>
                await Aws2Azure.UnitTests.Amqp.ServiceBus.ManagementBrokerSimulator.RunFullAsync(
                    mgmtServer, renewExpiry, statusCode, statusDescription, errorCondition,
                    captureOperation: _ => { }));

            var mgmtConn = new Aws2Azure.Amqp.Connection.AmqpConnection(mgmtClient,
                new AmqpConnectionSettings
                {
                    ContainerId = "test-client-mgmt",
                    Hostname = "ns.servicebus.windows.net",
                    IdleTimeout = TimeSpan.Zero,
                });
            await mgmtConn.OpenAsync();
            var mgmtSession = await mgmtConn.BeginSessionAsync();
            var mgmt = await ServiceBusManagementClient.OpenAsync(mgmtSession);

            return new TestHarness
            {
                Connection = baseHarness.Connection,
                Receiver = baseHarness.Receiver,
                Broker = baseHarness.Broker,
                Provider = new FakeAmqpReceiverProvider(queueName, baseHarness.Receiver, mgmt),
                MgmtConnection = mgmtConn,
                MgmtSession = mgmtSession,
                Management = mgmt,
                MgmtBrokerTask = mgmtBrokerTask,
                MgmtServer = mgmtServer,
            };
        }

        public async ValueTask DisposeAsync()
        {
            if (Management is not null) await Management.DisposeAsync();
            if (MgmtSession is not null) await MgmtSession.CloseAsync();
            if (MgmtConnection is not null) await MgmtConnection.CloseAsync();
            if (MgmtBrokerTask is not null)
            {
                try { await MgmtBrokerTask.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            }
            if (MgmtServer is not null) await MgmtServer.DisposeAsync();
            await Receiver.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }

    private sealed class FakeAmqpReceiverProvider : IAmqpReceiverProvider
    {
        private readonly string _expectedQueue;
        private readonly ServiceBusReceiver _receiver;
        private readonly ServiceBusReceiver? _brokerAssignedSessionReceiver;
        private readonly ServiceBusManagementClient? _management;
        public int InvalidateCount { get; private set; }
        public int InvalidateManagementCount { get; private set; }
        public int InvalidateSessionCount { get; private set; }
        public int AcquireSessionCount { get; private set; }

        public FakeAmqpReceiverProvider(string queueName, ServiceBusReceiver receiver,
            ServiceBusManagementClient? management = null,
            ServiceBusReceiver? brokerAssignedSessionReceiver = null)
        {
            _expectedQueue = queueName;
            _receiver = receiver;
            _management = management;
            _brokerAssignedSessionReceiver = brokerAssignedSessionReceiver;
        }

        public Task<ServiceBusReceiver> GetReceiverAsync(string queueName, CancellationToken cancellationToken)
        {
            Assert.Equal(_expectedQueue, queueName);
            return Task.FromResult(_receiver);
        }

        public Task<ServiceBusManagementClient> GetManagementClientAsync(string queueName, CancellationToken cancellationToken)
        {
            Assert.Equal(_expectedQueue, queueName);
            if (_management is null)
                throw new InvalidOperationException("Test harness did not wire a management client.");
            return Task.FromResult(_management);
        }

        public Task InvalidateAsync(string queueName, bool closeConnection)
        {
            InvalidateCount++;
            return Task.CompletedTask;
        }

        public Task InvalidateManagementClientAsync(string queueName)
        {
            InvalidateManagementCount++;
            return Task.CompletedTask;
        }

        public Task<ServiceBusReceiver> GetSessionReceiverAsync(
            string queueName, string sessionId, CancellationToken cancellationToken)
        {
            // Slice 7c.3d will exercise the (queue, sessionId)-keyed
            // settle path; until then GetSessionReceiverAsync is only
            // reached if a v3 receipt handle is decoded — return the
            // same session receiver the acquire path produced when
            // available.
            Assert.Equal(_expectedQueue, queueName);
            if (_brokerAssignedSessionReceiver is null)
                throw new NotSupportedException("Test harness did not wire a session receiver.");
            return Task.FromResult(_brokerAssignedSessionReceiver);
        }

        public Task<ServiceBusReceiver> AcquireBrokerAssignedSessionReceiverAsync(
            string queueName, CancellationToken cancellationToken)
        {
            Assert.Equal(_expectedQueue, queueName);
            if (_brokerAssignedSessionReceiver is null)
                throw new NotSupportedException("Test harness did not wire a broker-assigned session receiver.");
            AcquireSessionCount++;
            return Task.FromResult(_brokerAssignedSessionReceiver);
        }

        public Task InvalidateSessionReceiverAsync(string queueName, string sessionId)
        {
            InvalidateSessionCount++;
            return Task.CompletedTask;
        }
    }

    // --- shared helpers ------------------------------------------------

    private static byte[] EncodeMessage(string body)
        => EncodeMessage(body, groupId: null);

    private static byte[] EncodeMessage(string body, string? groupId)
    {
        var msg = new AmqpMessage { Body = Encoding.UTF8.GetBytes(body) };
        if (!string.IsNullOrEmpty(groupId))
            msg.Properties = msg.Properties with { GroupId = groupId };
        var rented = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            msg.Write(rented, out var written);
            return rented.AsSpan(0, written).ToArray();
        }
        finally { ArrayPool<byte>.Shared.Return(rented); }
    }

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

    private static string ExtractReceiptHandle(string xml)
    {
        const string open = "<ReceiptHandle>";
        const string close = "</ReceiptHandle>";
        var i = xml.IndexOf(open, StringComparison.Ordinal);
        if (i < 0) return string.Empty;
        var j = xml.IndexOf(close, i, StringComparison.Ordinal);
        if (j < 0) return string.Empty;
        return xml.Substring(i + open.Length, j - i - open.Length);
    }
}
