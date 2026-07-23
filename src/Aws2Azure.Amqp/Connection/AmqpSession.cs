using System.Buffers;
using Aws2Azure.Amqp.Framing;

namespace Aws2Azure.Amqp.Connection;

/// <summary>
/// AMQP 1.0 session state machine (§2.5). Owns a single
/// (outgoing-channel, remote-channel) pair on its parent
/// <see cref="AmqpConnection"/> and implements the
/// <c>begin</c>/<c>end</c> handshake.
/// </summary>
/// <remarks>
/// Beyond the begin/end lifecycle, the session also routes link
/// performatives (attach/flow/transfer/disposition/detach) to the owning
/// <see cref="AmqpLink"/> through <see cref="DispatchIncomingFrame"/>.
/// </remarks>
internal sealed class AmqpSession
{
    private const int StateClosed = 0;
    private const int StateOpening = 1;
    private const int StateOpened = 2;
    private const int StateClosingLocal = 3;
    private const int StateClosingRemote = 4;
    private const int StateFinal = 5;

    private readonly AmqpConnection _connection;
    private readonly AmqpSessionSettings _settings;
    private readonly TaskCompletionSource<AmqpBegin> _peerBeginReceived =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<AmqpEnd> _peerEndReceived =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _linkLock = new();
    private readonly Dictionary<uint, AmqpLink> _linksByOutgoingHandle = new();
    private readonly Dictionary<uint, AmqpLink> _linksByIncomingHandle = new();
    private readonly Dictionary<string, AmqpLink> _linksByName = new(StringComparer.Ordinal);
    private uint _nextOutgoingHandle;

    // Session-level transfer accounting (§2.5.6): a single monotonic
    // delivery-id counter. Full flow-control windowing is not implemented.
    private uint _nextOutgoingId;

    private int _state = StateClosed;

    internal AmqpSession(AmqpConnection connection, ushort outgoingChannel, AmqpSessionSettings settings)
    {
        _connection = connection;
        OutgoingChannel = outgoingChannel;
        _settings = settings;
        _nextOutgoingId = settings.NextOutgoingId;
    }

    /// <summary>Allocates the next delivery-id for an outgoing transfer.</summary>
    internal uint AllocateDeliveryId() => Interlocked.Increment(ref _nextOutgoingId) - 1;

    /// <summary>
    /// Session-scoped serialiser for the (allocate-delivery-id + write-frame)
    /// critical section on the sender side. AMQP §2.6.12 requires that
    /// delivery-ids appear on the wire in strictly sequential order; we
    /// therefore allocate the id and write the transfer frame atomically
    /// under this gate. Held only across the synchronous wire-write — the
    /// settled=false disposition wait happens outside, allowing concurrent
    /// senders to pipeline their broker round-trips.
    /// </summary>
    internal SemaphoreSlim TransferWriteGate { get; } = new(1, 1);

    /// <summary>Parent connection, exposed so links can use its write hook.</summary>
    internal AmqpConnection Connection => _connection;

    /// <summary>Our local channel number (the one peer sees as remote-channel).</summary>
    public ushort OutgoingChannel { get; }

    /// <summary>Peer's local channel number, learned from their <c>begin</c> reply.</summary>
    public ushort RemoteChannel { get; private set; }

    /// <summary>Peer's <c>begin</c> performative, available after <see cref="OpenAsync"/>.</summary>
    public AmqpBegin RemoteBegin { get; private set; }

    /// <summary>True once the session has reached its terminal state.</summary>
    public bool IsClosed => Volatile.Read(ref _state) >= StateClosingLocal;

