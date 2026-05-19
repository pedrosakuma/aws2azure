using System.Buffers;
using Aws2Azure.Amqp.Framing;

namespace Aws2Azure.Amqp.Connection;

/// <summary>
/// AMQP 1.0 link endpoint (§2.6). Owns the local handle on its parent
/// <see cref="AmqpSession"/> and implements the <c>attach</c>/<c>detach</c>
/// handshake.
/// </summary>
/// <remarks>
/// For Slice 5b the link is intentionally minimal: lifecycle only. The
/// flow / transfer / disposition surface lands in Slice 5c, on top of
/// the dispatch hook <see cref="DispatchIncomingFrame"/>.
/// </remarks>
internal sealed class AmqpLink
{
    private const int StateClosed = 0;
    private const int StateAttaching = 1;
    private const int StateAttached = 2;
    private const int StateDetachingLocal = 3;
    private const int StateDetachingRemote = 4;
    private const int StateFinal = 5;

    private readonly AmqpSession _session;
    private readonly AmqpLinkSettings _settings;
    private readonly TaskCompletionSource<AmqpAttach> _peerAttachReceived =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<AmqpDetach> _peerDetachReceived =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Sender-side delivery tracking.
    private readonly object _deliveryLock = new();
    private readonly Dictionary<uint, TaskCompletionSource<AmqpDispositionOutcome>> _pendingSends = new();
    private uint _deliveryCount;

    // Receiver-side message queue + credit tracking.
    private readonly System.Threading.Channels.Channel<AmqpIncomingDelivery> _incoming
        = System.Threading.Channels.Channel.CreateUnbounded<AmqpIncomingDelivery>(
            new System.Threading.Channels.UnboundedChannelOptions { SingleReader = false, SingleWriter = true });
    private uint _linkCredit;
    // Number of transfers we have received on this link. Per §2.6.7
    // link-credit is interpreted relative to (delivery-count + link-credit)
    // so we must advance this on every successful receive.
    private uint _receiverDeliveryCount;

    private int _state = StateClosed;

    internal AmqpLink(AmqpSession session, uint outgoingHandle, AmqpLinkSettings settings)
    {
        _session = session;
        OutgoingHandle = outgoingHandle;
        _settings = settings;
    }

    public string Name => _settings.Name;
    public AmqpRole Role => _settings.Role;
    /// <summary>Our local handle on this session (peer sees as their remote handle).</summary>
    public uint OutgoingHandle { get; }
    /// <summary>Peer's local handle, learned from their attach reply.</summary>
    public uint RemoteHandle { get; private set; }
    /// <summary>Peer's attach performative, available after <see cref="AttachAsync"/> returns.</summary>
    public AmqpAttach RemoteAttach { get; private set; }
    public bool IsClosed => Volatile.Read(ref _state) >= StateDetachingLocal;

    // ---- sender-side API (Slice 5c) -------------------------------------

    /// <summary>
    /// Sends a single AMQP message and (when <paramref name="settled"/>
    /// is false) returns a task that completes when the receiver
    /// dispositions the delivery. When <paramref name="settled"/> is
    /// true the task completes synchronously with
    /// <see cref="AmqpDispositionOutcome.Accepted"/> once the wire write
    /// finishes.
    /// </summary>
    public async Task<AmqpDispositionOutcome> SendMessageAsync(
        AmqpMessage message, bool settled = true, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (Role != AmqpRole.Sender)
            throw new InvalidOperationException("SendMessageAsync requires a sender link.");
        if (Volatile.Read(ref _state) != StateAttached)
            throw new InvalidOperationException($"Link is not attached (state={_state}).");

        var deliveryId = _session.AllocateDeliveryId();
        Interlocked.Increment(ref _deliveryCount);

        TaskCompletionSource<AmqpDispositionOutcome>? tcs = null;
        if (!settled)
        {
            tcs = new TaskCompletionSource<AmqpDispositionOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_deliveryLock) _pendingSends[deliveryId] = tcs;
        }

        // delivery-tag: 4 bytes of delivery-id (big-endian) — uniqueness only matters per-link-per-unsettled.
        Span<byte> tag = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(tag, deliveryId);

        // Encode bare message into pooled buffer.
        using var payload = message.EncodePooled();

        // Build transfer performative and concatenate with payload.
        var transferPerf = new AmqpTransfer
        {
            Handle = OutgoingHandle,
            DeliveryId = deliveryId,
            DeliveryTag = tag.ToArray(),
            MessageFormat = 0,
            Settled = settled,
        };

