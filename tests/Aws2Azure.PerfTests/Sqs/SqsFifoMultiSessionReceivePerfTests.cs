using System.Buffers;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
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

namespace Aws2Azure.PerfTests.Sqs;

/// <summary>
/// In-proc perf guard for the FIFO accumulation loop (#891/#892). Uses the
/// unit-test AMQP broker simulator rather than the Service Bus emulator
/// because the emulator still omits the broker-assigned session-id on
/// AcceptNextSession; the simulator gives a stable, repeatable receive path
/// for regression detection while the real-Azure conformance scenario carries
/// the end-to-end contract proof.
/// </summary>
public sealed class SqsFifoMultiSessionReceivePerfTests
{
    private const string QueueName = "perf-orders.fifo";
    private const int SessionCount = 8;
    private const int MaxMessagesPerReceive = 8;

    [SkippableFact]
    public async Task ReceiveMessage_delete_batch_throughput_fifo_multi_session()
    {
        Skip.IfNot(PerfGate.Enabled, "AWS2AZURE_PERF=1 not set.");

        await using var harness = await MultiSessionHarness.OpenAsync(
            queueName: QueueName,
            sessionCount: SessionCount).ConfigureAwait(false);

        var result = await PerfRunner.RunAsync(
            scenario: "sqs.ReceiveMessage+DeleteMessageBatch (8, fifo multi-session)",
            concurrency: 1,
            duration: TimeSpan.FromSeconds(12),
            warmup: TimeSpan.FromSeconds(2),
            action: async (_, ct) =>
            {
                var receiveCtx = NewCtx();
                await AmqpReceiveMessageHandlers.HandleAsync(
                    receiveCtx,
                    QueryParsed(
                        SqsOperation.ReceiveMessage,
                        ("QueueUrl", QueueUrl(QueueName)),
                        ("MaxNumberOfMessages", MaxMessagesPerReceive.ToString()),
                        ("WaitTimeSeconds", "1")),
                    harness.Provider,
                    ct).ConfigureAwait(false);
                EnsureSuccess(receiveCtx, "ReceiveMessage");

                var handles = ExtractAllReceiptHandles(ReadBody(receiveCtx));
                if (handles.Count == 0)
                    return;

                var deleteParams = new (string Name, string Value)[1 + (handles.Count * 2)];
                deleteParams[0] = ("QueueUrl", QueueUrl(QueueName));
                for (var i = 0; i < handles.Count; i++)
                {
                    var index = i + 1;
                    deleteParams[1 + (i * 2)] = ($"DeleteMessageBatchRequestEntry.{index}.Id", $"d{index}");
                    deleteParams[2 + (i * 2)] = ($"DeleteMessageBatchRequestEntry.{index}.ReceiptHandle", handles[i]);
                }

                var deleteCtx = NewCtx();
                await AmqpReceiveMessageHandlers.HandleAsync(
                    deleteCtx,
                    QueryParsed(SqsOperation.DeleteMessageBatch, deleteParams),
                    harness.Provider,
                    ct).ConfigureAwait(false);
                EnsureSuccess(deleteCtx, "DeleteMessageBatch");

                for (var i = 0; i < handles.Count; i++)
                {
                    Assert.True(AmqpReceiptHandle.TryDecode(handles[i], out var decoded));
                    await harness.ReplenishAsync(decoded.SessionId!, ct).ConfigureAwait(false);
                }
            }).ConfigureAwait(false);

        PerfReport.Append(
            result,
            notes: "SQS FIFO multi-session receive+DeleteMessageBatch over the in-proc AMQP broker simulator — 8 active MessageGroupIds, one visible delivery per group, ReceiveMessage(MaxNumberOfMessages=8) exercises broker-assigned cross-session accumulation and the mixed-session batch settle path.");
        result.AssertHealthy();
        result.AssertNoRegression();
    }

    private static void EnsureSuccess(HttpContext context, string operation)
    {
        if (context.Response.StatusCode == StatusCodes.Status200OK)
            return;

        throw new InvalidOperationException(
            $"{operation} returned {(int)context.Response.StatusCode}: {ReadBody(context)}");
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
        for (var i = 0; i < kv.Length; i++)
        {
            dict[kv[i].Name] = kv[i].Value;
        }
        return new SqsParseResult(SqsWireProtocol.Query, op, dict, JsonBody: null, Error: null);
    }

