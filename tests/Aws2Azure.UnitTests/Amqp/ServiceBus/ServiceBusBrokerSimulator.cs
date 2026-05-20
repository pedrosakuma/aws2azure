using System.Buffers;
using Aws2Azure.Amqp.Connection;
using Aws2Azure.Amqp.Framing;
using Aws2Azure.Amqp.Security;
using Aws2Azure.Amqp.ServiceBus;
using Aws2Azure.Amqp.Transport;
using Aws2Azure.UnitTests.Amqp.Connection;
using Aws2Azure.UnitTests.Amqp.Transport;

namespace Aws2Azure.UnitTests.Amqp.ServiceBus;

/// <summary>
/// In-process AMQP broker that impersonates Azure Service Bus enough to
/// drive <see cref="ServiceBusAmqpConnection"/> and
/// <see cref="ServiceBusReceiver"/> end-to-end:
/// <list type="number">
///   <item>open handshake on channel 0;</item>
///   <item>begin reply for the CBS session on broker channel 2;</item>
///   <item>attach handling for the CBS sender + receiver request/response
///   pair (broker handles 50 / 51);</item>
///   <item>put-token reply with a configurable status-code per audience;</item>
///   <item>begin reply for the data session on broker channel 4;</item>
///   <item>attach handling for queue receiver links + on-demand
///   transfer / disposition exchanges;</item>
///   <item>graceful close/end/detach echo on shutdown.</item>
/// </list>
/// <para>
/// Tests configure the broker by mutating
/// <see cref="ServiceBusBrokerSimulator.Inbox"/> before the receiver
/// asks for messages, and observe what the client sent via
/// <see cref="ServiceBusBrokerSimulator.Dispositions"/>.
/// </para>
/// </summary>
internal sealed class ServiceBusBrokerSimulator
{
    private const ushort CbsSessionChannel = 2;
    private const ushort DataSessionChannel = 4;
    private const uint CbsSenderHandle = 50;
    private const uint CbsReceiverHandle = 51;

    private readonly IAmqpTransport _server;
    private uint _cbsResponseDeliveryId;
    private uint _dataDeliveryIdCounter;
    private readonly Dictionary<uint, uint> _queueLinkHandles = new(); // client handle → broker handle
    private uint _nextBrokerDataHandle = 100;

    public ServiceBusBrokerSimulator(IAmqpTransport server) { _server = server; }

    /// <summary>Status code the broker returns for the next CBS put-token (default 202).</summary>
    public int CbsStatus { get; set; } = 202;
    public string CbsDescription { get; set; } = "Accepted";

    /// <summary>Audiences observed in put-token requests.</summary>
    public List<string> AuthorizedAudiences { get; } = new();

    /// <summary>Dispositions observed on receiver links, keyed by delivery-id.</summary>
    public Dictionary<uint, DispositionRecord> Dispositions { get; } = new();

    /// <summary>Transfers to deliver to the client on demand, keyed by client receiver-link name.</summary>
    public Dictionary<string, Queue<DeliveryToSend>> Inbox { get; } = new(StringComparer.Ordinal);

    public Task BrokerLoopTask { get; private set; } = Task.CompletedTask;

    public void Start(CancellationToken cancellationToken = default)
    {
        BrokerLoopTask = Task.Run(() => RunAsync(cancellationToken));
    }

    private readonly Dictionary<ushort, ChannelRole> _channelByPeer = new();
    private readonly Dictionary<string, ushort> _linkNameToClientChannel = new(StringComparer.Ordinal);
    private readonly Dictionary<string, uint> _linkNameToClientHandle = new(StringComparer.Ordinal);

    private enum ChannelRole { Cbs, Data }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await AmqpTestBroker.ConsumeOpenAsync(_server);

