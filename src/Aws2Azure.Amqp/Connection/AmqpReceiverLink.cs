using System.Buffers;
using Aws2Azure.Amqp.Framing;

namespace Aws2Azure.Amqp.Connection;

/// <summary>
/// Receiver-role AMQP link: incoming transfer reassembly, credit grants,
/// batched receive, and receiver-side disposition helpers inherited from
/// <see cref="AmqpLink"/>.
/// </summary>
internal sealed class AmqpReceiverLink : AmqpLink
{
    private readonly object _deliveryLock = new();
    private readonly System.Threading.Channels.Channel<AmqpIncomingDelivery> _incoming
        = System.Threading.Channels.Channel.CreateUnbounded<AmqpIncomingDelivery>(
            new System.Threading.Channels.UnboundedChannelOptions { SingleReader = false, SingleWriter = true });
    private uint _linkCredit;
    // Number of transfers we have received on this link. Per §2.6.7
    // link-credit is interpreted relative to (delivery-count + link-credit)
    // so we must advance this on every successful receive.
    private uint _receiverDeliveryCount;

    // Multi-frame receive reassembly buffer (§2.6.14). Keyed by
    // delivery-id; first transfer carries the id, continuation transfers
    // omit it and append to the most recent in-progress delivery.
    private readonly object _reassemblyLock = new();
    private uint? _currentInboundDeliveryId;
    private byte[]? _currentInboundTag;
    private bool? _currentInboundSettled;
    private System.IO.MemoryStream? _currentInboundPayload;

    internal AmqpReceiverLink(AmqpSession session, uint outgoingHandle, AmqpLinkSettings settings)
        : base(session, outgoingHandle, settings)
    {
    }

