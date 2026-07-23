using System.Buffers;
using System.Diagnostics;
using Aws2Azure.Amqp.Diagnostics;
using Aws2Azure.Amqp.Framing;
using Aws2Azure.Core.Observability;

namespace Aws2Azure.Amqp.Connection;

/// <summary>
/// Sender-role AMQP link: outgoing transfers, sender credit accounting,
/// and pending disposition resolution.
/// </summary>
internal sealed class AmqpSenderLink : AmqpLink
{
    private readonly object _deliveryLock = new();
    private readonly Dictionary<uint, TaskCompletionSource<AmqpSendOutcome>> _pendingSends = new();
    private uint _deliveryCount;

    // The receiver grants us a window via flow:
    // sender_credit = receiver.delivery_count + receiver.link_credit
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

    internal AmqpSenderLink(AmqpSession session, uint outgoingHandle, AmqpLinkSettings settings)
        : base(session, outgoingHandle, settings)
    {
    }

    /// <summary>
    /// Sends a single AMQP message and (when <paramref name="settled"/>
    /// is false) returns a task that completes when the receiver
    /// dispositions the delivery. When <paramref name="settled"/> is
    /// true the task completes synchronously with
    /// <see cref="AmqpDispositionOutcome.Accepted"/> once the wire write
    /// finishes.
    /// </summary>
    public override async Task<AmqpSendOutcome> SendMessageAsync(
        AmqpMessage message, bool settled = true, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (CurrentState != StateAttached)
            throw new InvalidOperationException($"Link is not attached (state={CurrentState}).");

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
            await Session.TransferWriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            RollbackUnusedCredit();
            throw;
        }
        try
        {
            deliveryId = Session.AllocateDeliveryId();

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
            Session.TransferWriteGate.Release();
        }

        if (settled)
        {
            if (timingEnabled)
            {
                AmqpTimingDiagnostics.LogSend(
                    link: Settings.Name,
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
                    link: Settings.Name,
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
    /// Receives a disposition for an outgoing transfer of ours.
    /// Resolves any pending <see cref="SendMessageAsync"/> task whose
    /// delivery-id falls in the range.
    /// </summary>
    internal override void DispatchDisposition(AmqpDisposition disposition)
    {
        base.DispatchDisposition(disposition);
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

    protected override void CompleteRoleWaitersTerminal(
        bool cancelled,
        AmqpError? peerError,
        Exception? terminalException)
    {
        TaskCompletionSource<AmqpSendOutcome>[] snapshot;
        lock (_deliveryLock)
        {
            snapshot = _pendingSends.Values.ToArray();
            _pendingSends.Clear();
        }
        foreach (var tcs in snapshot)
        {
            if (cancelled)
                tcs.TrySetCanceled();
            else if (terminalException is not null)
                tcs.TrySetException(terminalException);
            else
                tcs.TrySetException(BuildPeerDetachException(
                    "Link detached before disposition was received.",
                    peerError));
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

    protected override void HandlePeerFlow(AmqpFlow flow)
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

    private async ValueTask AcquireCreditAsync(CancellationToken ct)
    {
        while (true)
        {
            if (CurrentState != StateAttached)
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
        var maxFrame = Session.Connection.CurrentMaxFrameSize;

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
                    await Session.Connection.WriteSessionFrameAsync(
                        Session.OutgoingChannel,
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

                    await Session.Connection.WriteSessionFramesAtomicAsync(
                        Session.OutgoingChannel, segments, ct).ConfigureAwait(false);
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
}