        var rentedT = ArrayPool<byte>.Shared.Rent(Performatives.ScratchSize);
        var rentedFrame = ArrayPool<byte>.Shared.Rent(Performatives.ScratchSize + payload.Length);
        try
        {
            AmqpTransfer.Write(rentedT, in transferPerf, out var tlen);
            rentedT.AsSpan(0, tlen).CopyTo(rentedFrame);
            payload.Memory.Span.CopyTo(rentedFrame.AsSpan(tlen));
            await _session.Connection.WriteSessionFrameAsync(
                _session.OutgoingChannel,
                rentedFrame.AsMemory(0, tlen + payload.Length),
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Wire write failed — drop any pending entry so it doesn't leak.
            if (!settled)
            {
                lock (_deliveryLock) _pendingSends.Remove(deliveryId);
            }
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rentedT);
            ArrayPool<byte>.Shared.Return(rentedFrame);
        }

        if (settled)
            return AmqpDispositionOutcome.Accepted;

        using (cancellationToken.Register(() =>
        {
            lock (_deliveryLock) _pendingSends.Remove(deliveryId);
            tcs!.TrySetCanceled(cancellationToken);
        }))
            return await tcs!.Task.ConfigureAwait(false);
    }

    // ---- receiver-side API (Slice 5c) -----------------------------------

    /// <summary>
    /// Grants the peer additional link credit by sending a <c>flow</c>
    /// (§2.6.7). Credit is the cap on how many transfers the receiver
    /// is willing to take before issuing more credit.
    /// </summary>
    public async Task GrantCreditAsync(uint additional, CancellationToken cancellationToken = default)
    {
        if (Role != AmqpRole.Receiver)
            throw new InvalidOperationException("GrantCreditAsync requires a receiver link.");
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
    public async Task<AmqpIncomingDelivery> ReceiveMessageAsync(CancellationToken cancellationToken = default)
    {
        if (Role != AmqpRole.Receiver)
            throw new InvalidOperationException("ReceiveMessageAsync requires a receiver link.");
        return await _incoming.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a disposition settling <paramref name="delivery"/> with
    /// <c>accepted</c> (§3.4.2). Required for second-mode receiver
    /// settle mode; harmless under first-mode.
    /// </summary>
    public async Task AcceptAsync(AmqpIncomingDelivery delivery, CancellationToken cancellationToken = default)
    {
        var stateRented = ArrayPool<byte>.Shared.Rent(8);
        try
        {
            Accepted.Write(stateRented, out var sl);
            var disposition = new AmqpDisposition
            {
                Role = AmqpRole.Receiver,
                First = delivery.DeliveryId,
                Settled = true,
                State = stateRented.AsMemory(0, sl),
            };
            var rented = ArrayPool<byte>.Shared.Rent(Performatives.ScratchSize);
            try
            {
                AmqpDisposition.Write(rented, in disposition, out var dl);
                await _session.Connection.WriteSessionFrameAsync(
                    _session.OutgoingChannel, rented.AsMemory(0, dl), cancellationToken).ConfigureAwait(false);
            }
            finally { ArrayPool<byte>.Shared.Return(rented); }
        }
        finally { ArrayPool<byte>.Shared.Return(stateRented); }
    }

    /// <summary>
    /// Sends our <c>attach</c> and awaits the peer's reply. Throws if the
    /// peer immediately detaches with an error (carried as
    /// <see cref="AmqpLinkException"/>).
    /// </summary>
    public async Task AttachAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _state, StateAttaching, StateClosed) != StateClosed)
            throw new InvalidOperationException($"Link already attached or in state {_state}.");

        // Build typed source/target where the caller provided an address.
        ReadOnlyMemory<byte> sourceMem = ReadOnlyMemory<byte>.Empty;
        ReadOnlyMemory<byte> targetMem = ReadOnlyMemory<byte>.Empty;
        byte[]? rentedSrc = null;
        byte[]? rentedTgt = null;
        try
        {
            if (_settings.SourceAddress is not null)
            {
                rentedSrc = ArrayPool<byte>.Shared.Rent(Performatives.ScratchSize);
                var src = new AmqpSource { Address = _settings.SourceAddress };
                AmqpSource.Write(rentedSrc, in src, out var srcLen);
                sourceMem = rentedSrc.AsMemory(0, srcLen);
            }
            if (_settings.TargetAddress is not null)
            {
                rentedTgt = ArrayPool<byte>.Shared.Rent(Performatives.ScratchSize);
                var tgt = new AmqpTarget { Address = _settings.TargetAddress };
                AmqpTarget.Write(rentedTgt, in tgt, out var tgtLen);
                targetMem = rentedTgt.AsMemory(0, tgtLen);
            }

            var localAttach = new AmqpAttach
            {
                Name = _settings.Name,
                Handle = OutgoingHandle,
                Role = _settings.Role,
                SenderSettleMode = _settings.SenderSettleMode,
                ReceiverSettleMode = _settings.ReceiverSettleMode,
                Source = sourceMem,
                Target = targetMem,
                InitialDeliveryCount = _settings.Role == AmqpRole.Sender
                    ? (_settings.InitialDeliveryCount ?? 0u)
                    : null,
            };

            var rentedAtt = ArrayPool<byte>.Shared.Rent(Performatives.ScratchSize);
            try
            {
                AmqpAttach.Write(rentedAtt, in localAttach, out var attLen);
                await _session.Connection.WriteSessionFrameAsync(
                    _session.OutgoingChannel,
                    rentedAtt.AsMemory(0, attLen),
                    cancellationToken).ConfigureAwait(false);
            }
            finally { ArrayPool<byte>.Shared.Return(rentedAtt); }
        }
        finally
        {
            if (rentedSrc is not null) ArrayPool<byte>.Shared.Return(rentedSrc);
            if (rentedTgt is not null) ArrayPool<byte>.Shared.Return(rentedTgt);
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await using (linked.Token.Register(() => _peerAttachReceived.TrySetCanceled(linked.Token)))
        {
            var peerAttach = await _peerAttachReceived.Task.ConfigureAwait(false);
            RemoteAttach = peerAttach;
            Interlocked.Exchange(ref _state, StateAttached);
        }
    }

    /// <summary>
    /// Sends <c>detach</c> and waits for the peer's <c>detach</c>. When
    /// <paramref name="closed"/> is true, the link's resources are
    /// destroyed on both sides (§2.6.5); otherwise the link is just
    /// suspended.
    /// </summary>
    public async Task DetachAsync(
        bool closed = true,
        AmqpError? error = null,
        CancellationToken cancellationToken = default)
    {
        var prior = Interlocked.CompareExchange(ref _state, StateDetachingLocal, StateAttached);
        if (prior == StateFinal || prior == StateClosed) return;
        if (prior == StateDetachingLocal || prior == StateDetachingRemote)
        {
            await WaitForFinalAsync(cancellationToken).ConfigureAwait(false);
            return;
        }
        if (prior != StateAttached)
            throw new InvalidOperationException($"Cannot detach link from state {prior}.");

        try
        {
            await SendDetachAsync(closed, error, cancellationToken).ConfigureAwait(false);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(TimeSpan.FromSeconds(30));
            await using (linked.Token.Register(() => _peerDetachReceived.TrySetCanceled(linked.Token)))
            {
                try { await _peerDetachReceived.Task.ConfigureAwait(false); }
                catch (OperationCanceledException) { /* tear down regardless */ }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _state, StateFinal);
            CompleteWaitersTerminal(cancelled: false);
            _session.UnregisterLink(this);
        }
    }

    // ---- dispatch (invoked by AmqpSession) -------------------------------

    internal void DispatchIncomingFrame(PerformativeKind kind, ReadOnlyMemory<byte> body)
    {
        switch (kind)
        {
            case PerformativeKind.Attach:
                AmqpAttach.Read(body, out var attach, out _);
                _peerAttachReceived.TrySetResult(attach);
                break;

            case PerformativeKind.Detach:
                AmqpDetach.Read(body, out var detach, out _);
                HandlePeerDetach(detach);
                break;

            // Flow / Transfer / Disposition land in Slice 5c.
            default:
                break;
        }
    }

    internal void OnRemoteHandleLearned(uint remoteHandle) => RemoteHandle = remoteHandle;

    /// <summary>
    /// Receives a transfer frame for this link. The body memory belongs
    /// to a pooled frame buffer; we copy out the payload immediately so
    /// the caller can dispose the frame.
    /// </summary>
    internal void DispatchTransfer(AmqpTransfer transfer, ReadOnlyMemory<byte> frameBody)
    {
        AmqpTransfer.Read(frameBody, out _, out var perfLen);
        var payload = frameBody.Slice(perfLen);
        var copy = payload.ToArray();

        // §2.6.7: advance delivery-count, but guard against underflowing
        // credit if the peer over-sends.
        lock (_deliveryLock)
        {
            _receiverDeliveryCount++;
            if (_linkCredit > 0) _linkCredit--;
        }

        _incoming.Writer.TryWrite(new AmqpIncomingDelivery(
            transfer.DeliveryId ?? 0u,
            transfer.DeliveryTag.ToArray(),
            transfer.Settled ?? false,
            copy));
    }

    /// <summary>
    /// Receives a disposition for an outgoing transfer of ours.
    /// Resolves any pending <see cref="SendMessageAsync"/> task whose
    /// delivery-id falls in the range.
    /// </summary>
    internal void DispatchDisposition(AmqpDisposition disposition)
    {
        if (disposition.Role != AmqpRole.Receiver) return; // we only track sender-side here
        var first = disposition.First;
        var last = disposition.Last ?? first;
        var outcome = AmqpDispositionOutcomeExtractor.From(disposition.State);

        for (uint id = first; id <= last; id++)
        {
            TaskCompletionSource<AmqpDispositionOutcome>? tcs;
            lock (_deliveryLock)
            {
                if (!_pendingSends.Remove(id, out tcs)) continue;
            }
            tcs.TrySetResult(outcome);
        }
    }

    internal void Abort()
    {
        Interlocked.Exchange(ref _state, StateFinal);
        _peerAttachReceived.TrySetCanceled();
        _peerDetachReceived.TrySetCanceled();
        CompleteWaitersTerminal(cancelled: true);
    }

    private async ValueTask SendFlowAsync(CancellationToken ct)
    {
        uint credit, deliveryCount;
        lock (_deliveryLock)
        {
            credit = _linkCredit;
            deliveryCount = Role == AmqpRole.Receiver ? _receiverDeliveryCount : _deliveryCount;
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
            await _session.Connection.WriteSessionFrameAsync(
                _session.OutgoingChannel, rented.AsMemory(0, written), ct).ConfigureAwait(false);
        }
        finally { ArrayPool<byte>.Shared.Return(rented); }
    }

    // ---- internals --------------------------------------------------------

    private void HandlePeerDetach(AmqpDetach detach)
    {
        var prior = Interlocked.CompareExchange(ref _state, StateDetachingRemote, StateAttached);
        _peerDetachReceived.TrySetResult(detach);

        if (prior == StateAttached)
        {
            try
            {
                _ = SendDetachAsync(detach.Closed ?? true, error: null, CancellationToken.None);
            }
            catch { /* best effort */ }
            // Peer initiated tear-down: unblock any local receivers/senders so they
            // observe the detach rather than hanging on the message channel.
            CompleteWaitersTerminal(cancelled: false);
        }
    }

    /// <summary>
    /// Releases all link-level waiters (incoming message reader and any
    /// pending unsettled-send TCSes). Called from every terminal path:
    /// local detach, peer-initiated detach, abort. Idempotent.
    /// </summary>
    private void CompleteWaitersTerminal(bool cancelled)
    {
        _incoming.Writer.TryComplete();
        TaskCompletionSource<AmqpDispositionOutcome>[] snapshot;
        lock (_deliveryLock)
        {
            snapshot = _pendingSends.Values.ToArray();
            _pendingSends.Clear();
        }
        foreach (var tcs in snapshot)
        {
            if (cancelled) tcs.TrySetCanceled();
            else tcs.TrySetException(new InvalidOperationException("Link detached before disposition was received."));
        }
    }

    private async ValueTask SendDetachAsync(bool closed, AmqpError? error, CancellationToken ct)
    {
        ReadOnlyMemory<byte> errorPayload = ReadOnlyMemory<byte>.Empty;
        byte[]? rentedErr = null;
        if (error is { } e)
        {
            rentedErr = ArrayPool<byte>.Shared.Rent(Performatives.ScratchSize);
            AmqpError.Write(rentedErr, in e, out var errWritten);
            errorPayload = rentedErr.AsMemory(0, errWritten);
        }

        var rented = ArrayPool<byte>.Shared.Rent(Performatives.ScratchSize);
        try
        {
            var detach = new AmqpDetach
            {
                Handle = OutgoingHandle,
                Closed = closed,
                Error = errorPayload,
            };
            AmqpDetach.Write(rented, in detach, out var written);
            await _session.Connection.WriteSessionFrameAsync(
                _session.OutgoingChannel, rented.AsMemory(0, written), ct).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
            if (rentedErr is not null) ArrayPool<byte>.Shared.Return(rentedErr);
        }
    }

    private async Task WaitForFinalAsync(CancellationToken ct)
    {
        while (Volatile.Read(ref _state) != StateFinal)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(20, ct).ConfigureAwait(false);
        }
    }
}
