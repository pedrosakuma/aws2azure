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
/// For Slice 5a the session is intentionally minimal: it manages
/// lifecycle only. Link routing (attach/flow/transfer/disposition/detach)
/// lands in Slice 5b on top of the dispatch hook
/// <see cref="DispatchIncomingFrame"/> added here.
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

    private int _state = StateClosed;

    internal AmqpSession(AmqpConnection connection, ushort outgoingChannel, AmqpSessionSettings settings)
    {
        _connection = connection;
        OutgoingChannel = outgoingChannel;
        _settings = settings;
    }

    /// <summary>Our local channel number (the one peer sees as remote-channel).</summary>
    public ushort OutgoingChannel { get; }

    /// <summary>Peer's local channel number, learned from their <c>begin</c> reply.</summary>
    public ushort RemoteChannel { get; private set; }

    /// <summary>Peer's <c>begin</c> performative, available after <see cref="OpenAsync"/>.</summary>
    public AmqpBegin RemoteBegin { get; private set; }

    /// <summary>True once the session has reached its terminal state.</summary>
    public bool IsClosed => Volatile.Read(ref _state) >= StateClosingLocal;

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
    /// Handles a frame received on this session's incoming channel. Slice
    /// 5a routes only begin/end; later slices extend the switch.
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

            // Future slices add: Attach, Flow, Transfer, Disposition, Detach.
            default:
                break;
        }
    }

    internal void OnRemoteChannelLearned(ushort remoteChannel) => RemoteChannel = remoteChannel;

    /// <summary>Forces the session to its final state when the parent connection is closing.</summary>
    internal void Abort()
    {
        Interlocked.Exchange(ref _state, StateFinal);
        _peerBeginReceived.TrySetCanceled();
        _peerEndReceived.TrySetCanceled();
    }

    // ---- internals --------------------------------------------------------

    private void HandlePeerEnd(AmqpEnd end)
    {
        var prior = Interlocked.CompareExchange(ref _state, StateClosingRemote, StateOpened);
        _peerEndReceived.TrySetResult(end);

        if (prior == StateOpened)
        {
            // Peer initiated — mirror an end back on the same channel.
            try
            {
                _ = SendEndAsync(error: null, CancellationToken.None);
            }
            catch { /* best effort */ }
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