            while (!ct.IsCancellationRequested)
            {
                using var f = await AmqpFrameIO.ReadFrameAsync(_server, 64 * 1024, ct);
                var kind = PerformativeCodec.PeekKind(f.Body.Span, out _);
                switch (kind)
                {
                    case PerformativeKind.Begin:
                        await HandleBeginAsync(f);
                        break;
                    case PerformativeKind.Attach:
                        await HandleAttachAsync(f);
                        break;
                    case PerformativeKind.Flow:
                        await HandleFlowAsync(f);
                        break;
                    case PerformativeKind.Transfer:
                        await HandleTransferAsync(f);
                        break;
                    case PerformativeKind.Disposition:
                        HandleDisposition(f);
                        break;
                    case PerformativeKind.Detach:
                        await HandleDetachAsync(f);
                        break;
                    case PerformativeKind.End:
                        await AmqpTestBroker.SendPerfAsync(_server, f.Header.Channel, new AmqpEnd(), AmqpEnd.Write);
                        break;
                    case PerformativeKind.Close:
                        await AmqpTestBroker.SendPerfAsync(_server, 0, new AmqpClose(), AmqpClose.Write);
                        return;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (EndOfStreamException) { }
        catch (IOException) { }
    }

    private async Task HandleBeginAsync(RentedFrame f)
    {
        AmqpBegin.Read(f.Body, out var begin, out _);
        var role = _channelByPeer.Count == 0 ? ChannelRole.Cbs : ChannelRole.Data;
        var brokerChannel = role == ChannelRole.Cbs ? CbsSessionChannel : DataSessionChannel;
        _channelByPeer[f.Header.Channel] = role;
        await AmqpTestBroker.SendPerfAsync(_server, brokerChannel, new AmqpBegin
        {
            RemoteChannel = f.Header.Channel,
            NextOutgoingId = 0,
            IncomingWindow = begin.OutgoingWindow,
            OutgoingWindow = begin.IncomingWindow,
            HandleMax = 255,
        }, AmqpBegin.Write);
    }

    private async Task HandleAttachAsync(RentedFrame f)
    {
        AmqpAttach.Read(f.Body, out var a, out _);
        var role = _channelByPeer[f.Header.Channel];
        var brokerChannel = role == ChannelRole.Cbs ? CbsSessionChannel : DataSessionChannel;

        if (role == ChannelRole.Cbs)
        {
            // Client's sender → we respond as a Receiver with handle 50;
            // Client's receiver → we respond as a Sender with handle 51 and prime credit.
            if (a.Role == AmqpRole.Sender)
            {
                await AmqpTestBroker.SendPerfAsync(_server, brokerChannel, new AmqpAttach
                {
                    Name = a.Name, Handle = CbsSenderHandle, Role = AmqpRole.Receiver, InitialDeliveryCount = 0,
                }, AmqpAttach.Write);
                await AmqpTestBroker.SendPerfAsync(_server, brokerChannel, new AmqpFlow
                {
                    NextIncomingId = 0, IncomingWindow = uint.MaxValue,
                    NextOutgoingId = 0, OutgoingWindow = uint.MaxValue,
                    Handle = CbsSenderHandle, DeliveryCount = 0, LinkCredit = 100,
                }, AmqpFlow.Write);
            }
            else
            {
                await AmqpTestBroker.SendPerfAsync(_server, brokerChannel, new AmqpAttach
                {
                    Name = a.Name, Handle = CbsReceiverHandle, Role = AmqpRole.Sender, InitialDeliveryCount = 0,
                }, AmqpAttach.Write);
            }
            return;
        }

        // Data session: queue receiver attach. Allocate a broker handle and
        // respond as a Sender. Record the link name → client handle so we
        // know where to send transfers in HandleFlowAsync / on demand.
        var brokerHandle = _nextBrokerDataHandle++;
        _queueLinkHandles[a.Handle] = brokerHandle;
        _linkNameToClientChannel[a.Name] = f.Header.Channel;
        _linkNameToClientHandle[a.Name] = a.Handle;
        await AmqpTestBroker.SendPerfAsync(_server, brokerChannel, new AmqpAttach
        {
            Name = a.Name, Handle = brokerHandle, Role = AmqpRole.Sender, InitialDeliveryCount = 0,
        }, AmqpAttach.Write);
    }

    private async Task HandleFlowAsync(RentedFrame f)
    {
        AmqpFlow.Read(f.Body, out var flow, out _);
        var role = _channelByPeer[f.Header.Channel];
        if (role == ChannelRole.Cbs) return;
        if (flow.Handle is not { } clientHandle) return;

        // Find the link name owning this client handle.
        string? linkName = null;
        foreach (var kvp in _linkNameToClientHandle)
        {
            if (kvp.Value == clientHandle) { linkName = kvp.Key; break; }
        }
        if (linkName is null) return;

        if (!Inbox.TryGetValue(linkName, out var queue) || queue.Count == 0) return;

        var credit = (int)(flow.LinkCredit ?? 0u);
        while (credit-- > 0 && queue.Count > 0)
        {
            var item = queue.Dequeue();
            var brokerHandle = _queueLinkHandles[clientHandle];
            var deliveryId = Interlocked.Increment(ref _dataDeliveryIdCounter) - 1;
            await AmqpTestBroker.SendTransferPayloadAsync(
                _server, DataSessionChannel, brokerHandle,
                deliveryId, item.DeliveryTag, item.Payload, more: false);
        }
    }

    private async Task HandleTransferAsync(RentedFrame f)
    {
        // Only CBS sender transfers are inbound: parse the put-token,
        // reply on the CBS receiver link.
        AmqpTransfer.Read(f.Body, out var transfer, out var perfLen);
        var payload = f.Body.Slice(perfLen).ToArray();
        var msg = AmqpMessage.Parse(payload);
        var audience = msg.ApplicationProperties is { } ap && ap.TryGetValue("name", out var raw) && raw is string s ? s : "";
        AuthorizedAudiences.Add(audience);

        var response = new AmqpMessage
        {
            Properties = new AmqpProperties { CorrelationId = msg.Properties.MessageId },
            ApplicationProperties = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["status-code"] = CbsStatus,
                ["status-description"] = CbsDescription,
            },
            Body = Array.Empty<byte>(),
        };
        var rented = ArrayPool<byte>.Shared.Rent(Performatives.ScratchSize);
        response.Write(rented, out var respLen);
        var deliveryId = Interlocked.Increment(ref _cbsResponseDeliveryId) - 1;
        await AmqpTestBroker.SendTransferPayloadAsync(
            _server, CbsSessionChannel, CbsReceiverHandle,
            deliveryId, new byte[] { (byte)deliveryId }, rented.AsMemory(0, respLen),
            more: false);
        ArrayPool<byte>.Shared.Return(rented);
    }