    /// <summary>
    /// Allocates the next handle (under the peer's <c>begin.handle-max</c>),
    /// registers a new <see cref="AmqpLink"/>, and performs the
    /// <c>attach</c> handshake.
    /// </summary>
    public async Task<AmqpLink> AttachLinkAsync(
        AmqpLinkSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (Volatile.Read(ref _state) != StateOpened)
            throw new InvalidOperationException("Session is not open.");

        AmqpLink link;
        lock (_linkLock)
        {
            if (_linksByName.ContainsKey(settings.Name))
                throw new InvalidOperationException($"Link '{settings.Name}' is already attached on this session.");

            var max = RemoteBegin.HandleMax ?? uint.MaxValue;
            uint handle = _nextOutgoingHandle;
            while (_linksByOutgoingHandle.ContainsKey(handle))
            {
                if (handle == max)
                    throw new InvalidOperationException("No link handles available under negotiated handle-max.");
                handle++;
            }
            if (handle > max)
                throw new InvalidOperationException("No link handles available under negotiated handle-max.");

            link = settings.Role == AmqpRole.Sender
                ? new AmqpSenderLink(this, handle, settings)
                : new AmqpReceiverLink(this, handle, settings);
            _linksByOutgoingHandle.Add(handle, link);
            _linksByName.Add(settings.Name, link);
            _nextOutgoingHandle = handle == max ? 0 : handle + 1;
        }

        try
        {
            await link.AttachAsync(cancellationToken).ConfigureAwait(false);
            return link;
        }
        catch
        {
            if (!link.AttachWriteAttempted)
            {
                link.Abort();
            }
            else if (!link.AttachFrameSent)
            {
                await _connection.AbortAsync(new AmqpConnectionException(
                    "AMQP attach write had an indeterminate outcome; the connection was aborted.",
                    AmqpErrorKind.Transient)).ConfigureAwait(false);
            }
            else
            {
                try
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
                    await link.DetachAsync(closed: true, cancellationToken: cleanup.Token).ConfigureAwait(false);
                }
                catch
                {
                    await _connection.AbortAsync(new AmqpConnectionException(
                        "AMQP attach cancellation cleanup failed; the connection was aborted to release remote session state.",
                        AmqpErrorKind.Transient)).ConfigureAwait(false);
                }
            }
            UnregisterLink(link);
            throw;
        }
    }

    /// <summary>Internal hook used by <see cref="AmqpLink"/> on detach/abort.</summary>
    internal void UnregisterLink(AmqpLink link)
    {
        lock (_linkLock)
        {
            _linksByOutgoingHandle.Remove(link.OutgoingHandle);
            _linksByName.Remove(link.Name);
            if (_linksByIncomingHandle.TryGetValue(link.RemoteHandle, out var existing) && existing == link)
                _linksByIncomingHandle.Remove(link.RemoteHandle);
        }
    }

    /// <summary>
    /// Sends our <c>begin</c> and awaits the peer's <c>begin</c> reply.
    /// </summary>
    public async Task OpenAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _state, StateOpening, StateClosed) != StateClosed)
            throw new InvalidOperationException($"Session already opened or in state {_state}.");

        var local = new AmqpBegin
        {
            // RemoteChannel left null: we are the initiator.
            NextOutgoingId = _settings.NextOutgoingId,
            IncomingWindow = _settings.IncomingWindow,
            OutgoingWindow = _settings.OutgoingWindow,
            HandleMax = _settings.HandleMax,
        };

        var rented = ArrayPool<byte>.Shared.Rent(Performatives.ScratchSize);
        try
        {
            AmqpBegin.Write(rented, in local, out var written);
            await _connection.WriteSessionFrameAsync(
                OutgoingChannel, rented.AsMemory(0, written), cancellationToken).ConfigureAwait(false);
        }
        finally { ArrayPool<byte>.Shared.Return(rented); }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await using (linked.Token.Register(() => _peerBeginReceived.TrySetCanceled(linked.Token)))
        {
            var peerBegin = await _peerBeginReceived.Task.ConfigureAwait(false);
            RemoteBegin = peerBegin;
            Interlocked.Exchange(ref _state, StateOpened);
        }
    }

    /// <summary>
    /// Sends <c>end</c> and waits for the peer's <c>end</c>. Optional
    /// <paramref name="error"/> surfaces a local failure to the peer.
    /// </summary>
    public async Task CloseAsync(AmqpError? error = null, CancellationToken cancellationToken = default)
    {
        var prior = Interlocked.CompareExchange(ref _state, StateClosingLocal, StateOpened);
        if (prior == StateFinal || prior == StateClosed) return;
        if (prior == StateClosingLocal || prior == StateClosingRemote)
        {
            await WaitForFinalAsync(cancellationToken).ConfigureAwait(false);
            return;
        }
        if (prior != StateOpened)
            throw new InvalidOperationException($"Cannot close session from state {prior}.");

        try
        {
            await SendEndAsync(error, cancellationToken).ConfigureAwait(false);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(TimeSpan.FromSeconds(30));
            await using (linked.Token.Register(() => _peerEndReceived.TrySetCanceled(linked.Token)))
            {
                try { await _peerEndReceived.Task.ConfigureAwait(false); }
                catch (OperationCanceledException) { /* swallow — we still tear down */ }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _state, StateFinal);
            _connection.UnregisterSession(this);
        }
    }

    // ---- dispatch (invoked by the connection's read loop) ----------------

    /// <summary>
    /// Handles a frame received on this session's incoming channel,
    /// routing each performative to the owning link (or to the session
    /// lifecycle for begin/end).
    /// </summary>
    internal void DispatchIncomingFrame(PerformativeKind kind, ReadOnlyMemory<byte> body)
    {
        switch (kind)
        {
            case PerformativeKind.Begin:
                AmqpBegin.Read(body, out var begin, out _);
                if (begin.RemoteChannel is { } rc) { /* echo only; we already know our channel */ }
                _peerBeginReceived.TrySetResult(begin);
                break;

            case PerformativeKind.End:
                AmqpEnd.Read(body, out var end, out _);
                HandlePeerEnd(end);
                break;

            case PerformativeKind.Attach:
                // Peer's attach binds their handle to one of our links by Name.
                AmqpAttach.Read(body, out var attach, out _);
                AmqpLink? attachLink;
                lock (_linkLock)
                {
                    if (!_linksByName.TryGetValue(attach.Name, out attachLink))
                        return; // unknown link — drop (server-initiated attach not supported in 5b)
                    attachLink.OnRemoteHandleLearned(attach.Handle);
                    _linksByIncomingHandle[attach.Handle] = attachLink;
                }
                attachLink.DispatchIncomingFrame(kind, body);
                break;

            case PerformativeKind.Detach:
                AmqpDetach.Read(body, out var detach, out _);
                AmqpLink? detachLink;
                lock (_linkLock)
                {
                    _linksByIncomingHandle.TryGetValue(detach.Handle, out detachLink);
                }
                detachLink?.DispatchIncomingFrame(kind, body);
                break;

            case PerformativeKind.Flow:
                AmqpFlow.Read(body, out var flow, out _);
                if (flow.Handle is { } flowHandle)
                {
                    AmqpLink? flowLink;
                    lock (_linkLock)
                    {
                        _linksByIncomingHandle.TryGetValue(flowHandle, out flowLink);
                    }
                    flowLink?.DispatchIncomingFrame(kind, body);
                }
                break;

            case PerformativeKind.Transfer:
                AmqpTransfer.Read(body, out var transfer, out _);
                AmqpLink? transferLink;
                lock (_linkLock)
                {
                    _linksByIncomingHandle.TryGetValue(transfer.Handle, out transferLink);
                }
                transferLink?.DispatchTransfer(transfer, body);
                break;

            case PerformativeKind.Disposition:
                AmqpDisposition.Read(body, out var disposition, out _);
                DispatchDisposition(disposition);
                break;

            default:
                // Unknown / unhandled performatives are ignored.
                break;
        }
    }

    private void DispatchDisposition(AmqpDisposition disposition)
    {
        // Sender-role disposition (from receiver) updates state for our
        // outgoing transfers. Fanned out to all outgoing-handle links,
        // each of which matches the disposition's delivery-id range.
        AmqpLink[] snapshot;
        lock (_linkLock)
        {
            snapshot = _linksByOutgoingHandle.Values.ToArray();
        }
        foreach (var link in snapshot)
        {
            link.DispatchDisposition(disposition);
        }
    }

    internal void OnRemoteChannelLearned(ushort remoteChannel) => RemoteChannel = remoteChannel;

    /// <summary>Forces the session to its final state when the parent connection is closing.</summary>
    internal void Abort(Exception? exception = null)
    {
        Interlocked.Exchange(ref _state, StateFinal);
        if (exception is null)
        {
            _peerBeginReceived.TrySetCanceled();
            _peerEndReceived.TrySetCanceled();
        }
        else
        {
            _peerBeginReceived.TrySetException(exception);
            _peerEndReceived.TrySetException(exception);
        }

        AmqpLink[] links;
        lock (_linkLock)
        {
            links = _linksByOutgoingHandle.Values.ToArray();
            _linksByOutgoingHandle.Clear();
            _linksByIncomingHandle.Clear();
            _linksByName.Clear();
        }
        foreach (var l in links) l.Abort(exception);
        _connection.UnregisterSession(this);
    }

    // ---- internals --------------------------------------------------------

    private void HandlePeerEnd(AmqpEnd end)
    {
        var prior = Interlocked.CompareExchange(ref _state, StateClosingRemote, StateOpened);
        _peerEndReceived.TrySetResult(end);

        if (prior == StateOpened)
        {
            // Peer initiated — mirror an end back on the same channel,
            // then transition to Final and unregister so any concurrent
            // CloseAsync sees the session is fully torn down.
            _ = Task.Run(async () =>
            {
                try { await SendEndAsync(error: null, CancellationToken.None).ConfigureAwait(false); }
                catch { /* best effort */ }
                finally
                {
                    Interlocked.Exchange(ref _state, StateFinal);
                    _connection.UnregisterSession(this);
                }
            });
        }
    }

    private async ValueTask SendEndAsync(AmqpError? error, CancellationToken ct)
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
            var endPerf = new AmqpEnd { Error = errorPayload };
            AmqpEnd.Write(rented, in endPerf, out var written);
            await _connection.WriteSessionFrameAsync(OutgoingChannel, rented.AsMemory(0, written), ct).ConfigureAwait(false);
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