    private static string QueueUrl(string queueName)
        => $"https://sqs.us-east-1.amazonaws.com/000000000000/{queueName}";

    private static string ReadBody(HttpContext ctx)
    {
        ctx.Response.Body.Position = 0;
        using var reader = new StreamReader(ctx.Response.Body, leaveOpen: true);
        return reader.ReadToEnd();
    }

    private static List<string> ExtractAllReceiptHandles(string xml)
    {
        const string open = "<ReceiptHandle>";
        const string close = "</ReceiptHandle>";
        var handles = new List<string>();
        var cursor = 0;
        while (true)
        {
            var start = xml.IndexOf(open, cursor, StringComparison.Ordinal);
            if (start < 0)
                return handles;

            var end = xml.IndexOf(close, start, StringComparison.Ordinal);
            if (end < 0)
                return handles;

            handles.Add(xml.Substring(start + open.Length, end - start - open.Length));
            cursor = end + close.Length;
        }
    }

    private sealed class MultiSessionHarness : IAsyncDisposable
    {
        private readonly ConcurrentDictionary<string, SessionHarness> _sessions;

        private MultiSessionHarness(
            PerfAmqpReceiverProvider provider,
            ConcurrentDictionary<string, SessionHarness> sessions)
        {
            Provider = provider;
            _sessions = sessions;
        }

        public PerfAmqpReceiverProvider Provider { get; }

        public static async Task<MultiSessionHarness> OpenAsync(
            string queueName,
            int sessionCount)
        {
            var sessions = new ConcurrentDictionary<string, SessionHarness>(StringComparer.Ordinal);
            var orderedReceivers = new ServiceBusReceiver[sessionCount];
            for (var i = 0; i < sessionCount; i++)
            {
                var sessionId = "group-" + i.ToString("D2");
                var session = await SessionHarness.OpenAsync(queueName, sessionId).ConfigureAwait(false);
                await session.ReplenishAsync().ConfigureAwait(false);
                sessions[sessionId] = session;
                orderedReceivers[i] = session.Receiver;
            }

            return new MultiSessionHarness(
                new PerfAmqpReceiverProvider(queueName, sessions, orderedReceivers),
                sessions);
        }

        public Task ReplenishAsync(string sessionId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_sessions.TryGetValue(sessionId, out var session))
                throw new InvalidOperationException($"Unknown session '{sessionId}'.");
            return session.ReplenishAsync();
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var session in _sessions.Values)
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private sealed class SessionHarness : IAsyncDisposable
    {
        private int _messageIndex;

        private SessionHarness(
            ServiceBusAmqpConnection connection,
            ServiceBusReceiver receiver,
            ServiceBusBrokerSimulator broker,
            string sessionId)
        {
            Connection = connection;
            Receiver = receiver;
            Broker = broker;
            SessionId = sessionId;
        }

        public ServiceBusAmqpConnection Connection { get; }

        public ServiceBusReceiver Receiver { get; }

        public ServiceBusBrokerSimulator Broker { get; }

        public string SessionId { get; }

        public static async Task<SessionHarness> OpenAsync(string queueName, string sessionId)
        {
            var (client, server) = PipePairTransport.CreatePair();
            var broker = new ServiceBusBrokerSimulator(server);
            broker.Start();
            var connection = await ServiceBusAmqpConnection
                .OpenAsync(client, new FakeTokenProvider(), new AmqpConnectionSettings
                {
                    ContainerId = "perf-client-" + sessionId,
                    Hostname = "ns.servicebus.windows.net",
                    IdleTimeout = TimeSpan.Zero,
                })
                .WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            var audience = ServiceBusEndpoint.BuildQueueAudience("ns.servicebus.windows.net", queueName);
            var receiver = await connection
                .OpenSessionReceiverAsync(queueName, audience, sessionId, prefetchCredit: 0)
                .WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);

            return new SessionHarness(connection, receiver, broker, sessionId);
        }