    private void HandleDisposition(RentedFrame f)
    {
        AmqpDisposition.Read(f.Body, out var d, out _);
        // Only record dispositions from the data session — CBS put-token
        // replies also produce Accepted dispositions on delivery-id 0
        // which would otherwise race with the data delivery's outcome.
        if (!_channelByPeer.TryGetValue(f.Header.Channel, out var role) || role != ChannelRole.Data)
            return;
        var first = d.First;
        var last = d.Last ?? first;
        var outcome = AmqpDispositionOutcomeExtractor.From(d.State);
        for (var id = first; id <= last; id++)
        {
            Dispositions[id] = new DispositionRecord(outcome, d.Settled ?? false);
        }
    }

    private async Task HandleDetachAsync(RentedFrame f)
    {
        AmqpDetach.Read(f.Body, out var d, out _);
        var role = _channelByPeer[f.Header.Channel];
        var brokerChannel = role == ChannelRole.Cbs ? CbsSessionChannel : DataSessionChannel;
        // Echo a detach on the broker's own session channel with the
        // broker-side handle (or just echo the client's handle for CBS
        // because we mirrored 50/51 there).
        uint echoHandle = d.Handle;
        if (role == ChannelRole.Data && _queueLinkHandles.TryGetValue(d.Handle, out var bh))
            echoHandle = bh;
        else if (role == ChannelRole.Cbs)
            echoHandle = d.Handle == 0u ? CbsSenderHandle : CbsReceiverHandle;
        await AmqpTestBroker.SendPerfAsync(_server, brokerChannel, new AmqpDetach
        {
            Handle = echoHandle, Closed = true,
        }, AmqpDetach.Write);
    }

    public readonly record struct DispositionRecord(AmqpDispositionOutcome Outcome, bool Settled);

    public sealed record DeliveryToSend(byte[] DeliveryTag, byte[] Payload);
}

/// <summary>
/// Lightweight in-memory token provider used by the SB unit tests. Returns
/// a fixed token string + configurable expiry so tests can assert on
/// the put-token round-trip without exercising the real HMAC path.
/// </summary>
internal sealed class FakeTokenProvider : IAmqpTokenProvider
{
    public string TokenType => "servicebus.windows.net:sastoken";

    public DateTimeOffset Expiry { get; set; } = DateTimeOffset.UtcNow.AddMinutes(20);

    public AmqpToken GetToken(string audience) =>
        new("SharedAccessSignature sr=" + audience + "&sig=fake", Expiry);
}
