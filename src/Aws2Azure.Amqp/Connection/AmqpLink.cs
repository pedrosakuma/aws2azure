using System.Buffers;
using System.Diagnostics;
using Aws2Azure.Amqp.Diagnostics;
using Aws2Azure.Amqp.Framing;
using Aws2Azure.Core.Observability;

namespace Aws2Azure.Amqp.Connection;

/// <summary>
/// AMQP 1.0 link endpoint (§2.6). Owns the local handle on its parent
/// <see cref="AmqpSession"/> and implements the <c>attach</c>/<c>detach</c>
/// handshake.
/// </summary>
/// <remarks>
/// Beyond the attach/detach lifecycle, the link carries the full
/// transfer surface: credit-flow-controlled outgoing sends, incoming
/// delivery reassembly, and disposition
/// (settle / accept / release / reject / modify), all driven through
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

    // Captured peer error when the peer detached with one. Reads of
    // _incoming after a peer-initiated detach surface this through
    // AmqpLinkException so callers (e.g. the SQS AMQP handler) can map
    // condition → SqsError without inspecting message text. Boxed in a
    // small holder so Volatile.Read/Write can publish it across threads
    // (AmqpError is a struct, so Volatile<T> on Nullable<AmqpError> is
    // not legal).
    private sealed class FaultBox { public AmqpError Error; }
    private FaultBox? _pendingFault;

    // Sender-side delivery tracking.
    private readonly object _deliveryLock = new();
    private readonly Dictionary<uint, TaskCompletionSource<AmqpSendOutcome>> _pendingSends = new();
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
    // - sender.delivery_count (§2.6.7). Credit is computed under
    // _deliveryLock from the peer-observed counters as a `long` (which
    // can absorb the full uint.MaxValue ceiling §2.7.4 allows for
    // link-credit). AcquireCreditAsync is the credit gate: it checks
    // available > 0 and increments _deliveryCount in the SAME critical
    // section, so concurrent senders cannot overshoot. When credit is
    // exhausted, waiters park on _creditPulse — a fresh TCS swapped in
    // on every successful peer flow. This intentionally decouples
    // accounting from a SemaphoreSlim permit count: the prior design
    // capped releases at int.MaxValue and would deadlock once a sender
    // drained that many permits without an intervening flow (peer would
    // not re-flow because logical credit remained, refs #51).
    private uint _peerReceiverDeliveryCount;
    private uint _peerReceiverLinkCredit;
    private bool _peerFlowSeen;
    private TaskCompletionSource _creditPulse = new(TaskCreationOptions.RunContinuationsAsynchronously);

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
    public async Task<AmqpSendOutcome> SendMessageAsync(
        AmqpMessage message, bool settled = true, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (Role != AmqpRole.Sender)
            throw new InvalidOperationException("SendMessageAsync requires a sender link.");
        if (Volatile.Read(ref _state) != StateAttached)
            throw new InvalidOperationException($"Link is not attached (state={_state}).");

        var timingEnabled = AmqpTimingDiagnostics.Enabled;
        var tsStart = Stopwatch.GetTimestamp(); // Always capture for BackendTimingContext

        // §2.6.7: wait for receiver credit before transferring. If the
        // peer has never sent a flow we treat that as no credit and
        // block here until one arrives — matches strict broker behaviour
        // (Service Bus, ActiveMQ Artemis) that detach a link whose
        // sender pushes transfers without granted credit. AcquireCredit
        // atomically increments _deliveryCount under _deliveryLock so
        // concurrent senders can't overshoot a tight credit window.
        await AcquireCreditAsync(cancellationToken).ConfigureAwait(false);
        var tsAfterCredit = timingEnabled ? Stopwatch.GetTimestamp() : 0L;

        // Encode bare message into pooled buffer (outside the session
        // write gate — body encoding does not depend on delivery-id and
        // is safe to run concurrently with other senders on the same
        // session).
        using var payload = message.EncodePooled();
        var payloadBytes = payload.Memory.Length;

        TaskCompletionSource<AmqpSendOutcome>? tcs = null;
        uint deliveryId;
        long tsAfterWrite;

        // Atomic (allocate-delivery-id + register-pending + wire-write)
        // under the session-scoped TransferWriteGate. AMQP §2.6.12
        // requires sequential delivery-ids on the wire; without this
        // serialisation two concurrent senders on the same session can
        // race so the higher id is transferred first, which strict
        // brokers (Service Bus) reject with a per-delivery Rejected
        // outcome. The gate is released BEFORE awaiting the broker
        // disposition so callers pipeline their round-trips — the cost
        // of the gate is bounded by the wire-write itself, not by the
        // far-side latency.
        //
        // Credit-rollback: AcquireCreditAsync has already incremented
        // _deliveryCount. If the gate-wait or the wire-write fails
        // before the transfer is on the wire we MUST give the credit
        // back, otherwise local credit drifts below what the peer
        // believes and future sends stall.
        try
        {
            await _session.TransferWriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            RollbackUnusedCredit();
            throw;
        }
        try
        {
            deliveryId = _session.AllocateDeliveryId();

            if (!settled)
            {
                tcs = new TaskCompletionSource<AmqpSendOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (_deliveryLock) _pendingSends[deliveryId] = tcs;
            }

            // delivery-tag: 4 bytes of delivery-id (big-endian) — uniqueness only matters per-link-per-unsettled.
            Span<byte> tag = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(tag, deliveryId);
            var tagArr = tag.ToArray();

            try
            {
                await WriteTransferFragmentedAsync(
                    deliveryId, tagArr, payload.Memory, settled, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Wire write failed — drop any pending entry so it doesn't leak,
                // and give back the credit the transfer never actually consumed.
                if (!settled)
                {
                    lock (_deliveryLock) _pendingSends.Remove(deliveryId);
                }
                RollbackUnusedCredit();
                throw;
            }

            tsAfterWrite = timingEnabled ? Stopwatch.GetTimestamp() : 0L;
        }
        finally
        {
            _session.TransferWriteGate.Release();
        }

        if (settled)
        {
            if (timingEnabled)
            {
                AmqpTimingDiagnostics.LogSend(
                    link: _settings.Name,
                    totalUs: AmqpTimingDiagnostics.ElapsedMicros(tsStart),
                    creditUs: (tsAfterCredit - tsStart) * 1_000_000L / Stopwatch.Frequency,
                    writeUs: (tsAfterWrite - tsAfterCredit) * 1_000_000L / Stopwatch.Frequency,
                    dispositionUs: 0L,
                    settled: true,
                    payloadBytes: payloadBytes);
            }
            // Record to ambient backend timing context (if set by ServiceModuleRegistry)
            BackendTimingContext.RecordBackendCall(Stopwatch.GetElapsedTime(tsStart));
            return AmqpSendOutcome.Accepted;
        }

        using (cancellationToken.Register(() =>
        {
            lock (_deliveryLock) _pendingSends.Remove(deliveryId);
            tcs!.TrySetCanceled(cancellationToken);
        }))
        {
            var outcome = await tcs!.Task.ConfigureAwait(false);
            if (timingEnabled)
            {
                var tsAfterDisp = Stopwatch.GetTimestamp();
                AmqpTimingDiagnostics.LogSend(
                    link: _settings.Name,
                    totalUs: AmqpTimingDiagnostics.ElapsedMicros(tsStart),
                    creditUs: (tsAfterCredit - tsStart) * 1_000_000L / Stopwatch.Frequency,
                    writeUs: (tsAfterWrite - tsAfterCredit) * 1_000_000L / Stopwatch.Frequency,
                    dispositionUs: (tsAfterDisp - tsAfterWrite) * 1_000_000L / Stopwatch.Frequency,
                    settled: false,
                    payloadBytes: payloadBytes);
            }
            // Record to ambient backend timing context (if set by ServiceModuleRegistry)
            BackendTimingContext.RecordBackendCall(Stopwatch.GetElapsedTime(tsStart));
            return outcome;
        }
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
    public async Task<IReadOnlyList<AmqpIncomingDelivery>> ReceiveBatchAsync(
        int maxMessages,
        TimeSpan maxWait,
        TimeSpan? tailWait = null,
        CancellationToken cancellationToken = default)
    {
        if (Role != AmqpRole.Receiver)
            throw new InvalidOperationException("ReceiveBatchAsync requires a receiver link.");
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
                    Volatile.Read(ref _pendingFault)?.Error);
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
                var src = new AmqpSource
                {
                    Address = _settings.SourceAddress,
                    Filter = _settings.SourceFilter,
                };
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
                throw BuildPeerDetachException(
                    "Peer detached during attach handshake.",
                    Volatile.Read(ref _pendingFault)?.Error);
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
        // Accept both StateAttached and StateAttaching as valid starting
        // points. The latter covers a window where an inbound condition
        // (e.g. oversize transfer) demands a detach before the attach
        // handshake has finished. CAS in a loop so we observe the right
        // prior value race-free.
        int prior;
        while (true)
        {
            prior = Volatile.Read(ref _state);
            if (prior == StateFinal || prior == StateClosed) return;
            if (prior == StateDetachingLocal || prior == StateDetachingRemote)
            {
                await WaitForFinalAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            if (prior != StateAttached && prior != StateAttaching)
                throw new InvalidOperationException($"Cannot detach link from state {prior}.");
            if (Interlocked.CompareExchange(ref _state, StateDetachingLocal, prior) == prior)
                break;
        }

        if (prior == StateAttaching)
        {
            // Surface the detach to any pending AttachAsync waiter so it
            // doesn't hang on _peerAttachReceived.
            _peerAttachReceived.TrySetException(new InvalidOperationException(
                "Link detached during attach handshake."));
        }

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
                // AmqpAttach.Read returns the opaque fields (Source, Target,
                // Unsettled, capabilities, properties) as slices into `body` —
                // which is the inbound rented frame buffer. The connection's
                // read loop returns the frame to the pool immediately after
                // dispatch, so anything we store past this point must own its
                // own backing storage. Deep-copy each opaque slot.
                attach = attach with
                {
                    Source = CopyOpaque(attach.Source),
                    Target = CopyOpaque(attach.Target),
                    Unsettled = CopyOpaque(attach.Unsettled),
                    OfferedCapabilities = CopyOpaque(attach.OfferedCapabilities),
                    DesiredCapabilities = CopyOpaque(attach.DesiredCapabilities),
                    Properties = CopyOpaque(attach.Properties),
                };
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

    /// <summary>
    /// Copies the opaque slice into a fresh heap array so it survives
    /// the inbound frame's return-to-pool. No-op for empty inputs.
    /// </summary>
    private static ReadOnlyMemory<byte> CopyOpaque(ReadOnlyMemory<byte> source)
    {
        if (source.IsEmpty) return ReadOnlyMemory<byte>.Empty;
        var copy = new byte[source.Length];
        source.CopyTo(copy);
        return copy;
    }

    private void HandlePeerFlow(AmqpFlow flow)
    {
        // §2.6.7: sender_credit = receiver.delivery_count + receiver.link_credit
        //                       - sender.delivery_count.
        var receiverDeliveryCount = flow.DeliveryCount ?? 0u;
        var receiverLinkCredit = flow.LinkCredit ?? 0u;
        TaskCompletionSource? toPulse = null;
        lock (_deliveryLock)
        {
            var hadFlow = _peerFlowSeen;
            _peerReceiverDeliveryCount = receiverDeliveryCount;
            _peerReceiverLinkCredit = receiverLinkCredit;
            _peerFlowSeen = true;
            var available = (long)receiverDeliveryCount + receiverLinkCredit - _deliveryCount;
            // Wake parked senders only when (a) this is the first flow
            // (callers waiting on the initial credit grant) or
            // (b) the credit window grew. Spurious wakes are harmless —
            // AcquireCreditAsync re-checks `available > 0` under the lock
            // before returning — but unnecessary.
            if (!hadFlow || available > 0)
            {
                toPulse = _creditPulse;
                _creditPulse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
        toPulse?.TrySetResult();
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
        var (outcome, condition, description) =
            AmqpDispositionOutcomeExtractor.FromWithError(disposition.State);
        var result = new AmqpSendOutcome(outcome, condition, description);

        for (uint id = first; id <= last; id++)
        {
            TaskCompletionSource<AmqpSendOutcome>? tcs;
            lock (_deliveryLock)
            {
                if (!_pendingSends.Remove(id, out tcs)) continue;
            }
            tcs.TrySetResult(result);
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
            Task pulse;
            lock (_deliveryLock)
            {
                if (_peerFlowSeen)
                {
                    var available = (long)_peerReceiverDeliveryCount + _peerReceiverLinkCredit - _deliveryCount;
                    if (available > 0)
                    {
                        // Atomic credit consumption: increment under the same
                        // lock that observed available > 0 so concurrent senders
                        // cannot all pass through the same 1-permit window.
                        // The caller no longer increments _deliveryCount itself.
                        _deliveryCount++;
                        return;
                    }
                }
                pulse = _creditPulse.Task;
            }
            await pulse.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Returns credit that <see cref="AcquireCreditAsync"/> consumed when
    /// the corresponding transfer never actually reached the wire
    /// (caller cancelled while waiting for the session
    /// <see cref="AmqpSession.TransferWriteGate"/>, or
    /// <c>WriteTransferFragmentedAsync</c> threw before the bytes left
    /// the socket). Pulses <c>_creditPulse</c> so any other sender
    /// parked on credit observes the rollback.
    /// </summary>
    private void RollbackUnusedCredit()
    {
        TaskCompletionSource toPulse;
        lock (_deliveryLock)
        {
            // _deliveryCount is the count of OUTGOING transfers we have
            // consumed credit for; we only roll back if there is
            // something to roll back (defensive against double-rollback).
            if (_deliveryCount == 0) return;
            _deliveryCount--;
            toPulse = _creditPulse;
            _creditPulse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        toPulse.TrySetResult();
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
        AmqpError? peerError = null;
        if (!detach.Error.IsEmpty)
        {
            try
            {
                AmqpError.Read(detach.Error, out var parsed, out _);
                peerError = parsed;
                Volatile.Write(ref _pendingFault, new FaultBox { Error = parsed });
            }
            catch
            {
                // Malformed error payload — treat as no diagnostic.
            }
        }

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
                _peerAttachReceived.TrySetException(BuildPeerDetachException(
                    "Peer detached during attach handshake.", peerError));
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

    private static AmqpLinkException BuildPeerDetachException(string baseMessage, AmqpError? peerError)
    {
        if (peerError is { } err)
        {
            var msg = string.IsNullOrEmpty(err.Description)
                ? $"{baseMessage} condition={err.Condition}"
                : $"{baseMessage} condition={err.Condition}: {err.Description}";
            return new AmqpLinkException(msg, err.Kind)
            {
                PeerCondition = err.Condition,
                PeerDescription = err.Description,
            };
        }
        return new AmqpLinkException(baseMessage);
    }

    /// <summary>
    /// Releases all link-level waiters (incoming message reader and any
    /// pending unsettled-send TCSes). Called from every terminal path:
    /// local detach, peer-initiated detach, abort. Idempotent.
    /// </summary>
    private void CompleteWaitersTerminal(bool cancelled)
    {
        _incoming.Writer.TryComplete();
        TaskCompletionSource<AmqpSendOutcome>[] snapshot;
        lock (_deliveryLock)
        {
            snapshot = _pendingSends.Values.ToArray();
            _pendingSends.Clear();
        }
        foreach (var tcs in snapshot)
        {
            if (cancelled) tcs.TrySetCanceled();
            else tcs.TrySetException(BuildPeerDetachException(
                "Link detached before disposition was received.",
                Volatile.Read(ref _pendingFault)?.Error));
        }
        // Wake any sender parked on credit so it observes the terminal
        // state (the StateAttached check at the top of AcquireCreditAsync
        // throws InvalidOperationException after the pulse fires).
        TaskCompletionSource toPulse;
        lock (_deliveryLock)
        {
            toPulse = _creditPulse;
            _creditPulse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        toPulse.TrySetResult();
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