        public Task ReplenishAsync()
        {
            var deliveryId = Interlocked.Increment(ref _messageIndex);
            Queue<ServiceBusBrokerSimulator.DeliveryToSend> queue;
            lock (Broker.Inbox)
            {
                if (!Broker.Inbox.TryGetValue(Receiver.Link.Name, out queue!))
                {
                    queue = new Queue<ServiceBusBrokerSimulator.DeliveryToSend>();
                    Broker.Inbox[Receiver.Link.Name] = queue;
                }
            }
            lock (queue)
            {
                queue.Enqueue(new ServiceBusBrokerSimulator.DeliveryToSend(
                    Guid.NewGuid().ToByteArray(),
                    EncodeMessage($"fifo-{SessionId}-{deliveryId}", SessionId)));
            }
            return Task.CompletedTask;
        }

        public async ValueTask DisposeAsync()
        {
            await Receiver.DisposeAsync().ConfigureAwait(false);
            await Connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class PerfAmqpReceiverProvider : IAmqpReceiverProvider
    {
        private readonly string _queueName;
        private readonly ConcurrentDictionary<string, SessionHarness> _sessions;
        private readonly ConcurrentQueue<ServiceBusReceiver> _availableReceivers;

        public PerfAmqpReceiverProvider(
            string queueName,
            ConcurrentDictionary<string, SessionHarness> sessions,
            IReadOnlyList<ServiceBusReceiver> orderedReceivers)
        {
            _queueName = queueName;
            _sessions = sessions;
            _availableReceivers = new ConcurrentQueue<ServiceBusReceiver>(orderedReceivers);
        }

        public Task<ServiceBusReceiver> GetReceiverAsync(string queueName, CancellationToken cancellationToken)
            => throw new NotSupportedException("Standard receive is not used by the FIFO perf harness.");

        public ServiceBusReceiver? TryGetExistingReceiver(string queueName) => null;

        public Task<ServiceBusReceiver> GetSessionReceiverAsync(
            string queueName,
            string sessionId,
            CancellationToken cancellationToken)
        {
            Assert.Equal(_queueName, queueName);
            if (_sessions.TryGetValue(sessionId, out var session))
                return Task.FromResult(session.Receiver);
            throw new NotSupportedException($"Test harness did not wire session '{sessionId}'.");
        }

        public ServiceBusReceiver? TryGetExistingSessionReceiver(string queueName, string sessionId)
        {
            Assert.Equal(_queueName, queueName);
            return _sessions.TryGetValue(sessionId, out var session)
                ? session.Receiver
                : null;
        }

        public AmqpReceiverLease? TryAcquireExistingSessionReceiver(string queueName, string sessionId)
        {
            var receiver = TryGetExistingSessionReceiver(queueName, sessionId);
            return receiver is null ? null : new AmqpReceiverLease(receiver);
        }

        public Task<BrokerAssignedSessionReceiverResult> AcquireBrokerAssignedSessionReceiverAsync(
            string queueName,
            TimeSpan maxBrokerWait,
            CancellationToken cancellationToken)
        {
            Assert.Equal(_queueName, queueName);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(
                _availableReceivers.TryDequeue(out var receiver)
                    ? new BrokerAssignedSessionReceiverResult(receiver, TimeSpan.Zero)
                    : new BrokerAssignedSessionReceiverResult(null, TimeSpan.Zero));
        }

        public Task InvalidateSessionReceiverAsync(string queueName, string sessionId)
        {
            Assert.Equal(_queueName, queueName);
            if (_sessions.TryGetValue(sessionId, out var session))
                _availableReceivers.Enqueue(session.Receiver);
            return Task.CompletedTask;
        }

        public Task<ServiceBusManagementClient> GetManagementClientAsync(string queueName, CancellationToken cancellationToken)
            => throw new NotSupportedException("Management is not used by the FIFO perf harness.");

        public Task ForwardAsync(string queueName, AmqpMessage message, CancellationToken cancellationToken)
            => throw new NotSupportedException("Redrive forwarding is not used by the FIFO perf harness.");

        public Task InvalidateForwardSenderAsync(string queueName) => Task.CompletedTask;

        public Task InvalidateAsync(string queueName, bool closeConnection) => Task.CompletedTask;

        public Task InvalidateManagementClientAsync(string queueName) => Task.CompletedTask;
    }

    private static byte[] EncodeMessage(string body, string groupId)
    {
        var message = new AmqpMessage
        {
            Body = Encoding.UTF8.GetBytes(body),
        };
        message.Properties = message.Properties with { GroupId = groupId };

        var rented = ArrayPool<byte>.Shared.Rent(1024);
        try
        {
            message.Write(rented, out var written);
            return rented.AsSpan(0, written).ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }
}
