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

    internal void Abort()
    {
        Interlocked.Exchange(ref _state, StateFinal);
        _peerAttachReceived.TrySetCanceled();
        _peerDetachReceived.TrySetCanceled();
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
