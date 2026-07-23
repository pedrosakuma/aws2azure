using System.Buffers;
using Aws2Azure.Amqp.Codec;
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

    /// <summary>
    /// AMQP error payloads carried on <c>rejected</c> dispositions,
    /// keyed by delivery-id. Populated alongside
    /// <see cref="Dispositions"/> when the outcome is
    /// <see cref="AmqpDispositionOutcome.Rejected"/>; lets dead-letter
    /// tests inspect the <c>error.info</c> map written by
    /// <see cref="ServiceBusReceiver"/>.
    /// </summary>
    public Dictionary<uint, AmqpError> RejectedErrors { get; } = new();

    /// <summary>Transfers to deliver to the client on demand, keyed by client receiver-link name.</summary>
    public Dictionary<string, Queue<DeliveryToSend>> Inbox { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Session-id requested by each session-bound receiver attach (slice
    /// 7b), keyed by client-side link name. <c>null</c> values represent
    /// "any available session" requests. Empty when the link was not a
    /// session receiver.
    /// </summary>
    public Dictionary<string, string?> SessionFiltersByLink { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, SessionAttachRecord> SessionAttachesByLink { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Session-id the simulator binds to a given link name (slice 7b).
    /// Pre-populate to control the broker's response when the client
    /// asks for "any available session"; entries the test does not set
    /// default to the literal "broker-assigned-session" so the round-trip
    /// stays deterministic.
    /// </summary>
    public Dictionary<string, string> AssignedSessionByLink { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// When false, the simulator deliberately omits the source.filter
    /// from the response attach even for session-bound clients —
    /// exercises the "broker did not bind a session" error path that
    /// <see cref="ServiceBusAmqpConnection.OpenSessionReceiverAsync"/>
    /// surfaces when the caller asked for "any available session".
    /// </summary>
    public bool EchoSessionFilterOnAttach { get; set; } = true;


    /// <summary>Flow frames received per link name (recorded as the granted credit value).</summary>
    public Dictionary<string, List<uint>> FlowCreditsByLink { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Messages received via data-session <c>sender</c> attaches
    /// (i.e. client-as-publisher), keyed by client link name. Populated
    /// by <see cref="HandleTransferAsync"/> whenever an inbound transfer
    /// arrives on a link the broker accepted as a Receiver.
    /// </summary>
    public Dictionary<string, List<AmqpMessage>> ReceivedTransfers { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Initial link-credit the broker grants to a client-sender attach
    /// (slice-1 default 100). Tests can lower this to exercise the
    /// send-side credit-wait path.
    /// </summary>
    public uint SenderInitialCredit { get; set; } = 100;

    /// <summary>
    /// If a link name appears here, the next transfer the broker
    /// receives on that link is settled as <c>rejected</c> (with the
    /// supplied error) instead of <c>accepted</c>. The mapping is
    /// consumed on first use so subsequent transfers on the same link
    /// succeed.
    /// </summary>
    public Dictionary<string, AmqpError> RejectNextTransferByLink { get; } = new(StringComparer.Ordinal);

    /// <summary>Tracks the broker handle assigned to each client-sender link.</summary>
    private readonly Dictionary<uint, string> _senderLinkByClientHandle = new();

    public Task BrokerLoopTask { get; private set; } = Task.CompletedTask;
    public TaskCompletionSource? SettlementConfirmationGate { get; set; }

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
                        await HandleDispositionAsync(f);
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

        if (a.Role == AmqpRole.Sender)
        {
            // Client is a publisher: respond as Receiver and grant
            // initial credit so the client can start sending.
            _senderLinkByClientHandle[a.Handle] = a.Name;
            await AmqpTestBroker.SendPerfAsync(_server, brokerChannel, new AmqpAttach
            {
                Name = a.Name, Handle = brokerHandle, Role = AmqpRole.Receiver, InitialDeliveryCount = 0,
            }, AmqpAttach.Write);
            await AmqpTestBroker.SendPerfAsync(_server, brokerChannel, new AmqpFlow
            {
                NextIncomingId = 0, IncomingWindow = uint.MaxValue,
                NextOutgoingId = 0, OutgoingWindow = uint.MaxValue,
                Handle = brokerHandle, DeliveryCount = 0, LinkCredit = SenderInitialCredit,
            }, AmqpFlow.Write);
            return;
        }

        // Detect a session-bound attach (slice 7b): if the client's source
        // carries a com.microsoft:session-filter, echo the bound session
        // back to the client via the response attach's source.filter.
        ReadOnlyMemory<byte> responseSource = ReadOnlyMemory<byte>.Empty;
        if (!a.Source.IsEmpty)
        {
            AmqpSource.Read(a.Source, out var clientSource, out _);
            if (!clientSource.Filter.IsEmpty &&
                ServiceBusSessionFilter.TryDecode(clientSource.Filter, out var requestedSession))
            {
                SessionFiltersByLink[a.Name] = requestedSession;
                string? targetAddress = null;
                if (!a.Target.IsEmpty)
                {
                    AmqpTarget.Read(a.Target, out var target, out _);
                    targetAddress = target.Address;
                }
                SessionAttachesByLink[a.Name] = new SessionAttachRecord(
                    a.ReceiverSettleMode,
                    targetAddress,
                    ReadTimeoutMilliseconds(a.Properties));
                if (EchoSessionFilterOnAttach)
                {
                    var assigned = requestedSession
                        ?? (AssignedSessionByLink.TryGetValue(a.Name, out var fromTable)
                            ? fromTable
                            : "broker-assigned-session");
                    var responseFilter = ServiceBusSessionFilter.Encode(assigned);
                    var responseSrc = new AmqpSource
                    {
                        Address = clientSource.Address,
                        Filter = responseFilter,
                    };
                    // Encode into a stack-bounded scratch then COPY into a
                    // fresh heap array — SendPerfAsync may capture the
                    // memory; renting & forgetting from ArrayPool would leak.
                    Span<byte> scratch = stackalloc byte[Performatives.ScratchSize];
                    AmqpSource.Write(scratch, in responseSrc, out var srcLen);
                    var copy = new byte[srcLen];
                    scratch[..srcLen].CopyTo(copy);
                    responseSource = copy;
                }
            }
        }

        await AmqpTestBroker.SendPerfAsync(_server, brokerChannel, new AmqpAttach
        {
            Name = a.Name, Handle = brokerHandle, Role = AmqpRole.Sender, InitialDeliveryCount = 0,
            Source = responseSource,
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

        var credit = (int)(flow.LinkCredit ?? 0u);
        if (!FlowCreditsByLink.TryGetValue(linkName, out var creditLog))
        {
            creditLog = new List<uint>();
            FlowCreditsByLink[linkName] = creditLog;
        }
        creditLog.Add(flow.LinkCredit ?? 0u);

        if (!Inbox.TryGetValue(linkName, out var queue) || queue.Count == 0) return;

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
        AmqpTransfer.Read(f.Body, out var transfer, out var perfLen);
        var payload = f.Body.Slice(perfLen).ToArray();
        var role = _channelByPeer.TryGetValue(f.Header.Channel, out var r) ? r : ChannelRole.Cbs;

        if (role == ChannelRole.Data)
        {
            await HandleDataTransferAsync(transfer, payload);
            return;
        }

        // CBS sender transfers: parse the put-token, reply on the CBS
        // receiver link with the configured status.
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

    private async Task HandleDataTransferAsync(AmqpTransfer transfer, byte[] payload)
    {
        if (!_senderLinkByClientHandle.TryGetValue(transfer.Handle, out var linkName))
            return;
        if (transfer.More == true)
        {
            // Multi-frame transfer: buffer until the terminal fragment.
            if (!_pendingTransferPayloads.TryGetValue(transfer.Handle, out var buffer))
            {
                buffer = new List<byte>(payload.Length * 2);
                _pendingTransferPayloads[transfer.Handle] = buffer;
            }
            buffer.AddRange(payload);
            return;
        }

        byte[] full = payload;
        if (_pendingTransferPayloads.TryGetValue(transfer.Handle, out var pending))
        {
            pending.AddRange(payload);
            full = pending.ToArray();
            _pendingTransferPayloads.Remove(transfer.Handle);
        }

        var msg = AmqpMessage.Parse(full);
        if (!ReceivedTransfers.TryGetValue(linkName, out var bag))
        {
            bag = new List<AmqpMessage>();
            ReceivedTransfers[linkName] = bag;
        }
        bag.Add(msg);

        if (transfer.DeliveryId is not { } deliveryId)
            return;

        if (RejectNextTransferByLink.TryGetValue(linkName, out var error))
        {
            RejectNextTransferByLink.Remove(linkName);
            Span<byte> errScratch = stackalloc byte[Performatives.ScratchSize];
            AmqpError.Write(errScratch, in error, out var errLen);
            var errCopy = new byte[errLen];
            errScratch[..errLen].CopyTo(errCopy);
            var rejected = new Rejected { Error = errCopy };
            Span<byte> rejScratch = stackalloc byte[Performatives.ScratchSize];
            Rejected.Write(rejScratch, in rejected, out var rejLen);
            var stateCopy = new byte[rejLen];
            rejScratch[..rejLen].CopyTo(stateCopy);
            await AmqpTestBroker.SendPerfAsync(_server, DataSessionChannel, new AmqpDisposition
            {
                Role = AmqpRole.Receiver,
                First = deliveryId,
                Last = deliveryId,
                Settled = true,
                State = stateCopy,
            }, AmqpDisposition.Write);
            return;
        }

        Span<byte> accepted = stackalloc byte[16];
        Accepted.Write(accepted, out var acceptedLen);
        var acceptedCopy = new byte[acceptedLen];
        accepted[..acceptedLen].CopyTo(acceptedCopy);
        await AmqpTestBroker.SendPerfAsync(_server, DataSessionChannel, new AmqpDisposition
        {
            Role = AmqpRole.Receiver,
            First = deliveryId,
            Last = deliveryId,
            Settled = true,
            State = acceptedCopy,
        }, AmqpDisposition.Write);
    }

    private readonly Dictionary<uint, List<byte>> _pendingTransferPayloads = new();

    private async Task HandleDispositionAsync(RentedFrame f)
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
        AmqpError? rejectedError = null;
        bool? deliveryFailed = null;
        bool? undeliverableHere = null;
        if (outcome == AmqpDispositionOutcome.Modified && !d.State.IsEmpty)
        {
            Modified.Read(d.State, out var modified, out _);
            deliveryFailed = modified.DeliveryFailed;
            undeliverableHere = modified.UndeliverableHere;
        }
        if (outcome == AmqpDispositionOutcome.Rejected && !d.State.IsEmpty)
        {
            Rejected.Read(d.State, out var rejected, out _);
            if (!rejected.Error.IsEmpty)
            {
                AmqpError.Read(rejected.Error, out var err, out _);
                rejectedError = err;
            }
        }
        if (last >= first)
        {
            for (var id = first; ; id++)
            {
                Dispositions[id] = new DispositionRecord(
                    outcome,
                    d.Settled ?? false,
                    deliveryFailed,
                    undeliverableHere);
                if (rejectedError is { } re) RejectedErrors[id] = re;
                if (id == last) break;
            }
        }

        if (d.Role == AmqpRole.Receiver && d.Settled != true)
        {
            if (SettlementConfirmationGate is { } gate)
            {
                await gate.Task.ConfigureAwait(false);
            }

            var state = d.State.IsEmpty ? ReadOnlyMemory<byte>.Empty : d.State.ToArray();
            await AmqpTestBroker.SendPerfAsync(_server, DataSessionChannel, new AmqpDisposition
            {
                Role = AmqpRole.Sender,
                First = first,
                Last = last,
                Settled = true,
                State = state,
            }, AmqpDisposition.Write);
        }
    }

    private static uint? ReadTimeoutMilliseconds(ReadOnlyMemory<byte> properties)
    {
        if (properties.IsEmpty) return null;
        var map = AmqpCompoundReader.ReadMap(properties.Span, out _);
        var elements = map.Elements;
        var offset = 0;
        for (var i = 0; i < map.Count / 2; i++)
        {
            var key = AmqpVariableReader.ReadSymbol(elements[offset..], out var keyLen);
            offset += keyLen;
            if (key == "com.microsoft:timeout")
                return AmqpPrimitiveReader.ReadUInt(elements[offset..], out _);
            offset += AmqpValueScanner.Measure(elements[offset..]);
        }
        return null;
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

    public readonly record struct DispositionRecord(
        AmqpDispositionOutcome Outcome,
        bool Settled,
        bool? DeliveryFailed,
        bool? UndeliverableHere);

    public readonly record struct SessionAttachRecord(
        AmqpReceiverSettleMode? ReceiverSettleMode,
        string? TargetAddress,
        uint? TimeoutMilliseconds);

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