    /// <summary>
    /// Grants the peer additional link credit by sending a <c>flow</c>
    /// (§2.6.7). Credit is the cap on how many transfers the receiver
    /// is willing to take before issuing more credit.
    /// </summary>
    public override async Task GrantCreditAsync(uint additional, CancellationToken cancellationToken = default)
    {
        lock (_deliveryLock)
        {
            _linkCredit += additional;
        }
        await SendFlowAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Awaits the next incoming delivery on this receiver link. Throws
    /// if the link is detached before a message arrives.
    /// </summary>
    public override async Task<AmqpIncomingDelivery> ReceiveMessageAsync(CancellationToken cancellationToken = default)
    {
        return await _incoming.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Long-poll equivalent: grants enough additional credit to cover
    /// <paramref name="maxMessages"/> deliveries (accounting for credit
    /// already outstanding with the peer and messages already buffered
    /// locally) and drains up to that many deliveries within
    /// <paramref name="maxWait"/>. Returns early as soon as the cap is
    /// reached. The returned list may be empty if the wait elapses
    /// with no deliveries.
    /// </summary>
    /// <remarks>
    /// Top-up semantics (not additive): repeated calls on the same
    /// receiver only top up to <paramref name="maxMessages"/> total
    /// in-flight credit, so a long-lived receiver shared across many
    /// SQS receive requests never over-grants and never causes the
    /// peer to push messages that no caller is waiting to drain.
    /// Higher layers that need fully independent receive batches
    /// should still serialise concurrent calls to this method to
    /// avoid races between the read of <c>_linkCredit</c> and the
    /// subsequent grant.
    /// <para>
    /// <paramref name="tailWait"/> controls "burst-coalesce" semantics
    /// once at least one delivery has been collected. By default the
    /// method waits the full <paramref name="maxWait"/> trying to fill
    /// <paramref name="maxMessages"/>. When a shorter <c>tailWait</c>
    /// is supplied, the deadline is shrunk to <c>now + tailWait</c>
    /// the first time the local buffer is non-empty, so the call
    /// returns promptly once the peer's first burst lands — matching
    /// the "drain what's there, don't block waiting for an arbitrary
    /// batch ceiling" semantic of <c>PartitionReceiver.ReceiveBatchAsync</c>
    /// and similar SDK consumers. The shrunk deadline is bounded above
    /// by the original <paramref name="maxWait"/>.
    /// </para>
    /// </remarks>
    public override async Task<IReadOnlyList<AmqpIncomingDelivery>> ReceiveBatchAsync(
        int maxMessages,
        TimeSpan maxWait,
        TimeSpan? tailWait = null,
        CancellationToken cancellationToken = default)
    {
        if (maxMessages <= 0) throw new ArgumentOutOfRangeException(nameof(maxMessages));

        // Compute the credit top-up: in-flight = credit already granted
        // to the peer (broker may still transfer) + deliveries already
        // sitting in our local buffer (broker has transferred, no caller
        // yet drained). Only grant the difference up to maxMessages.
        uint toGrant;
        lock (_deliveryLock)
        {
            var buffered = (uint)_incoming.Reader.Count;
            var inFlight = _linkCredit + buffered;
            toGrant = (uint)maxMessages > inFlight ? (uint)maxMessages - inFlight : 0u;
        }
        if (toGrant > 0)
            await GrantCreditAsync(toGrant, cancellationToken).ConfigureAwait(false);

        var batch = new List<AmqpIncomingDelivery>(maxMessages);

        // Drain anything already buffered before arming the timer.
        while (batch.Count < maxMessages && _incoming.Reader.TryRead(out var ready))
            batch.Add(ready);
        if (batch.Count >= maxMessages) return batch;

        using var deadlineCts = new CancellationTokenSource(maxWait);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadlineCts.Token);
        var tailShrunk = false;

        // If the pre-loop drain already collected at least one delivery
        // AND a shorter tailWait was supplied, shrink the deadline right
        // away — most calls against a long-lived receiver land in this
        // branch (the broker pipelines transfers into the local buffer
        // between calls), so it's the hot path we must keep fast.
        if (batch.Count > 0 && tailWait is { } twImmediate && twImmediate < maxWait)
        {
            deadlineCts.CancelAfter(twImmediate);
            tailShrunk = true;
        }

        try
        {
            while (batch.Count < maxMessages
                   && await _incoming.Reader.WaitToReadAsync(linked.Token).ConfigureAwait(false))
            {
                while (batch.Count < maxMessages && _incoming.Reader.TryRead(out var item))
                    batch.Add(item);

                if (!tailShrunk
                    && batch.Count > 0
                    && tailWait is { } tw
                    && tw < maxWait)
                {
                    // First burst landed mid-wait: same shrink semantic
                    // as the pre-loop branch above. Bounded above by the
                    // original maxWait via the CTS we already armed.
                    deadlineCts.CancelAfter(tw);
                    tailShrunk = true;
                }
            }

            // If the channel was completed (link is terminal) before the
            // deadline elapsed AND we drained nothing, the long-poll did
            // not just time out — surface the broken link to the caller.
            // If we did collect deliveries, return them so the caller can
            // disposition them; a subsequent receive will surface the
            // terminal state on its own.
            if (batch.Count == 0
                && _incoming.Reader.Completion.IsCompleted
                && !deadlineCts.IsCancellationRequested
                && !cancellationToken.IsCancellationRequested)
            {
                throw BuildPeerDetachException(
                    "Receiver link is closed; no further deliveries will arrive.",
                    PendingPeerError);
            }
        }
        catch (OperationCanceledException) when (deadlineCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // Deadline reached: return whatever we have.
        }

        return batch;
    }

    /// <summary>
    /// Receives a transfer frame for this link. The body memory belongs
    /// to a pooled frame buffer; we copy out the payload immediately so
    /// the caller can dispose the frame. Implements §2.6.14 multi-frame
    /// reassembly: <c>more=true</c> transfers buffer payload until a
    /// final transfer arrives; <c>aborted=true</c> discards the in-progress
    /// delivery without enqueueing it.
    /// </summary>
    internal override void DispatchTransfer(AmqpTransfer transfer, ReadOnlyMemory<byte> frameBody)
    {
        AmqpTransfer.Read(frameBody, out _, out var perfLen);
        var payload = frameBody.Slice(perfLen);

        var more = transfer.More ?? false;
        var aborted = transfer.Aborted ?? false;
        var settled = transfer.Settled ?? false;

        byte[]? completed = null;
        uint completedDeliveryId = 0;
        byte[]? completedTag = null;
        bool completedSettled = false;
        bool sizeExceeded = false;

        lock (_reassemblyLock)
        {
            // §2.6.14: only the first transfer of a delivery carries
            // delivery-id and delivery-tag; continuation transfers omit
            // them and append to the most recent in-progress delivery on
            // the link.
            if (transfer.DeliveryId is { } did)
            {
                if (_currentInboundDeliveryId is not null)
                {
                    // Spec violation by peer (new delivery before previous
                    // signalled more=false). Drop the in-progress one.
                    _currentInboundPayload?.Dispose();
                    _currentInboundPayload = null;
                }
                _currentInboundDeliveryId = did;
                _currentInboundTag = transfer.DeliveryTag.IsEmpty ? Array.Empty<byte>() : transfer.DeliveryTag.ToArray();
                _currentInboundSettled = settled;
                _currentInboundPayload = null;
            }

            if (aborted)
            {
                _currentInboundPayload?.Dispose();
                _currentInboundPayload = null;
                _currentInboundDeliveryId = null;
                _currentInboundTag = null;
                _currentInboundSettled = null;
            }
            else if (more)
            {
                _currentInboundPayload ??= new System.IO.MemoryStream();
                if (!payload.IsEmpty) _currentInboundPayload.Write(payload.Span);
                if (ExceedsMaxMessageSize((ulong)_currentInboundPayload.Length))
                {
                    _currentInboundPayload.Dispose();
                    _currentInboundPayload = null;
                    _currentInboundDeliveryId = null;
                    _currentInboundTag = null;
                    _currentInboundSettled = null;
                    sizeExceeded = true;
                }
            }
            else if (_currentInboundDeliveryId is { } activeId)
            {
                long total = (_currentInboundPayload?.Length ?? 0L) + payload.Length;
                if (ExceedsMaxMessageSize((ulong)total))
                {
                    _currentInboundPayload?.Dispose();
                    _currentInboundPayload = null;
                    _currentInboundDeliveryId = null;
                    _currentInboundTag = null;
                    _currentInboundSettled = null;
                    sizeExceeded = true;
                }
                else
                {
                    if (_currentInboundPayload is { } stream)
                    {
                        if (!payload.IsEmpty) stream.Write(payload.Span);
                        completed = stream.ToArray();
                        stream.Dispose();
                    }
                    else
                    {
                        completed = payload.IsEmpty ? Array.Empty<byte>() : payload.ToArray();
                    }
                    completedDeliveryId = activeId;
                    completedTag = _currentInboundTag ?? Array.Empty<byte>();
                    completedSettled = _currentInboundSettled ?? settled;
                    _currentInboundPayload = null;
                    _currentInboundDeliveryId = null;
                    _currentInboundTag = null;
                    _currentInboundSettled = null;
                }
            }
        }

        if (sizeExceeded)
        {
            // §2.7.3: receiver enforces max-message-size by detaching the
            // link with amqp:link:message-size-exceeded.
            _ = Task.Run(async () =>
            {
                try
                {
                    await DetachAsync(closed: true, error: new AmqpError
                    {
                        Condition = AmqpErrorCondition.LinkMessageSizeExceeded,
                        Description = "incoming message exceeds advertised max-message-size",
                    }, CancellationToken.None).ConfigureAwait(false);
                }
                catch { /* best effort */ }
            });
            return;
        }

        // §2.6.7: advance delivery-count, but guard against underflowing
        // credit if the peer over-sends. Only count completed deliveries.
        if (completed is not null)
        {
            lock (_deliveryLock)
            {
                _receiverDeliveryCount++;
                if (_linkCredit > 0) _linkCredit--;
            }
            _incoming.Writer.TryWrite(new AmqpIncomingDelivery(
                completedDeliveryId, completedTag!, completedSettled, completed));
        }
    }

    protected override void CompleteRoleWaitersTerminal(bool cancelled, AmqpError? peerError)
        => _incoming.Writer.TryComplete();

    private async ValueTask SendFlowAsync(CancellationToken ct)
    {
        uint credit, deliveryCount;
        lock (_deliveryLock)
        {
            credit = _linkCredit;
            deliveryCount = _receiverDeliveryCount;
        }
        var flow = new AmqpFlow
        {
            NextIncomingId = null,
            IncomingWindow = uint.MaxValue,
            NextOutgoingId = 0,
            OutgoingWindow = uint.MaxValue,
            Handle = OutgoingHandle,
            DeliveryCount = deliveryCount,
            LinkCredit = credit,
        };
        var rented = ArrayPool<byte>.Shared.Rent(Performatives.ScratchSize);
        try
        {
            AmqpFlow.Write(rented, in flow, out var written);
            await Session.Connection.WriteSessionFrameAsync(
                Session.OutgoingChannel, rented.AsMemory(0, written), ct).ConfigureAwait(false);
        }
        finally { ArrayPool<byte>.Shared.Return(rented); }
    }

    /// <summary>
    /// True if <paramref name="bytes"/> would exceed the configured local
    /// <c>max-message-size</c> (§2.7.3). A configured value of <c>0</c>
    /// means "no limit".
    /// </summary>
    private bool ExceedsMaxMessageSize(ulong bytes)
        => Settings.MaxMessageSize != 0 && bytes > Settings.MaxMessageSize;
}
