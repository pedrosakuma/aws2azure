using System.Buffers;
using System.Collections.Concurrent;
using Aws2Azure.Amqp.Framing;

namespace Aws2Azure.Amqp.Connection;

/// <summary>
/// Shared AMQP 1.0 link endpoint (§2.6). Owns the local handle on its
/// parent <see cref="AmqpSession"/> and implements the
/// <c>attach</c>/<c>detach</c> handshake.
/// </summary>
/// <remarks>
/// Role-specific transfer mechanics live in <see cref="AmqpSenderLink"/>
/// and <see cref="AmqpReceiverLink"/>; the session continues to route
/// inbound frames through this shared base.
/// </remarks>
internal abstract class AmqpLink
{
    protected const int StateClosed = 0;
    protected const int StateAttaching = 1;
    protected const int StateAttached = 2;
    protected const int StateDetachingLocal = 3;
    protected const int StateDetachingRemote = 4;
    protected const int StateFinal = 5;

    private readonly AmqpSession _session;
    private readonly AmqpLinkSettings _settings;
    private readonly TaskCompletionSource<AmqpAttach> _peerAttachReceived =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<AmqpDetach> _peerDetachReceived =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _finalStateReached =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal static TimeSpan PeerDetachFinalizationTimeout { get; set; } = TimeSpan.FromSeconds(30);
    internal static TimeSpan ReceiverSettlementConfirmationTimeout { get; set; } = TimeSpan.FromSeconds(30);

    // Captured peer error when the peer detached with one. Reads of
    // receiver-side queues and sender-side pending outcomes surface this
    // through AmqpLinkException so callers can map condition-specific
    // failures without inspecting message text. Boxed so Volatile can
    // publish the struct safely across threads.
    private sealed class FaultBox { public AmqpError Error; }
    private FaultBox? _pendingFault;
    private readonly ConcurrentDictionary<uint, TaskCompletionSource> _pendingReceiverSettlements = new();

    private int _state = StateClosed;
    private int _attachWriteAttempted;
    private int _attachFrameSent;

    protected AmqpLink(AmqpSession session, uint outgoingHandle, AmqpLinkSettings settings)
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
    internal bool AttachWriteAttempted => Volatile.Read(ref _attachWriteAttempted) != 0;
    internal bool AttachFrameSent => Volatile.Read(ref _attachFrameSent) != 0;

    protected AmqpSession Session => _session;
    protected AmqpLinkSettings Settings => _settings;
    protected int CurrentState => Volatile.Read(ref _state);
    protected AmqpError? PendingPeerError => Volatile.Read(ref _pendingFault)?.Error;

    // ---- role APIs -------------------------------------------------------

    public virtual Task<AmqpSendOutcome> SendMessageAsync(
        AmqpMessage message, bool settled = true, CancellationToken cancellationToken = default)
    {
        if (message is null)
            return Task.FromException<AmqpSendOutcome>(new ArgumentNullException(nameof(message)));
        return Task.FromException<AmqpSendOutcome>(
            new InvalidOperationException("SendMessageAsync requires a sender link."));
    }

    public virtual Task GrantCreditAsync(uint additional, CancellationToken cancellationToken = default)
        => Task.FromException(new InvalidOperationException("GrantCreditAsync requires a receiver link."));

    public virtual Task<AmqpIncomingDelivery> ReceiveMessageAsync(CancellationToken cancellationToken = default)
        => Task.FromException<AmqpIncomingDelivery>(
            new InvalidOperationException("ReceiveMessageAsync requires a receiver link."));

    public virtual Task<IReadOnlyList<AmqpIncomingDelivery>> ReceiveBatchAsync(
        int maxMessages,
        TimeSpan maxWait,
        TimeSpan? tailWait = null,
        CancellationToken cancellationToken = default)
    {
        if (maxMessages <= 0)
            return Task.FromException<IReadOnlyList<AmqpIncomingDelivery>>(
                new ArgumentOutOfRangeException(nameof(maxMessages)));
        return Task.FromException<IReadOnlyList<AmqpIncomingDelivery>>(
            new InvalidOperationException("ReceiveBatchAsync requires a receiver link."));
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

    // ---- lifecycle -------------------------------------------------------

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
                Properties = _settings.Properties,
            };

