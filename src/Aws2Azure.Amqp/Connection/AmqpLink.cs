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

    // Sender-side credit tracking. The receiver grants us a window via
    // flow: sender_credit = receiver.delivery_count + receiver.link_credit
    // - sender.delivery_count (§2.6.7). We wait on _creditAvailable until
    // credit > 0 before transferring.
    private readonly SemaphoreSlim _creditAvailable = new(0, int.MaxValue);
    private uint _peerReceiverDeliveryCount;
    private uint _peerReceiverLinkCredit;
    private bool _peerFlowSeen;

    // Multi-frame receive reassembly buffer (§2.6.14). Keyed by
    // delivery-id; first transfer carries the id, continuation transfers
    // omit it and append to the most recent in-progress delivery.
    private readonly object _reassemblyLock = new();
    private uint? _currentInboundDeliveryId;
    private byte[]? _currentInboundTag;
    private bool? _currentInboundSettled;
    private System.IO.MemoryStream? _currentInboundPayload;

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

        // §2.6.7: wait for receiver credit before transferring. If the
        // peer has never sent a flow we treat that as no credit and
        // block here until one arrives — matches strict broker behaviour
        // (Service Bus, ActiveMQ Artemis) that detach a link whose
        // sender pushes transfers without granted credit.
        await AcquireCreditAsync(cancellationToken).ConfigureAwait(false);

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
        var tagArr = tag.ToArray();

        // Encode bare message into pooled buffer.
        using var payload = message.EncodePooled();

        try
        {
            await WriteTransferFragmentedAsync(
                deliveryId, tagArr, payload.Memory, settled, cancellationToken).ConfigureAwait(false);
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

        if (settled)
            return AmqpDispositionOutcome.Accepted;

        using (cancellationToken.Register(() =>
        {
            lock (_deliveryLock) _pendingSends.Remove(deliveryId);
            tcs!.TrySetCanceled(cancellationToken);
        }))
            return await tcs!.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// §2.6.14 outbound multi-frame transfer. Emits a single
    /// <c>transfer</c> when the encoded message fits in one frame;
    /// otherwise splits across N frames where the first carries
    /// <c>delivery-id</c>+<c>delivery-tag</c> and intermediates set
    /// <c>more=true</c>. All segments share the connection's write lock
    /// so they cannot interleave with other writers on the same
    /// connection.
    /// </summary>
    private async ValueTask WriteTransferFragmentedAsync(
        uint deliveryId, byte[] tag, ReadOnlyMemory<byte> body, bool settled, CancellationToken ct)
    {
        const int FrameHeaderSize = 8;
        var maxFrame = _session.Connection.CurrentMaxFrameSize;

        // Encode the "first" transfer perf (full descriptor) to measure overhead.
        var rentedFirstPerf = ArrayPool<byte>.Shared.Rent(Performatives.ScratchSize);
        var rentedContPerf = ArrayPool<byte>.Shared.Rent(Performatives.ScratchSize);
        try
        {
            int firstPerfLen;
            {
                var firstPerf = new AmqpTransfer
                {
                    Handle = OutgoingHandle,
                    DeliveryId = deliveryId,
                    DeliveryTag = tag,
                    MessageFormat = 0,
                    Settled = settled,
                };
                AmqpTransfer.Write(rentedFirstPerf, in firstPerf, out firstPerfLen);
            }

            int singleFrameCapacity = maxFrame - FrameHeaderSize - firstPerfLen;
            if (singleFrameCapacity < 0)
                throw new InvalidOperationException(
                    $"Transfer performative ({firstPerfLen} B) exceeds negotiated max-frame-size ({maxFrame} B).");

            // Fast path: fits in one frame.
            if (body.Length <= singleFrameCapacity)
            {
                var rentedFrame = ArrayPool<byte>.Shared.Rent(firstPerfLen + body.Length);
                try
                {
                    rentedFirstPerf.AsSpan(0, firstPerfLen).CopyTo(rentedFrame);
                    body.Span.CopyTo(rentedFrame.AsSpan(firstPerfLen));
                    await _session.Connection.WriteSessionFrameAsync(
                        _session.OutgoingChannel,
                        rentedFrame.AsMemory(0, firstPerfLen + body.Length), ct).ConfigureAwait(false);
                }
                finally { ArrayPool<byte>.Shared.Return(rentedFrame); }
                return;
            }

            // Multi-frame: re-encode the first perf with more=true.
            {
                var firstPerf = new AmqpTransfer
                {
                    Handle = OutgoingHandle,
                    DeliveryId = deliveryId,
                    DeliveryTag = tag,
                    MessageFormat = 0,
                    Settled = settled,
                    More = true,
                };
                AmqpTransfer.Write(rentedFirstPerf, in firstPerf, out firstPerfLen);
            }
            // Continuation perf: §2.6.14 omits delivery-id / delivery-tag.
            int contPerfMoreLen;
            {
                var contPerf = new AmqpTransfer
                {
                    Handle = OutgoingHandle,
                    MessageFormat = 0,
                    Settled = settled,
                    More = true,
                };
                AmqpTransfer.Write(rentedContPerf, in contPerf, out contPerfMoreLen);
            }
            // Final continuation perf: more absent (= false).
            int contPerfFinalLen;
            var rentedContFinalPerf = ArrayPool<byte>.Shared.Rent(Performatives.ScratchSize);
            try
            {
                var contFinalPerf = new AmqpTransfer
                {
                    Handle = OutgoingHandle,
                    MessageFormat = 0,
                    Settled = settled,
                };
                AmqpTransfer.Write(rentedContFinalPerf, in contFinalPerf, out contPerfFinalLen);

                int firstCap = maxFrame - FrameHeaderSize - firstPerfLen;
                int contCap = maxFrame - FrameHeaderSize - contPerfMoreLen;
                if (firstCap <= 0 || contCap <= 0)
                    throw new InvalidOperationException(
                        $"Negotiated max-frame-size ({maxFrame} B) leaves no room for transfer payload.");

                // Pre-allocate the segment list (avoid per-frame allocation in inner loop).
                int remaining = body.Length - firstCap;
                int contFrames = (remaining + contCap - 1) / contCap;
                int totalFrames = 1 + contFrames;
                var segments = new ReadOnlyMemory<byte>[totalFrames];
                var rented = new byte[totalFrames][];

                int offset = 0;
                try
                {
                    // First frame.
                    int take = firstCap;
                    var buf0 = ArrayPool<byte>.Shared.Rent(firstPerfLen + take);
                    rented[0] = buf0;
                    rentedFirstPerf.AsSpan(0, firstPerfLen).CopyTo(buf0);
                    body.Span.Slice(offset, take).CopyTo(buf0.AsSpan(firstPerfLen));
                    segments[0] = buf0.AsMemory(0, firstPerfLen + take);
                    offset += take;

                    // Continuation frames.
                    for (int i = 1; i < totalFrames; i++)
                    {
                        bool isLast = i == totalFrames - 1;
                        int perfLen = isLast ? contPerfFinalLen : contPerfMoreLen;
                        var perfSrc = isLast ? rentedContFinalPerf : rentedContPerf;
                        int contTake = Math.Min(contCap, body.Length - offset);
                        var bufI = ArrayPool<byte>.Shared.Rent(perfLen + contTake);
                        rented[i] = bufI;
                        perfSrc.AsSpan(0, perfLen).CopyTo(bufI);
                        body.Span.Slice(offset, contTake).CopyTo(bufI.AsSpan(perfLen));
                        segments[i] = bufI.AsMemory(0, perfLen + contTake);
                        offset += contTake;
                    }

                    await _session.Connection.WriteSessionFramesAtomicAsync(
                        _session.OutgoingChannel, segments, ct).ConfigureAwait(false);
                }
                finally
                {
                    foreach (var r in rented) if (r is not null) ArrayPool<byte>.Shared.Return(r);
                }
            }
            finally { ArrayPool<byte>.Shared.Return(rentedContFinalPerf); }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rentedFirstPerf);
            ArrayPool<byte>.Shared.Return(rentedContPerf);
        }
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
    public Task AcceptAsync(AmqpIncomingDelivery delivery, CancellationToken cancellationToken = default)
    {
        var rented = ArrayPool<byte>.Shared.Rent(8);
        try
        {
            Accepted.Write(rented, out var sl);
            return SendDispositionAsync(delivery.DeliveryId, rented.AsMemory(0, sl), settled: true, cancellationToken);
        }
        finally { ArrayPool<byte>.Shared.Return(rented); }
    }

    /// <summary>
    /// Sends a disposition settling <paramref name="delivery"/> with
    /// <c>rejected</c> (§3.4.4). Service Bus dead-letters on this
    /// outcome when the entity is configured for it.
    /// </summary>
    public Task RejectAsync(
        AmqpIncomingDelivery delivery,
        AmqpError? error = null,
        CancellationToken cancellationToken = default)
    {
        ReadOnlyMemory<byte> errorMem = ReadOnlyMemory<byte>.Empty;
        byte[]? rentedErr = null;
        if (error is { } e)
        {
            rentedErr = ArrayPool<byte>.Shared.Rent(Performatives.ScratchSize);
            AmqpError.Write(rentedErr, in e, out var el);
            errorMem = rentedErr.AsMemory(0, el);
        }
        var rented = ArrayPool<byte>.Shared.Rent(Performatives.ScratchSize);
        try
        {
            var rejected = new Rejected { Error = errorMem };
            Rejected.Write(rented, in rejected, out var sl);
            return SendDispositionAsync(delivery.DeliveryId, rented.AsMemory(0, sl), settled: true, cancellationToken);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
            if (rentedErr is not null) ArrayPool<byte>.Shared.Return(rentedErr);
        }
    }

    /// <summary>
    /// Sends a disposition settling <paramref name="delivery"/> with
    /// <c>released</c> (§3.4.5). The broker requeues the message
    /// immediately without incrementing the delivery-count.
    /// </summary>
    public Task ReleaseAsync(AmqpIncomingDelivery delivery, CancellationToken cancellationToken = default)
    {
        var rented = ArrayPool<byte>.Shared.Rent(8);
        try
        {
            Released.Write(rented, out var sl);
            return SendDispositionAsync(delivery.DeliveryId, rented.AsMemory(0, sl), settled: true, cancellationToken);
        }
        finally { ArrayPool<byte>.Shared.Return(rented); }
    }

    /// <summary>
    /// Sends a disposition settling <paramref name="delivery"/> with
    /// <c>modified</c> (§3.4.6). Service Bus honours
    /// <c>delivery-failed</c> (bumps the dead-letter counter) and
    /// <c>undeliverable-here</c> (route to DLQ immediately).
    /// </summary>
    public Task ModifyAsync(
        AmqpIncomingDelivery delivery,
        bool? deliveryFailed = null,
        bool? undeliverableHere = null,
        CancellationToken cancellationToken = default)
    {
        var rented = ArrayPool<byte>.Shared.Rent(Performatives.ScratchSize);
        try
        {
            var modified = new Modified
            {
                DeliveryFailed = deliveryFailed,
                UndeliverableHere = undeliverableHere,
            };
            Modified.Write(rented, in modified, out var sl);
            return SendDispositionAsync(delivery.DeliveryId, rented.AsMemory(0, sl), settled: true, cancellationToken);
        }
        finally { ArrayPool<byte>.Shared.Return(rented); }
    }

    /// <summary>
    /// Long-poll equivalent: grants <paramref name="maxMessages"/> credit
    /// and drains up to that many deliveries within
    /// <paramref name="maxWait"/>. Returns early as soon as the cap is
    /// reached. The returned list may be empty if the wait elapses
    /// with no deliveries.
    /// </summary>
    /// <remarks>
    /// Single-shot semantics: granted credit is additive. Repeated
    /// calls on the same link will over-grant. Higher layers that
    /// reuse a long-lived receiver should track outstanding credit
    /// themselves and use <see cref="GrantCreditAsync"/> +
    /// <see cref="ReceiveMessageAsync"/> directly.
    /// </remarks>
    public async Task<IReadOnlyList<AmqpIncomingDelivery>> ReceiveBatchAsync(
        int maxMessages,
        TimeSpan maxWait,
        CancellationToken cancellationToken = default)
    {
        if (Role != AmqpRole.Receiver)
            throw new InvalidOperationException("ReceiveBatchAsync requires a receiver link.");
        if (maxMessages <= 0) throw new ArgumentOutOfRangeException(nameof(maxMessages));

        await GrantCreditAsync((uint)maxMessages, cancellationToken).ConfigureAwait(false);

        var batch = new List<AmqpIncomingDelivery>(maxMessages);

        // Drain anything already buffered before arming the timer.
        while (batch.Count < maxMessages && _incoming.Reader.TryRead(out var ready))
            batch.Add(ready);
        if (batch.Count >= maxMessages) return batch;

        using var deadlineCts = new CancellationTokenSource(maxWait);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadlineCts.Token);
        try
        {
            while (batch.Count < maxMessages
                   && await _incoming.Reader.WaitToReadAsync(linked.Token).ConfigureAwait(false))
            {
                while (batch.Count < maxMessages && _incoming.Reader.TryRead(out var item))
                    batch.Add(item);
            }

            // If the channel was completed (link is terminal) before the
            // deadline elapsed, the long-poll did not just time out — the
            // link is broken/closed. Don't mask that as an empty receive.
            if (_incoming.Reader.Completion.IsCompleted
                && !deadlineCts.IsCancellationRequested
                && !cancellationToken.IsCancellationRequested)
            {
                throw new InvalidOperationException(
                    "Receiver link is closed; no further deliveries will arrive.");
            }
        }
        catch (OperationCanceledException) when (deadlineCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // Deadline reached: return whatever we have.
        }

        return batch;
    }

    private async Task SendDispositionAsync(
        uint deliveryId,
        ReadOnlyMemory<byte> state,
        bool settled,
        CancellationToken cancellationToken)
    {
        var disposition = new AmqpDisposition
        {
            Role = AmqpRole.Receiver,
            First = deliveryId,
            Settled = settled,
            State = state,
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
                MaxMessageSize = _settings.MaxMessageSize == 0 ? null : _settings.MaxMessageSize,
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
            // CAS only from StateAttaching → StateAttached. If HandlePeerDetach
            // raced ahead and moved us to StateDetachingRemote / StateFinal
            // (peer sent attach immediately followed by detach), don't
            // resurrect the link — surface the detach to the caller.
            var prior = Interlocked.CompareExchange(ref _state, StateAttached, StateAttaching);
            if (prior != StateAttaching)
            {
                throw new InvalidOperationException(
                    "Peer detached during attach handshake.");
            }
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

            case PerformativeKind.Flow:
                if (Role == AmqpRole.Sender)
                {
                    AmqpFlow.Read(body, out var flow, out _);
                    HandlePeerFlow(flow);
                }
                break;

            default:
                break;
        }
    }

    private void HandlePeerFlow(AmqpFlow flow)
    {
        // §2.6.7: sender_credit = receiver.delivery_count + receiver.link_credit
        //                       - sender.delivery_count.
        var receiverDeliveryCount = flow.DeliveryCount ?? 0u;
        var receiverLinkCredit = flow.LinkCredit ?? 0u;
        int releaseCount;
        lock (_deliveryLock)
        {
            _peerReceiverDeliveryCount = receiverDeliveryCount;
            _peerReceiverLinkCredit = receiverLinkCredit;
            _peerFlowSeen = true;
            var available = (long)receiverDeliveryCount + receiverLinkCredit - _deliveryCount;
            // §2.6.7: link-credit is uint, so `available` may exceed int.MaxValue
            // (e.g. peer grants uint.MaxValue). Clamp to semaphore capacity so the
            // sender can drain permits up to int.MaxValue without being starved.
            releaseCount = available <= 0 ? 0 : (int)Math.Min(available, int.MaxValue);
            // Drain any stale permits so the semaphore reflects the new window.
            while (_creditAvailable.CurrentCount > 0 && _creditAvailable.Wait(0)) { }
        }
        if (releaseCount > 0) _creditAvailable.Release(releaseCount);
    }

    internal void OnRemoteHandleLearned(uint remoteHandle) => RemoteHandle = remoteHandle;

    /// <summary>
    /// True if <paramref name="bytes"/> would exceed the configured local
    /// <c>max-message-size</c> (§2.7.3). A configured value of <c>0</c>
    /// means "no limit".
    /// </summary>
    private bool ExceedsMaxMessageSize(ulong bytes)
        => _settings.MaxMessageSize != 0 && bytes > _settings.MaxMessageSize;

    /// <summary>
    /// Receives a transfer frame for this link. The body memory belongs
    /// to a pooled frame buffer; we copy out the payload immediately so
    /// the caller can dispose the frame. Implements §2.6.14 multi-frame
    /// reassembly: <c>more=true</c> transfers buffer payload until a
    /// final transfer arrives; <c>aborted=true</c> discards the in-progress
    /// delivery without enqueueing it.
    /// </summary>
    internal void DispatchTransfer(AmqpTransfer transfer, ReadOnlyMemory<byte> frameBody)
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

    private async ValueTask AcquireCreditAsync(CancellationToken ct)
    {
        while (true)
        {
            if (Volatile.Read(ref _state) != StateAttached)
                throw new InvalidOperationException("Link is no longer attached.");
            lock (_deliveryLock)
            {
                if (_peerFlowSeen)
                {
                    var available = (long)_peerReceiverDeliveryCount + _peerReceiverLinkCredit - _deliveryCount;
                    if (available > 0)
                    {
                        if (_creditAvailable.Wait(0)) return;
                    }
                }
            }
            await _creditAvailable.WaitAsync(ct).ConfigureAwait(false);
            if (Volatile.Read(ref _state) != StateAttached)
                throw new InvalidOperationException("Link is no longer attached.");
            lock (_deliveryLock)
            {
                var available = (long)_peerReceiverDeliveryCount + _peerReceiverLinkCredit - _deliveryCount;
                if (available > 0) return;
            }
        }
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
        // Per §2.6.5 the peer may detach at any time after sending its
        // attach, including before our own attach handshake completes
        // (e.g. broker rejects authorization). Accept from either state.
        int prior;
        while (true)
        {
            prior = Volatile.Read(ref _state);
            if (prior != StateAttached && prior != StateAttaching) break;
            if (Interlocked.CompareExchange(ref _state, StateDetachingRemote, prior) == prior) break;
        }
        _peerDetachReceived.TrySetResult(detach);

        if (prior == StateAttached || prior == StateAttaching)
        {
            // Peer initiated tear-down: mirror a detach back, then transition
            // to Final and unregister. Also unblock any local senders/receivers
            // (and the AttachAsync waiter) so they observe the detach rather
            // than hanging on the message channel.
            CompleteWaitersTerminal(cancelled: false);
            if (prior == StateAttaching)
            {
                // Surface a link-detached failure to AttachAsync.
                _peerAttachReceived.TrySetException(new InvalidOperationException(
                    "Peer detached during attach handshake."));
            }
            _ = Task.Run(async () =>
            {
                try { await SendDetachAsync(detach.Closed ?? true, error: null, CancellationToken.None).ConfigureAwait(false); }
                catch { /* best effort */ }
                finally
                {
                    Interlocked.Exchange(ref _state, StateFinal);
                    _session.UnregisterLink(this);
                }
            });
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
        // Wake any sender waiting on credit so it observes the terminal state.
        try { _creditAvailable.Release(int.MaxValue / 2); }
        catch (SemaphoreFullException) { /* already at max */ }
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
