using System.Buffers;
using Aws2Azure.Amqp.Framing;
using Aws2Azure.Amqp.Transport;

namespace Aws2Azure.Amqp.Connection;

/// <summary>
/// AMQP 1.0 connection state machine on top of an
/// <see cref="IAmqpTransport"/>. Owns:
/// <list type="bullet">
///   <item><description>the <c>open</c>/<c>close</c> handshakes (§2.7);</description></item>
///   <item><description>the background read loop that drains the transport and routes frames;</description></item>
///   <item><description>the idle-timeout heartbeat (§2.4.5): send empty AMQP frames at half of the peer's advertised idle-time-out, and detect peer silence past twice our own advertised idle-time-out.</description></item>
/// </list>
/// All writes are serialised through a single <see cref="SemaphoreSlim"/>
/// so the heartbeat task can never interleave with a session write.
/// </summary>
internal sealed class AmqpConnection : IAsyncDisposable
{
    private const int StateClosed = 0;
    private const int StateOpening = 1;
    private const int StateOpened = 2;
    private const int StateClosingLocal = 3;
    private const int StateClosingRemote = 4;
    private const int StateFinal = 5;

    private readonly IAmqpTransport _transport;
    private readonly AmqpConnectionSettings _settings;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly TaskCompletionSource<AmqpClose> _peerCloseReceived =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly object _sessionLock = new();
    private readonly Dictionary<ushort, AmqpSession> _sessionsByOutgoingChannel = new();
    private readonly Dictionary<ushort, AmqpSession> _sessionsByIncomingChannel = new();
    private ushort _nextOutgoingChannel;

    private Task? _readLoopTask;
    private Task? _heartbeatTask;
    private int _state = StateClosed;
    private long _lastReceivedTicks;

    public AmqpConnection(IAmqpTransport transport, AmqpConnectionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(settings);
        _transport = transport;
        _settings = settings;
    }

    /// <summary>Peer's <c>open</c> performative, available after <see cref="OpenAsync"/>.</summary>
    public AmqpOpen RemoteOpen { get; private set; }

    /// <summary>Effective max-frame-size we may send (peer's advertised value, capped at uint.MaxValue).</summary>
    public uint EffectiveOutgoingMaxFrameSize { get; private set; }

    /// <summary>Effective idle-time-out we must honour towards the peer (their advertised value).</summary>
    public TimeSpan EffectiveOutgoingIdleTimeout { get; private set; }