            var rentedAtt = ArrayPool<byte>.Shared.Rent(Performatives.ScratchSize);
            try
            {
                AmqpAttach.Write(rentedAtt, in localAttach, out var attLen);
                await _session.Connection.WriteSessionFrameAsync(
                    _session.OutgoingChannel,
                    rentedAtt.AsMemory(0, attLen),
                    cancellationToken,
                    () => Volatile.Write(ref _attachWriteAttempted, 1)).ConfigureAwait(false);
                Volatile.Write(ref _attachFrameSent, 1);
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
            TransitionToFinal();
            CompleteWaitersTerminal(cancelled: false);
            _session.UnregisterLink(this);
        }
    }

    internal void Abort(Exception? exception = null)
    {
        TransitionToFinal();
        if (exception is null)
        {
            _peerAttachReceived.TrySetCanceled();
            _peerDetachReceived.TrySetCanceled();
            CompleteWaitersTerminal(cancelled: true);
        }
        else
        {
            _peerAttachReceived.TrySetException(exception);
            _peerDetachReceived.TrySetException(exception);
            CompleteWaitersTerminal(cancelled: false, exception);
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

    internal virtual void DispatchTransfer(AmqpTransfer transfer, ReadOnlyMemory<byte> frameBody) { }

    internal virtual void DispatchDisposition(AmqpDisposition disposition)
    {
        if (disposition.Role != AmqpRole.Sender || disposition.Settled != true)
            return;

        var first = disposition.First;
        var last = disposition.Last ?? first;
        if (last < first)
        {
            return;
        }

        foreach (var pending in _pendingReceiverSettlements)
        {
            if (pending.Key >= first &&
                pending.Key <= last &&
                _pendingReceiverSettlements.TryRemove(pending.Key, out var completion))
            {
                completion.TrySetResult();
            }
        }
    }

    internal void OnRemoteHandleLearned(uint remoteHandle) => RemoteHandle = remoteHandle;

    protected virtual void HandlePeerFlow(AmqpFlow flow) { }

    protected static AmqpLinkException BuildPeerDetachException(string baseMessage, AmqpError? peerError)
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

    protected abstract void CompleteRoleWaitersTerminal(
        bool cancelled,
        AmqpError? peerError,
        Exception? terminalException);

    private static ReadOnlyMemory<byte> CopyOpaque(ReadOnlyMemory<byte> source)
    {
        if (source.IsEmpty) return ReadOnlyMemory<byte>.Empty;
        var copy = new byte[source.Length];
        source.CopyTo(copy);
        return copy;
    }

    private async Task SendDispositionAsync(
        uint deliveryId,
        ReadOnlyMemory<byte> state,
        bool settled,
        CancellationToken cancellationToken)
    {
        var awaitConfirmation = Role == AmqpRole.Receiver
            && Settings.ReceiverSettleMode == AmqpReceiverSettleMode.Second;
        TaskCompletionSource? confirmation = null;
        if (awaitConfirmation)
        {
            confirmation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_pendingReceiverSettlements.TryAdd(deliveryId, confirmation))
                throw new InvalidOperationException($"Settlement is already pending for delivery-id {deliveryId}.");
            if (CurrentState != StateAttached)
            {
                _pendingReceiverSettlements.TryRemove(deliveryId, out _);
                throw BuildPeerDetachException(
                    "Receiver link closed before settlement could be sent.",
                    PendingPeerError);
            }
            settled = false;
        }

        var disposition = new AmqpDisposition
        {
            Role = AmqpRole.Receiver,
            First = deliveryId,
            Settled = settled,
            State = state,
        };
        var rented = ArrayPool<byte>.Shared.Rent(Performatives.ScratchSize);
        var writeStarted = false;
        try
        {
            AmqpDisposition.Write(rented, in disposition, out var dl);
            await _session.Connection.WriteSessionFrameAsync(
                _session.OutgoingChannel,
                rented.AsMemory(0, dl),
                cancellationToken,
                () => writeStarted = true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (confirmation is not null)
                _pendingReceiverSettlements.TryRemove(deliveryId, out _);
            if (writeStarted)
            {
                var connectionException = new AmqpConnectionException(
                    "Disposition write failed after transport emission began.",
                    ex,
                    AmqpErrorKind.Transient);
                await _session.Connection.AbortAsync(connectionException).ConfigureAwait(false);
                throw new AmqpLinkException(
                    $"Settlement outcome is indeterminate for delivery-id {deliveryId}.",
                    ex,
                    AmqpErrorKind.Transient);
            }
            throw;
        }
        finally { ArrayPool<byte>.Shared.Return(rented); }

        if (confirmation is not null)
        {
            try
            {
                // Once the disposition frame is on the wire, caller
                // cancellation cannot tell us whether the broker settled the
                // delivery. Finish the second-mode handshake so retries never
                // target an already-settled delivery.
                await confirmation.Task
                    .WaitAsync(ReceiverSettlementConfirmationTimeout)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException ex)
            {
                var linkException = new AmqpLinkException(
                    $"Broker did not confirm receiver settlement for delivery-id {deliveryId}.",
                    ex,
                    AmqpErrorKind.Transient);
                await _session.Connection.AbortAsync(new AmqpConnectionException(
                    linkException.Message,
                    linkException,
                    AmqpErrorKind.Transient)).ConfigureAwait(false);
                throw linkException;
            }
            finally
            {
                _pendingReceiverSettlements.TryRemove(deliveryId, out _);
            }
        }
    }

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
            // than hanging on role-local waiters.
            CompleteWaitersTerminal(cancelled: false);
            if (prior == StateAttaching)
            {
                // Surface a link-detached failure to AttachAsync.
                _peerAttachReceived.TrySetException(BuildPeerDetachException(
                    "Peer detached during attach handshake.", peerError));
            }
            _ = FinalizePeerDetachAsync(detach.Closed ?? true);
        }
    }

    private async Task FinalizePeerDetachAsync(bool closed)
    {
        try
        {
            using var cts = new CancellationTokenSource(PeerDetachFinalizationTimeout);
            await SendDetachAsync(closed, error: null, cts.Token).ConfigureAwait(false);
        }
        catch { /* best effort; terminal state is still published below */ }
        finally
        {
            TransitionToFinal();
            _session.UnregisterLink(this);
        }
    }

    /// <summary>
    /// Releases all link-level waiters. Called from every terminal path:
    /// local detach, peer-initiated detach, abort. Idempotent.
    /// </summary>
    private void CompleteWaitersTerminal(bool cancelled, Exception? terminalException = null)
    {
        var peerError = Volatile.Read(ref _pendingFault)?.Error;
        CompleteRoleWaitersTerminal(cancelled, peerError, terminalException);
        foreach (var pending in _pendingReceiverSettlements)
        {
            if (!_pendingReceiverSettlements.TryRemove(pending.Key, out var completion))
                continue;
            if (cancelled)
                completion.TrySetCanceled();
            else if (terminalException is not null)
                completion.TrySetException(terminalException);
            else
                completion.TrySetException(BuildPeerDetachException(
                    "Receiver link closed before settlement was confirmed.",
                    peerError));
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

    // Local detach, peer-detach mirror finalization, and parent abort all
    // publish terminal state through the same signal so concurrent detach
    // callers never poll or depend on which path won the CAS.
    private void TransitionToFinal()
    {
        Interlocked.Exchange(ref _state, StateFinal);
        _finalStateReached.TrySetResult();
    }

    private Task WaitForFinalAsync(CancellationToken ct)
        => Volatile.Read(ref _state) == StateFinal
            ? Task.CompletedTask
            : _finalStateReached.Task.WaitAsync(ct);
}