    /// <summary>
    /// Performs the AMQP open handshake: send our <c>open</c>, read the
    /// peer's <c>open</c>, validate, then start the read loop and idle
    /// heartbeat. Throws <see cref="AmqpConnectionException"/> on any
    /// peer-side failure.
    /// </summary>
    public async Task OpenAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _state, StateOpening, StateClosed) != StateClosed)
            throw new InvalidOperationException($"Connection already opened or in state {_state}.");

        try
        {
            var localOpen = BuildLocalOpen();
            await SendPerformativeAsync<int>(localOpen, AmqpClose.Descriptor /* unused tag */, cancellationToken)
                .ConfigureAwait(false);

            // Read remote open before starting background loops.
            using (var frame = await AmqpFrameIO.ReadFrameAsync(_transport, (int)_settings.MaxFrameSize, cancellationToken).ConfigureAwait(false))
            {
                if (frame.Header.Type != AmqpFrameType.Amqp)
                    throw new AmqpConnectionException(
                        $"Expected AMQP frame for peer open, got type {frame.Header.Type}.", AmqpErrorKind.ClientFatal);

                if (frame.Body.IsEmpty)
                    throw new AmqpConnectionException(
                        "Peer sent empty AMQP frame in place of open.", AmqpErrorKind.ClientFatal);

                var kind = PerformativeCodec.PeekKind(frame.Body.Span, out _);
                if (kind == PerformativeKind.Close)
                {
                    AmqpClose.Read(frame.Body, out var earlyClose, out _);
                    var (peerKind, peerCondition) = ExtractCloseError(earlyClose);
                    throw new AmqpConnectionException(
                        $"Peer closed connection before open completed (condition={peerCondition ?? "<none>"}).",
                        peerKind) { PeerCondition = peerCondition };
                }
                if (kind != PerformativeKind.Open)
                    throw new AmqpConnectionException(
                        $"Expected open from peer, got {kind}.", AmqpErrorKind.ClientFatal);

                AmqpOpen.Read(frame.Body, out var remoteOpen, out _);
                RemoteOpen = remoteOpen;
                EffectiveOutgoingMaxFrameSize = remoteOpen.MaxFrameSize ?? uint.MaxValue;
                EffectiveOutgoingIdleTimeout = remoteOpen.IdleTimeoutMilliseconds is { } ms && ms > 0
                    ? TimeSpan.FromMilliseconds(ms)
                    : TimeSpan.Zero;
            }

            Volatile.Write(ref _lastReceivedTicks, Environment.TickCount64);
            Interlocked.Exchange(ref _state, StateOpened);
            _readLoopTask = Task.Run(() => ReadLoopAsync(_shutdownCts.Token));
            if (EffectiveOutgoingIdleTimeout > TimeSpan.Zero)
                _heartbeatTask = Task.Run(() => HeartbeatLoopAsync(_shutdownCts.Token));
        }
        catch
        {
            Interlocked.Exchange(ref _state, StateFinal);
            throw;
        }
    }

    /// <summary>
    /// Performs the close handshake (§2.7.5): send <c>close</c>, wait for
    /// peer <c>close</c> (or transport completion), then stop the read
    /// loop. Optionally surface a local <paramref name="error"/> in the
    /// outgoing close.
    /// </summary>
    public async Task CloseAsync(AmqpError? error = null, CancellationToken cancellationToken = default)
    {
        var prior = Interlocked.CompareExchange(ref _state, StateClosingLocal, StateOpened);
        if (prior == StateFinal || prior == StateClosed)
            return;
        if (prior == StateClosingLocal || prior == StateClosingRemote)
        {
            // Already closing — just await termination.
            await WaitForFinalAsync(cancellationToken).ConfigureAwait(false);
            return;
        }
        if (prior != StateOpened)
            throw new InvalidOperationException($"Cannot close from state {prior}.");

        try
        {
            ReadOnlyMemory<byte> errorPayload = ReadOnlyMemory<byte>.Empty;
            byte[]? rentedErr = null;
            if (error is { } e)
            {
                rentedErr = ArrayPool<byte>.Shared.Rent(Performatives.ScratchSize);
                AmqpError.Write(rentedErr, in e, out var errWritten);
                errorPayload = rentedErr.AsMemory(0, errWritten);
            }

            try
            {
                var close = new AmqpClose { Error = errorPayload };
                await SendCloseAsync(close, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (rentedErr is not null) ArrayPool<byte>.Shared.Return(rentedErr);
            }

            // Wait for peer close (or read loop to terminate the stream).
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(TimeSpan.FromSeconds(30));
            await using (linked.Token.Register(() => _peerCloseReceived.TrySetCanceled(linked.Token)))
            {
                try { await _peerCloseReceived.Task.ConfigureAwait(false); }
                catch (OperationCanceledException) { /* swallow timeout — we still tear down */ }
            }
        }
        finally
        {
            await ShutdownAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Allocates the next outgoing channel, registers a new
    /// <see cref="AmqpSession"/>, and performs the <c>begin</c>/<c>begin</c>
    /// handshake. Throws if no channels remain under
    /// <c>open.channel-max</c>.
    /// </summary>
    public async Task<AmqpSession> BeginSessionAsync(
        AmqpSessionSettings? settings = null, CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _state) != StateOpened)
            throw new InvalidOperationException("Connection is not open.");

        settings ??= new AmqpSessionSettings();

        AmqpSession session;
        lock (_sessionLock)
        {
            // §2.4.7: channel-max negotiation takes the minimum of the two
            // peers' advertised values. Default per spec is ushort.MaxValue.
            var localMax = _settings.ChannelMax;
            var peerMax = RemoteOpen.ChannelMax ?? ushort.MaxValue;
            var max = (ushort)Math.Min(localMax, peerMax);
            if (_sessionsByOutgoingChannel.Count > max)
                throw new InvalidOperationException("No channels available under negotiated channel-max.");

            ushort channel = _nextOutgoingChannel;
            while (_sessionsByOutgoingChannel.ContainsKey(channel))
            {
                if (channel == max)
                    throw new InvalidOperationException("No channels available under negotiated channel-max.");
                channel++;
            }
            session = new AmqpSession(this, channel, settings);
            _sessionsByOutgoingChannel.Add(channel, session);
            _nextOutgoingChannel = (ushort)(channel == max ? 0 : channel + 1);
        }

        try
        {
            await session.OpenAsync(cancellationToken).ConfigureAwait(false);
            return session;
        }
        catch
        {
            UnregisterSession(session);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await ShutdownAsync().ConfigureAwait(false);
        await _transport.DisposeAsync().ConfigureAwait(false);
        _shutdownCts.Dispose();
        _writeLock.Dispose();
    }

    // ---- internal helpers -------------------------------------------------

    private AmqpOpen BuildLocalOpen() => new()
    {
        ContainerId = _settings.ContainerId,
        Hostname = _settings.Hostname,
        MaxFrameSize = _settings.MaxFrameSize,
        ChannelMax = _settings.ChannelMax,
        IdleTimeoutMilliseconds = _settings.IdleTimeout > TimeSpan.Zero
            ? (uint?)_settings.IdleTimeout.TotalMilliseconds
            : null,
    };

    private async ValueTask SendPerformativeAsync<TUnused>(AmqpOpen open, ulong _tag, CancellationToken ct)
    {
        var rented = ArrayPool<byte>.Shared.Rent(Performatives.ScratchSize);
        try
        {
            AmqpOpen.Write(rented, in open, out var written);
            await WriteFrameLockedAsync(AmqpFrameType.Amqp, 0, rented.AsMemory(0, written), ct).ConfigureAwait(false);
        }
        finally { ArrayPool<byte>.Shared.Return(rented); }
    }

    private async ValueTask SendCloseAsync(AmqpClose close, CancellationToken ct)
    {
        var rented = ArrayPool<byte>.Shared.Rent(Performatives.ScratchSize);
        try
        {
            AmqpClose.Write(rented, in close, out var written);
            await WriteFrameLockedAsync(AmqpFrameType.Amqp, 0, rented.AsMemory(0, written), ct).ConfigureAwait(false);
        }
        finally { ArrayPool<byte>.Shared.Return(rented); }
    }

    private async ValueTask WriteFrameLockedAsync(
        AmqpFrameType type, ushort channel, ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Pre-open we use the spec-mandated initial limit (4 KiB);
            // post-open we honour the peer's advertised max-frame-size.
            var maxFrame = Volatile.Read(ref _state) == StateOpened
                ? (int)Math.Min(EffectiveOutgoingMaxFrameSize, int.MaxValue)
                : AmqpFrameIO.InitialMaxFrameSize;
            await AmqpFrameIO.WriteFrameAsync(_transport, type, channel, body, ct, maxFrame).ConfigureAwait(false);
        }
        finally { _writeLock.Release(); }
    }

    /// <summary>Internal hook used by <see cref="AmqpSession"/> to emit frames on its own channel.</summary>
    internal ValueTask WriteSessionFrameAsync(ushort channel, ReadOnlyMemory<byte> body, CancellationToken ct)
        => WriteFrameLockedAsync(AmqpFrameType.Amqp, channel, body, ct);

    /// <summary>
    /// Effective post-open max-frame-size for outbound writes. Returns
    /// <see cref="AmqpFrameIO.InitialMaxFrameSize"/> until <c>open</c>
    /// completes.
    /// </summary>
    internal int CurrentMaxFrameSize => Volatile.Read(ref _state) == StateOpened
        ? (int)Math.Min(EffectiveOutgoingMaxFrameSize, int.MaxValue)
        : AmqpFrameIO.InitialMaxFrameSize;

    /// <summary>
    /// Writes a sequence of frames atomically (under the connection write
    /// lock) so that multi-frame transfers (§2.6.14) cannot interleave
    /// with other frames on the same connection. Each segment is a
    /// pre-encoded <c>{performative ++ payload}</c> buffer for one frame.
    /// </summary>
    internal async ValueTask WriteSessionFramesAtomicAsync(
        ushort channel, IReadOnlyList<ReadOnlyMemory<byte>> segments, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var maxFrame = CurrentMaxFrameSize;
            for (int i = 0; i < segments.Count; i++)
            {
                await AmqpFrameIO.WriteFrameAsync(
                    _transport, AmqpFrameType.Amqp, channel, segments[i], ct, maxFrame).ConfigureAwait(false);
            }
        }
        finally { _writeLock.Release(); }
    }

    /// <summary>Internal hook used by <see cref="AmqpSession"/> on close/abort.</summary>
    internal void UnregisterSession(AmqpSession session)
    {
        lock (_sessionLock)
        {
            _sessionsByOutgoingChannel.Remove(session.OutgoingChannel);
            // RemoteChannel defaults to 0 before begin reply lands; only
            // remove if the mapping actually points at this session.
            if (_sessionsByIncomingChannel.TryGetValue(session.RemoteChannel, out var existing) && existing == session)
                _sessionsByIncomingChannel.Remove(session.RemoteChannel);
        }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                RentedFrame frame;
                try
                {
                    frame = await AmqpFrameIO.ReadFrameAsync(_transport, (int)_settings.MaxFrameSize, ct).ConfigureAwait(false);
                }
                catch (EndOfStreamException)
                {
                    _peerCloseReceived.TrySetException(
                        new AmqpConnectionException("Transport closed before peer sent close.", AmqpErrorKind.ServerFatal));
                    return;
                }

                using (frame)
                {
                    Volatile.Write(ref _lastReceivedTicks, Environment.TickCount64);

                    if (frame.Body.IsEmpty)
                        continue; // heartbeat

                    if (frame.Header.Type != AmqpFrameType.Amqp)
                        continue; // SASL post-handshake; ignore (shouldn't happen here).

                    var kind = PerformativeCodec.PeekKind(frame.Body.Span, out _);
                    if (kind == PerformativeKind.Close)
                    {
                        AmqpClose.Read(frame.Body, out var close, out _);
                        Interlocked.CompareExchange(ref _state, StateClosingRemote, StateOpened);
                        _peerCloseReceived.TrySetResult(close);

                        // Mirror the close if the peer initiated.
                        if (_state == StateClosingRemote)
                        {
                            try
                            {
                                var reply = new AmqpClose();
                                await SendCloseAsync(reply, CancellationToken.None).ConfigureAwait(false);
                            }
                            catch { /* peer might already be gone */ }
                        }
                        AbortAllSessions();
                        return;
                    }

                    DispatchSessionFrame(kind, frame.Header.Channel, frame.Body);
                }
            }
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
        catch (Exception ex)
        {
            _peerCloseReceived.TrySetException(
                new AmqpConnectionException("Read loop failed.", ex, AmqpErrorKind.ClientFatal));
        }
    }

    private void DispatchSessionFrame(PerformativeKind kind, ushort incomingChannel, ReadOnlyMemory<byte> body)
    {
        AmqpSession? session;
        if (kind == PerformativeKind.Begin)
        {
            // The peer's begin echoes our outgoing channel back as
            // remote-channel; we use that to bind the session before
            // delivering the frame.
            AmqpBegin.Read(body, out var begin, out _);
            if (begin.RemoteChannel is not { } ourChannel)
            {
                // Peer-initiated session (server begin first) — not used in
                // any flow we currently support; ignore for 5a.
                return;
            }
            lock (_sessionLock)
            {
                if (!_sessionsByOutgoingChannel.TryGetValue(ourChannel, out session))
                    return;
                session.OnRemoteChannelLearned(incomingChannel);
                _sessionsByIncomingChannel[incomingChannel] = session;
            }
            session.DispatchIncomingFrame(kind, body);
            return;
        }

        lock (_sessionLock)
        {
            _sessionsByIncomingChannel.TryGetValue(incomingChannel, out session);
        }
        session?.DispatchIncomingFrame(kind, body);
    }

    private void AbortAllSessions()
    {
        AmqpSession[] all;
        lock (_sessionLock)
        {
            all = _sessionsByOutgoingChannel.Values.ToArray();
            _sessionsByOutgoingChannel.Clear();
            _sessionsByIncomingChannel.Clear();
        }
        foreach (var s in all) s.Abort();
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        // §2.4.5: send no later than the peer's idle-time-out. We aim at
        // half to leave a safety margin.
        var period = TimeSpan.FromMilliseconds(EffectiveOutgoingIdleTimeout.TotalMilliseconds / 2);
        if (period <= TimeSpan.Zero) return;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(period, ct).ConfigureAwait(false);
                if (Volatile.Read(ref _state) != StateOpened) return;
                await WriteFrameLockedAsync(AmqpFrameType.Amqp, 0, ReadOnlyMemory<byte>.Empty, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
        catch { /* read loop will surface the failure */ }
    }

    private async Task ShutdownAsync()
    {
        if (Interlocked.Exchange(ref _state, StateFinal) == StateFinal)
            return;
        try { _shutdownCts.Cancel(); } catch { }

        if (_heartbeatTask is { } hb)
        {
            try { await hb.ConfigureAwait(false); } catch { }
        }
        if (_readLoopTask is { } rl)
        {
            try { await rl.ConfigureAwait(false); } catch { }
        }
        AbortAllSessions();
    }

    private async Task WaitForFinalAsync(CancellationToken ct)
    {
        while (Volatile.Read(ref _state) != StateFinal)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(20, ct).ConfigureAwait(false);
        }
    }

    private static (AmqpErrorKind kind, string? condition) ExtractCloseError(AmqpClose close)
    {
        if (close.Error.IsEmpty) return (AmqpErrorKind.Unknown, null);
        try
        {
            AmqpError.Read(close.Error, out var err, out _);
            return (AmqpErrorClassifier.Classify(err.Condition), err.Condition);
        }
        catch
        {
            return (AmqpErrorKind.Unknown, null);
        }
    }
}
