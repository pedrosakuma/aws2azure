using System.Collections.Concurrent;
using System.Linq;
using Aws2Azure.Amqp.Connection;
using Aws2Azure.Amqp.Security;

namespace Aws2Azure.Amqp.ServiceBus;

/// <summary>
/// Lazy, thread-safe pool of Service Bus AMQP connections and receiver
/// links. Conceptually a two-level cache:
/// <list type="bullet">
///   <item>Level 1: one
///   <see cref="ServiceBusAmqpConnection"/> per
///   <see cref="ServiceBusAmqpConnectionKey"/> (namespace + SAS key
///   name). All queues under the same key share the connection and
///   data session.</item>
///   <item>Level 2: one <see cref="ServiceBusReceiver"/> per
///   <c>(connection-key, queueName)</c>. The link is opened on first
///   request and reused across handler invocations.</item>
/// </list>
/// <para>
/// Concurrency: many callers may safely call
/// <see cref="GetReceiverAsync"/> concurrently; the first caller for a
/// given key takes a per-slot semaphore and creates the connection /
/// receiver while everyone else waits, then reuses the cached
/// instance.
/// </para>
/// <para>
/// Failure semantics: the pool does <b>not</b> detect a half-dead
/// connection or auto-reconnect. When a handler observes a
/// link/connection-level failure it must call
/// <see cref="InvalidateReceiverAsync"/> (or the sender / management /
/// session-receiver variant) to evict the entry; the next
/// <see cref="GetReceiverAsync"/> call rebuilds from scratch. The SQS
/// receive handler already follows this contract on receive failures.
/// Active reconnection is intentionally deferred.
/// </para>
/// </summary>
internal sealed class ServiceBusAmqpPool : IAsyncDisposable
{
    /// <summary>
    /// Default idle window after which an unused FIFO session-receiver
    /// link is torn down by the background sweeper (#262). Comfortably
    /// larger than a typical queue LockDuration / visibility timeout so
    /// an in-flight message's settle window has elapsed before the link
    /// is evicted — eviction then merely returns the broker session to
    /// the broker for another consumer (scale-up rebalance), matching
    /// SQS visibility-timeout semantics.
    /// </summary>
    public static readonly TimeSpan DefaultSessionReceiverIdleTimeout = TimeSpan.FromMinutes(5);

    /// <summary>Default cadence of the idle session-receiver sweep.</summary>
    public static readonly TimeSpan DefaultSweepInterval = TimeSpan.FromMinutes(1);

    private readonly IServiceBusAmqpConnectionFactory _factory;
    private readonly ConcurrentDictionary<ServiceBusAmqpConnectionKey, ConnectionSlot> _connections
        = new();
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan? _sessionReceiverIdleTimeout;
    private readonly ITimer? _sweepTimer;
    private int _sweepRunning;
    private int _disposed;

    public ServiceBusAmqpPool(IServiceBusAmqpConnectionFactory factory)
        : this(factory, sessionReceiverIdleTimeout: null, sweepInterval: null, timeProvider: null)
    {
    }

    /// <summary>
    /// Creates a pool with proactive idle-TTL eviction of FIFO
    /// session-receiver links (#262). When
    /// <paramref name="sessionReceiverIdleTimeout"/> is non-null a
    /// background timer fires every <paramref name="sweepInterval"/>
    /// and closes session receivers that have had no receive/settle
    /// activity within the idle window — detaching the AMQP link and
    /// returning the Service Bus session to the broker while leaving the
    /// shared connection warm. When it is <c>null</c> no sweeper runs
    /// and session receivers are only evicted on link failure
    /// (<see cref="InvalidateSessionReceiverAsync"/>) or pool disposal.
    /// </summary>
    public ServiceBusAmqpPool(
        IServiceBusAmqpConnectionFactory factory,
        TimeSpan? sessionReceiverIdleTimeout,
        TimeSpan? sweepInterval = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        if (sessionReceiverIdleTimeout is { } idle && idle <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(sessionReceiverIdleTimeout));
        _factory = factory;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _sessionReceiverIdleTimeout = sessionReceiverIdleTimeout;

        if (sessionReceiverIdleTimeout is not null)
        {
            var period = sweepInterval ?? DefaultSweepInterval;
            if (period <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(sweepInterval));
            _sweepTimer = _timeProvider.CreateTimer(
                static state => ((ServiceBusAmqpPool)state!).OnSweepTimer(),
                this,
                period,
                period);
        }
    }

    /// <summary>
    /// Returns the shared <see cref="ServiceBusReceiver"/> for the
    /// given (namespace, key, queue) tuple, creating the connection
    /// and the receiver link on first use. The returned receiver is
    /// owned by the pool — callers must <b>not</b> dispose it
    /// directly; call <see cref="InvalidateReceiverAsync"/> instead when a
    /// failure occurs.
    /// </summary>
    public async Task<ServiceBusReceiver> GetReceiverAsync(
        ServiceBusAmqpEndpoint endpoint,
        string sasKeyName,
        string sasKey,
        string queueName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sasKeyName);
        ArgumentException.ThrowIfNullOrWhiteSpace(sasKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(queueName);
        ThrowIfDisposed();

        var key = new ServiceBusAmqpConnectionKey(endpoint, sasKeyName);
        while (true)
        {
            var slot = await GetOrCreateConnectionSlotAsync(key).ConfigureAwait(false);
            try
            {
                var connection = await slot.GetOrCreateConnectionAsync(
                    _factory, key, sasKey, cancellationToken).ConfigureAwait(false);
                var receiver = await slot.GetOrCreateReceiverAsync(
                    connection, key, queueName, cancellationToken).ConfigureAwait(false);
                // Pool may have been disposed while we were creating. If so,
                // the slot will be torn down in DisposeAsync if it's still in
                // the dict, or already torn down if not. Either way, surface
                // the disposal to the caller instead of returning a doomed
                // receiver.
                ThrowIfDisposed();
                return receiver;
            }
            catch (ObjectDisposedException) when (Volatile.Read(ref _disposed) == 0)
            {
                // Slot was invalidated concurrently — retry with a fresh slot.
                _connections.TryRemove(KeyValuePair.Create(key, slot));
                continue;
            }
        }
    }

    /// <summary>
    /// Returns the shared session-bound <see cref="ServiceBusReceiver"/>
    /// for the (namespace, key, queue, session-id) tuple, opening the
    /// connection and a session-filtered receiver link on first use.
    /// The returned receiver is owned by the pool — callers must
    /// <b>not</b> dispose it directly; call
    /// <see cref="InvalidateSessionReceiverAsync"/> instead when a
    /// failure occurs.
    /// <para>
    /// <paramref name="sessionId"/> is required: the pool is only
    /// useful when the cache key is stable, so the "ask the broker
    /// for any available session" mode is intentionally not exposed
    /// here. Callers needing that should go through
    /// <see cref="ServiceBusAmqpConnection.OpenSessionReceiverAsync"/>
    /// directly and either own the lifecycle or hand the resulting
    /// receiver back to the pool keyed by its resolved
    /// <see cref="ServiceBusReceiver.SessionId"/> via
    /// <see cref="AcquireBrokerAssignedSessionReceiverAsync"/>.
    /// </para>
    /// </summary>
    public async Task<ServiceBusReceiver> GetSessionReceiverAsync(
        ServiceBusAmqpEndpoint endpoint,
        string sasKeyName,
        string sasKey,
        string queueName,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sasKeyName);
        ArgumentException.ThrowIfNullOrWhiteSpace(sasKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(queueName);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ThrowIfDisposed();

        var key = new ServiceBusAmqpConnectionKey(endpoint, sasKeyName);
        while (true)
        {
            var slot = await GetOrCreateConnectionSlotAsync(key).ConfigureAwait(false);
            try
            {
                var connection = await slot.GetOrCreateConnectionAsync(
                    _factory, key, sasKey, cancellationToken).ConfigureAwait(false);
                var receiver = await slot.GetOrCreateSessionReceiverAsync(
                    connection, key, queueName, sessionId, cancellationToken).ConfigureAwait(false);
                ThrowIfDisposed();
                return receiver;
            }
            catch (ObjectDisposedException) when (Volatile.Read(ref _disposed) == 0)
            {
                _connections.TryRemove(KeyValuePair.Create(key, slot));
                continue;
            }
        }
    }

    /// <summary>
    /// Opens a <b>broker-assigned</b> session receiver for the given
    /// queue: passes <c>sessionId: null</c> to
    /// <see cref="ServiceBusAmqpConnection.OpenSessionReceiverAsync"/>
    /// so Service Bus picks any currently-available session, then
    /// adopts the resulting receiver into the pool keyed by the
    /// resolved <see cref="ServiceBusReceiver.SessionId"/>.
    /// <para>
    /// This is the entry point the SQS FIFO <c>ReceiveMessage</c> path
    /// uses: AWS clients hit FIFO queues without knowing the
    /// MessageGroupId/SessionId in advance, so the broker is the only
    /// party that can pick one. After the first acquire, subsequent
    /// settle requests (DeleteMessage / ChangeMessageVisibility)
    /// route back to the same session receiver via the cached slot,
    /// which is essential for SB session-bound disposition.
    /// </para>
    /// <para>
    /// Race: if a concurrent caller already adopted a receiver for the
    /// same resolved session-id, the freshly-opened receiver is
    /// disposed and the cached one is returned. The receiver returned
    /// is owned by the pool; callers must not dispose it directly —
    /// use <see cref="InvalidateSessionReceiverAsync"/> instead.
    /// </para>
    /// </summary>
    public async Task<ServiceBusReceiver> AcquireBrokerAssignedSessionReceiverAsync(
        ServiceBusAmqpEndpoint endpoint,
        string sasKeyName,
        string sasKey,
        string queueName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sasKeyName);
        ArgumentException.ThrowIfNullOrWhiteSpace(sasKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(queueName);
        ThrowIfDisposed();

        var key = new ServiceBusAmqpConnectionKey(endpoint, sasKeyName);
        while (true)
        {
            var slot = await GetOrCreateConnectionSlotAsync(key).ConfigureAwait(false);
            try
            {
                var connection = await slot.GetOrCreateConnectionAsync(
                    _factory, key, sasKey, cancellationToken).ConfigureAwait(false);
                var receiver = await slot.AcquireBrokerAssignedSessionReceiverAsync(
                    connection, key, queueName, cancellationToken).ConfigureAwait(false);
                ThrowIfDisposed();
                return receiver;
            }
            catch (ObjectDisposedException) when (Volatile.Read(ref _disposed) == 0)
            {
                _connections.TryRemove(KeyValuePair.Create(key, slot));
                continue;
            }
        }
    }

    /// <summary>
    /// Evicts the cached session receiver for
    /// (namespace, key, queue, session-id). The receiver link is
    /// detached and the connection stays warm so other session and
    /// non-session receivers under the same key keep working. Next
    /// <see cref="GetSessionReceiverAsync"/> call for the same session
    /// rebuilds the link.
    /// </summary>
    public async Task InvalidateSessionReceiverAsync(
        ServiceBusAmqpEndpoint endpoint,
        string sasKeyName,
        string queueName,
        string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sasKeyName);
        ArgumentException.ThrowIfNullOrWhiteSpace(queueName);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (Volatile.Read(ref _disposed) != 0) return;

        var key = new ServiceBusAmqpConnectionKey(endpoint, sasKeyName);
        if (!_connections.TryGetValue(key, out var slot)) return;
        await slot.InvalidateSessionReceiverAsync(queueName, sessionId).ConfigureAwait(false);
    }

    /// <summary>
    /// Looks up the cached session receiver for (namespace, key, queue,
    /// session-id) without opening a fresh session lock. Returns
    /// <c>null</c> when the connection or the session slot have not
    /// been materialised yet. Used by settle paths (DeleteMessage,
    /// CMV=0) that must not acquire a new session lock — opening one
    /// just to fail the lock-token lookup would starve the
    /// MessageGroupId until the session lock expires.
    /// <para>
    /// A successful lookup also refreshes the session slot's idle
    /// timestamp. Callers that renew a session lock through the
    /// management link use this side-effect to keep the receiver slot
    /// alive while / after the renewal completes.
    /// </para>
    /// </summary>
    public ServiceBusReceiver? TryGetExistingSessionReceiver(
        ServiceBusAmqpEndpoint endpoint,
        string sasKeyName,
        string queueName,
        string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sasKeyName);
        ArgumentException.ThrowIfNullOrWhiteSpace(queueName);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (Volatile.Read(ref _disposed) != 0) return null;

        var key = new ServiceBusAmqpConnectionKey(endpoint, sasKeyName);
        if (!_connections.TryGetValue(key, out var slot)) return null;
        return slot.TryGetExistingSessionReceiver(queueName, sessionId);
    }

    /// <summary>
    /// Returns the shared management client for the (namespace, key,
    /// queue) tuple, opening the connection and the CBS-authorised
    /// <c>$management</c> request-response link on first use. Owned by
    /// the pool — callers must <b>not</b> dispose it; use
    /// <see cref="InvalidateManagementClientAsync"/> after a link
    /// failure.
    /// </summary>
    public async Task<ServiceBusManagementClient> GetManagementClientAsync(
        ServiceBusAmqpEndpoint endpoint,
        string sasKeyName,
        string sasKey,
        string queueName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sasKeyName);
        ArgumentException.ThrowIfNullOrWhiteSpace(sasKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(queueName);
        ThrowIfDisposed();

        var key = new ServiceBusAmqpConnectionKey(endpoint, sasKeyName);
        while (true)
        {
            var slot = await GetOrCreateConnectionSlotAsync(key).ConfigureAwait(false);
            try
            {
                var connection = await slot.GetOrCreateConnectionAsync(
                    _factory, key, sasKey, cancellationToken).ConfigureAwait(false);
                var client = await slot.GetOrCreateManagementClientAsync(
                    connection, key, queueName, cancellationToken).ConfigureAwait(false);
                ThrowIfDisposed();
                return client;
            }
            catch (ObjectDisposedException) when (Volatile.Read(ref _disposed) == 0)
            {
                _connections.TryRemove(KeyValuePair.Create(key, slot));
                continue;
            }
        }
    }

    /// <summary>
    /// Evicts the receiver for <paramref name="queueName"/> under
    /// (namespace, key). When <paramref name="closeConnection"/> is
    /// <c>true</c> (the default when the failure looks
    /// connection-scoped), the whole connection slot is dropped so a
    /// subsequent call rebuilds it from scratch; otherwise only the
    /// receiver is detached and the connection stays warm.
    /// </summary>
    public async Task InvalidateReceiverAsync(
        ServiceBusAmqpEndpoint endpoint,
        string sasKeyName,
        string queueName,
        bool closeConnection = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sasKeyName);
        ArgumentException.ThrowIfNullOrWhiteSpace(queueName);
        if (Volatile.Read(ref _disposed) != 0) return;

        var key = new ServiceBusAmqpConnectionKey(endpoint, sasKeyName);
        if (!_connections.TryGetValue(key, out var slot)) return;

        if (closeConnection)
        {
            if (_connections.TryRemove(key, out var removed))
                await removed.DisposeAsync().ConfigureAwait(false);
        }
        else
        {
            await slot.InvalidateReceiverAsync(queueName).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Returns the shared <see cref="ServiceBusAmqpSender"/> for the
    /// given (namespace, key, queue) tuple, creating the connection and
    /// the sender link on first use. The returned sender is owned by
    /// the pool — callers must <b>not</b> dispose it directly; call
    /// <see cref="InvalidateSenderAsync"/> instead when a failure
    /// occurs.
    /// </summary>
    public async Task<ServiceBusAmqpSender> GetSenderAsync(
        ServiceBusAmqpEndpoint endpoint,
        string sasKeyName,
        string sasKey,
        string queueName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sasKeyName);
        ArgumentException.ThrowIfNullOrWhiteSpace(sasKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(queueName);
        ThrowIfDisposed();

        var key = new ServiceBusAmqpConnectionKey(endpoint, sasKeyName);
        while (true)
        {
            var slot = await GetOrCreateConnectionSlotAsync(key).ConfigureAwait(false);
            try
            {
                var connection = await slot.GetOrCreateConnectionAsync(
                    _factory, key, sasKey, cancellationToken).ConfigureAwait(false);
                var sender = await slot.GetOrCreateSenderAsync(
                    connection, key, queueName, cancellationToken).ConfigureAwait(false);
                ThrowIfDisposed();
                return sender;
            }
            catch (ObjectDisposedException) when (Volatile.Read(ref _disposed) == 0)
            {
                _connections.TryRemove(KeyValuePair.Create(key, slot));
                continue;
            }
        }
    }

    /// <summary>
    /// Returns a shared sender link for callers that provide their own CBS
    /// token provider (for example OAuth-backed Service Bus Topics). The
    /// <paramref name="credentialMarker"/> participates in the connection
    /// pool key but is never sent to the broker.
    /// </summary>
    public async Task<ServiceBusAmqpSender> GetSenderAsync(
        ServiceBusAmqpEndpoint endpoint,
        string credentialMarker,
        IAmqpTokenProvider tokenProvider,
        string entityName,
        string audience,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialMarker);
        ArgumentNullException.ThrowIfNull(tokenProvider);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityName);
        ArgumentException.ThrowIfNullOrWhiteSpace(audience);
        ThrowIfDisposed();

        var key = new ServiceBusAmqpConnectionKey(endpoint, credentialMarker);
        while (true)
        {
            var slot = await GetOrCreateConnectionSlotAsync(key).ConfigureAwait(false);
            try
            {
                var connection = await slot.GetOrCreateConnectionAsync(
                    _factory, key, tokenProvider, cancellationToken).ConfigureAwait(false);
                var sender = await slot.GetOrCreateSenderAsync(
                    connection, key, entityName, audience, cancellationToken).ConfigureAwait(false);
                ThrowIfDisposed();
                return sender;
            }
            catch (ObjectDisposedException) when (Volatile.Read(ref _disposed) == 0)
            {
                _connections.TryRemove(KeyValuePair.Create(key, slot));
                continue;
            }
        }
    }

    /// <summary>
    /// Evicts the cached sender for (namespace, key, queue). When
    /// <paramref name="closeConnection"/> is <c>true</c> the whole
    /// connection slot is dropped (mirrors
    /// <see cref="InvalidateReceiverAsync"/>); otherwise only the sender link
    /// is detached and the connection stays warm.
    /// </summary>
    public async Task InvalidateSenderAsync(
        ServiceBusAmqpEndpoint endpoint,
        string sasKeyName,
        string queueName,
        bool closeConnection = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sasKeyName);
        ArgumentException.ThrowIfNullOrWhiteSpace(queueName);
        if (Volatile.Read(ref _disposed) != 0) return;

        var key = new ServiceBusAmqpConnectionKey(endpoint, sasKeyName);
        if (!_connections.TryGetValue(key, out var slot)) return;

        if (closeConnection)
        {
            if (_connections.TryRemove(key, out var removed))
                await removed.DisposeAsync().ConfigureAwait(false);
        }
        else
        {
            await slot.InvalidateSenderAsync(queueName).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Evicts the cached management client for (namespace, key, queue)
    /// without touching the receiver or connection. Next call to
    /// <see cref="GetManagementClientAsync"/> rebuilds it.
    /// </summary>
    public async Task InvalidateManagementClientAsync(
        ServiceBusAmqpEndpoint endpoint,
        string sasKeyName,
        string queueName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sasKeyName);
        ArgumentException.ThrowIfNullOrWhiteSpace(queueName);
        if (Volatile.Read(ref _disposed) != 0) return;

        var key = new ServiceBusAmqpConnectionKey(endpoint, sasKeyName);
        if (!_connections.TryGetValue(key, out var slot)) return;
        await slot.InvalidateManagementClientAsync(queueName).ConfigureAwait(false);
    }

    /// <summary>Number of cached connections; useful in tests.</summary>
    public int ConnectionCount => _connections.Count;

    /// <summary>
    /// Total cached FIFO session-receiver links across every connection.
    /// Diagnostics / test surface for the idle-TTL sweeper (#262).
    /// </summary>
    public int SessionReceiverCount
    {
        get
        {
            var total = 0;
            foreach (var slot in _connections.Values)
                total += slot.SessionReceiverCount;
            return total;
        }
    }

    /// <summary>
    /// Closes FIFO session-receiver links idle longer than the idle
    /// timeout the pool was created with (#262), detaching each link and
    /// returning its Service Bus session to the broker while leaving the
    /// shared connection warm. Returns the number of links evicted, or
    /// <c>0</c> when no idle timeout is configured or the pool is
    /// disposed. Best-effort: a link that turns active between the
    /// idle-check and the eviction simply has its activity timestamp
    /// reset and is skipped on the next sweep. Settle for an in-flight
    /// message whose link is evicted fails and is treated as a stale
    /// handle by the SQS handler (the broker redelivers the group on
    /// session-lock expiry — SQS visibility-timeout semantics).
    /// </summary>
    internal async Task<int> SweepIdleSessionReceiversAsync(CancellationToken cancellationToken = default)
    {
        if (_sessionReceiverIdleTimeout is not { } idle) return 0;
        if (Volatile.Read(ref _disposed) != 0) return 0;
        var now = _timeProvider.GetUtcNow();
        var evicted = 0;
        foreach (var slot in _connections.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            evicted += await slot.SweepIdleSessionReceiversAsync(now, idle).ConfigureAwait(false);
        }
        return evicted;
    }

    private void OnSweepTimer()
    {
        // Skip overlapping sweeps: a long sweep (many connections) must
        // not stack up behind a fast timer period.
        if (Interlocked.Exchange(ref _sweepRunning, 1) != 0) return;
        _ = SweepFromTimerAsync();
    }

    private async Task SweepFromTimerAsync()
    {
        try
        {
            await SweepIdleSessionReceiversAsync().ConfigureAwait(false);
        }
        catch
        {
            // Best-effort hygiene sweep — never surface to the timer
            // thread. Stale links that survive get retried next tick or
            // fail closed on next use (lazy InvalidateSessionReceiverAsync).
        }
        finally
        {
            Volatile.Write(ref _sweepRunning, 0);
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(ServiceBusAmqpPool));
    }

    /// <summary>
    /// Publishes a fresh <see cref="ConnectionSlot"/> for
    /// <paramref name="key"/> if none exists. Guards against the
    /// add-after-snapshot dispose race on both sides: the
    /// <see cref="ConcurrentDictionary{TKey,TValue}.TryGetValue"/>
    /// fast path re-checks <c>_disposed</c> so a concurrent caller can
    /// never observe a slot that has been (or is about to be)
    /// reclaimed by <see cref="DisposeAsync"/>; and the publish path
    /// awaits the cleanup <see cref="IAsyncDisposable.DisposeAsync"/>
    /// so the pool's own <c>DisposeAsync</c> cannot return before
    /// every connection it spawned has been disposed.
    /// </summary>
    private async ValueTask<ConnectionSlot> GetOrCreateConnectionSlotAsync(ServiceBusAmqpConnectionKey key)
    {
        if (_connections.TryGetValue(key, out var existing))
        {
            // If we observed a slot but the pool flipped to disposed
            // in between, refuse it: another thread may have published
            // it after a concurrent dispose snapshot, and we don't
            // want to let work pile up on a slot that's racing toward
            // cleanup.
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(ServiceBusAmqpPool));
            return existing;
        }
        var fresh = new ConnectionSlot(_timeProvider);
        var slot = _connections.GetOrAdd(key, fresh);
        if (ReferenceEquals(slot, fresh) && Volatile.Read(ref _disposed) != 0)
        {
            // We published into a disposed pool. Await cleanup so the
            // pool's DisposeAsync can rely on us not leaving any
            // outstanding work — if a parallel caller managed to fast-
            // path into this slot before the TryGetValue re-check
            // above was added, the slot may now own a real connection
            // whose disposal must complete synchronously with ours.
            _connections.TryRemove(KeyValuePair.Create(key, slot));
            await slot.DisposeAsync().ConfigureAwait(false);
            throw new ObjectDisposedException(nameof(ServiceBusAmqpPool));
        }
        return slot;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        // Stop the idle-TTL sweeper before draining connections so it
        // can't race the eviction loop below (#262).
        if (_sweepTimer is not null)
            await _sweepTimer.DisposeAsync().ConfigureAwait(false);
        // Drain-loop: each GetOrCreateConnectionSlot call site checks
        // _disposed after publishing, so once we've set the flag any
        // concurrent publish either (a) sees the flag and self-cleans
        // before this loop sees it, or (b) lands in the dict before
        // self-cleaning, in which case this loop catches it. Iterating
        // until empty closes the remaining gap where a publisher
        // hasn't reached its disposed-check yet at our first snapshot.
        while (!_connections.IsEmpty)
        {
            foreach (var entry in _connections.ToArray())
            {
                if (_connections.TryRemove(entry))
                    await entry.Value.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private sealed class ConnectionSlot : IAsyncDisposable
    {
        private readonly TimeProvider _timeProvider;
        private readonly SemaphoreSlim _connectionLock = new(1, 1);
        private readonly ConcurrentDictionary<string, ResourceSlot<ServiceBusReceiver>> _receivers
            = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, ResourceSlot<ServiceBusAmqpSender>> _senders
            = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<SessionReceiverKey, SessionReceiverSlot> _sessionReceivers
            = new();
        private readonly ConcurrentDictionary<string, ResourceSlot<ServiceBusManagementClient>> _managementClients
            = new(StringComparer.OrdinalIgnoreCase);
        private ServiceBusAmqpConnection? _connection;
        private int _disposed;

        public ConnectionSlot(TimeProvider timeProvider) => _timeProvider = timeProvider;

        public async Task<ServiceBusAmqpConnection> GetOrCreateConnectionAsync(
            IServiceBusAmqpConnectionFactory factory,
            ServiceBusAmqpConnectionKey key,
            string sasKey,
            CancellationToken cancellationToken)
            => await GetOrCreateConnectionAsync(
                    static (state, ct) => state.Factory.CreateAsync(state.Key.Endpoint, state.Key.SasKeyName, state.SasKey, ct),
                    (Factory: factory, Key: key, SasKey: sasKey),
                    cancellationToken)
                .ConfigureAwait(false);

        public async Task<ServiceBusAmqpConnection> GetOrCreateConnectionAsync(
            IServiceBusAmqpConnectionFactory factory,
            ServiceBusAmqpConnectionKey key,
            IAmqpTokenProvider tokenProvider,
            CancellationToken cancellationToken)
            => await GetOrCreateConnectionAsync(
                    static (state, ct) => state.Factory.CreateAsync(state.Key.Endpoint, state.Key.SasKeyName, state.TokenProvider, ct),
                    (Factory: factory, Key: key, TokenProvider: tokenProvider),
                    cancellationToken)
                .ConfigureAwait(false);

        private async Task<ServiceBusAmqpConnection> GetOrCreateConnectionAsync<TState>(
            Func<TState, CancellationToken, Task<ServiceBusAmqpConnection>> createAsync,
            TState state,
            CancellationToken cancellationToken)
        {
            var existing = Volatile.Read(ref _connection);
            if (existing is not null && !existing.IsClosed) return existing;
            ThrowIfDisposed();

            await _connectionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                existing = _connection;
                if (existing is not null && !existing.IsClosed) return existing;

                // Stale connection (peer closed it / network reset /
                // idle-time-out) — tear down every dependent slot before
                // dialling a fresh connection. Receivers / senders /
                // management / session links cached against the old
                // connection are no longer usable; leaving them in place
                // would surface as "Session is not open" / "Link is not
                // attached" on the next call.
                if (existing is not null)
                {
                    await EvictAllSlotsAsync().ConfigureAwait(false);
                    try { await existing.DisposeAsync().ConfigureAwait(false); }
                    catch { /* swallow during eviction */ }
                    Volatile.Write(ref _connection, null);
                }

                var created = await createAsync(state, cancellationToken).ConfigureAwait(false);
                Volatile.Write(ref _connection, created);
                return created;
            }
            finally
            {
                _connectionLock.Release();
            }
        }

        private async Task EvictAllSlotsAsync()
        {
            await EvictSlotsBestEffortAsync(_receivers).ConfigureAwait(false);
            await EvictSlotsBestEffortAsync(_senders).ConfigureAwait(false);
            await EvictSlotsBestEffortAsync(_sessionReceivers).ConfigureAwait(false);
            await EvictSlotsBestEffortAsync(_managementClients).ConfigureAwait(false);
        }

        public async Task<ServiceBusReceiver> GetOrCreateReceiverAsync(
            ServiceBusAmqpConnection connection,
            ServiceBusAmqpConnectionKey key,
            string queueName,
            CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            var slot = await GetOrPublishSlotAsync(_receivers, queueName, CreateReceiverSlot).ConfigureAwait(false);
            return await slot
                .GetOrCreateAsync(
                    new ReceiverOpenRequest(connection, key.Endpoint, queueName),
                    OpenReceiverResourceAsync,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        public async Task InvalidateReceiverAsync(string queueName)
        {
            if (_receivers.TryRemove(queueName, out var slot))
                await slot.DisposeAsync().ConfigureAwait(false);
        }

        public async Task<ServiceBusAmqpSender> GetOrCreateSenderAsync(
            ServiceBusAmqpConnection connection,
            ServiceBusAmqpConnectionKey key,
            string queueName,
            CancellationToken cancellationToken)
        {
            var audience = ServiceBusEndpoint.BuildQueueAudience(key.NamespaceFqdn, queueName);
            return await GetOrCreateSenderAsync(connection, key, queueName, audience, cancellationToken)
                .ConfigureAwait(false);
        }

        public async Task<ServiceBusAmqpSender> GetOrCreateSenderAsync(
            ServiceBusAmqpConnection connection,
            ServiceBusAmqpConnectionKey key,
            string entityName,
            string audience,
            CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            var slot = await GetOrPublishSlotAsync(_senders, entityName, CreateSenderSlot).ConfigureAwait(false);
            return await slot
                .GetOrCreateAsync(
                    new SenderOpenRequest(connection, entityName, audience),
                    OpenSenderResourceAsync,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        public async Task InvalidateSenderAsync(string queueName)
        {
            if (_senders.TryRemove(queueName, out var slot))
                await slot.DisposeAsync().ConfigureAwait(false);
        }

        public async Task<ServiceBusReceiver> GetOrCreateSessionReceiverAsync(
            ServiceBusAmqpConnection connection,
            ServiceBusAmqpConnectionKey key,
            string queueName,
            string sessionId,
            CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            var slotKey = new SessionReceiverKey(queueName, sessionId);
            while (true)
            {
                var slot = await GetOrPublishSlotAsync(_sessionReceivers, slotKey, CreateSessionReceiverSlot)
                    .ConfigureAwait(false);
                try
                {
                    var receiver = await slot
                        .GetOrCreateAsync(connection, key.Endpoint, queueName, sessionId, cancellationToken)
                        .ConfigureAwait(false);
                    slot.Touch(_timeProvider.GetUtcNow());
                    return receiver;
                }
                catch (ObjectDisposedException) when (Volatile.Read(ref _disposed) == 0 && slot.IsDisposed)
                {
                    // The idle-TTL sweeper (#262) disposed THIS session-receiver
                    // slot concurrently (slot.IsDisposed). The ConnectionSlot
                    // itself is still alive (_disposed == 0), so the dead child
                    // slot must NOT bubble to the connection-level catch in
                    // GetSessionReceiverAsync — that would evict the entire warm
                    // connection (orphaning its senders and sibling session
                    // links). Drop the dead slot (only if it's still the
                    // published one) and retry with a fresh slot, which is
                    // sweep-immune until its first Touch.
                    //
                    // An ObjectDisposedException from a concurrently-replaced
                    // stale CONNECTION (GetOrCreateConnectionAsync disposes the
                    // old connection while this ConnectionSlot stays alive) does
                    // NOT satisfy slot.IsDisposed, so it bubbles to the outer
                    // catch which re-dials — avoiding an infinite retry against
                    // a dead connection.
                    _sessionReceivers.TryRemove(KeyValuePair.Create(slotKey, slot));
                }
            }
        }

        public async Task<ServiceBusReceiver> AcquireBrokerAssignedSessionReceiverAsync(
            ServiceBusAmqpConnection connection,
            ServiceBusAmqpConnectionKey key,
            string queueName,
            CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            var audience = ServiceBusEndpoint.BuildQueueAudience(key.NamespaceFqdn, queueName);
            while (true)
            {
                // Open a fresh broker-assigned session receiver. The
                // connection.OpenSessionReceiverAsync contract guarantees a
                // non-null SessionId on success (it throws otherwise).
                var fresh = await connection
                    .OpenSessionReceiverAsync(queueName, audience, sessionId: null, prefetchCredit: 0, cancellationToken)
                    .ConfigureAwait(false);
                var resolvedId = fresh.SessionId!;
                var slotKey = new SessionReceiverKey(queueName, resolvedId);
                var ourSlot = SessionReceiverSlot.FromExisting(fresh);
                var inserted = _sessionReceivers.GetOrAdd(slotKey, ourSlot);
                if (ReferenceEquals(inserted, ourSlot))
                {
                    // We won — pool now owns 'fresh'. Re-check disposal so
                    // we surface ObjectDisposedException to the outer retry
                    // loop rather than leaking the slot we just published.
                    if (Volatile.Read(ref _disposed) != 0)
                    {
                        _sessionReceivers.TryRemove(KeyValuePair.Create(slotKey, ourSlot));
                        await ourSlot.DisposeAsync().ConfigureAwait(false);
                        throw new ObjectDisposedException(nameof(ConnectionSlot));
                    }
                    ourSlot.Touch(_timeProvider.GetUtcNow());
                    return fresh;
                }

                // Raced — another caller already adopted a receiver for the
                // same resolved session-id. Drop ours and return the cached
                // one. Dispose our slot (which also disposes 'fresh').
                await ourSlot.DisposeAsync().ConfigureAwait(false);
                try
                {
                    var receiver = await inserted
                        .GetOrCreateAsync(connection, key.Endpoint, queueName, resolvedId, cancellationToken)
                        .ConfigureAwait(false);
                    inserted.Touch(_timeProvider.GetUtcNow());
                    return receiver;
                }
                catch (ObjectDisposedException)
                {
                    // The session slot we lost to was invalidated before
                    // we could read its receiver. Surface that as a
                    // session-level retry (open a fresh broker-assigned
                    // session) rather than letting it bubble up to the
                    // outer connection-level catch, which would tear
                    // down the whole connection slot.
                    ThrowIfDisposed();
                    _sessionReceivers.TryRemove(KeyValuePair.Create(slotKey, inserted));
                    continue;
                }
            }
        }

        public async Task InvalidateSessionReceiverAsync(string queueName, string sessionId)
        {
            if (_sessionReceivers.TryRemove(new SessionReceiverKey(queueName, sessionId), out var slot))
                await slot.DisposeAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// Closes session-receiver links idle longer than
        /// <paramref name="idleTimeout"/> as of <paramref name="now"/>
        /// (#262). Each evicted link is detached and its Service Bus
        /// session returns to the broker; the shared connection stays
        /// warm. Returns the number of links evicted. Slots created but
        /// not yet successfully opened (no activity stamp) are never
        /// swept, so an in-flight broker-assigned acquire is safe.
        /// </summary>
        public async Task<int> SweepIdleSessionReceiversAsync(DateTimeOffset now, TimeSpan idleTimeout)
        {
            if (Volatile.Read(ref _disposed) != 0) return 0;
            var evicted = 0;
            foreach (var entry in _sessionReceivers.ToArray())
            {
                // Atomically commit to eviction. This loses the Dekker
                // handshake to any concurrent settle/peek that adopted the
                // receiver, so we never detach a session-receiver while a
                // DeleteMessage / ChangeMessageVisibility(0) / renew is in
                // flight on it.
                if (!entry.Value.TryBeginEvict(now, idleTimeout)) continue;
                _sessionReceivers.TryRemove(entry);
                try { await entry.Value.DisposeAsync().ConfigureAwait(false); }
                catch { /* swallow during idle eviction */ }
                evicted++;
            }
            return evicted;
        }

        /// <summary>
        /// Returns the receiver cached for (queue, sessionId) without
        /// opening a fresh session. Returns <c>null</c> when no slot
        /// exists yet or the cached slot has been disposed. The pool
        /// keeps ownership of the returned receiver — callers must not
        /// dispose it.
        /// </summary>
        public ServiceBusReceiver? TryGetExistingSessionReceiver(string queueName, string sessionId)
        {
            if (!_sessionReceivers.TryGetValue(new SessionReceiverKey(queueName, sessionId), out var slot))
                return null;
            // Atomically adopt the receiver and stamp activity. Returns null
            // when the idle sweeper (#262) is concurrently evicting this slot,
            // in which case the caller opens a fresh session — never reuses a
            // receiver the sweeper is about to dispose.
            return slot.TryGetActive(_timeProvider.GetUtcNow());
        }

        public async Task<ServiceBusManagementClient> GetOrCreateManagementClientAsync(
            ServiceBusAmqpConnection connection,
            ServiceBusAmqpConnectionKey key,
            string queueName,
            CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            var slot = await GetOrPublishSlotAsync(_managementClients, queueName, CreateManagementSlot).ConfigureAwait(false);
            return await slot
                .GetOrCreateAsync(
                    new ManagementOpenRequest(connection, key.Endpoint, queueName),
                    OpenManagementResourceAsync,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        public async Task InvalidateManagementClientAsync(string queueName)
        {
            if (_managementClients.TryRemove(queueName, out var slot))
                await slot.DisposeAsync().ConfigureAwait(false);
        }

        private static async Task EvictSlotsBestEffortAsync<TKey, TSlot>(
            ConcurrentDictionary<TKey, TSlot> slots)
            where TKey : notnull
            where TSlot : IAsyncDisposable
        {
            foreach (var key in slots.Keys.ToArray())
            {
                if (!slots.TryRemove(key, out var slot)) continue;
                try { await slot.DisposeAsync().ConfigureAwait(false); }
                catch { /* swallow during eviction */ }
            }
        }

        private static async Task DrainSlotsAsync<TKey, TSlot>(ConcurrentDictionary<TKey, TSlot> slots)
            where TKey : notnull
            where TSlot : IAsyncDisposable
        {
            while (!slots.IsEmpty)
            {
                foreach (var entry in slots.ToArray())
                {
                    if (slots.TryRemove(entry))
                        await entry.Value.DisposeAsync().ConfigureAwait(false);
                }
            }
        }

        private async ValueTask<TSlot> GetOrPublishSlotAsync<TKey, TSlot>(
            ConcurrentDictionary<TKey, TSlot> slots,
            TKey key,
            Func<TSlot> factory)
            where TKey : notnull
            where TSlot : IAsyncDisposable
        {
            if (slots.TryGetValue(key, out var existing))
            {
                if (Volatile.Read(ref _disposed) != 0)
                    throw new ObjectDisposedException(nameof(ConnectionSlot));
                return existing;
            }

            var fresh = factory();
            var slot = slots.GetOrAdd(key, fresh);
            if (ReferenceEquals(slot, fresh) && Volatile.Read(ref _disposed) != 0)
            {
                slots.TryRemove(KeyValuePair.Create(key, slot));
                await slot.DisposeAsync().ConfigureAwait(false);
                throw new ObjectDisposedException(nameof(ConnectionSlot));
            }
            return slot;
        }

        private static ResourceSlot<ServiceBusReceiver> CreateReceiverSlot()
            => new("ReceiverSlot", static receiver => receiver.IsClosed);

        private static ResourceSlot<ServiceBusAmqpSender> CreateSenderSlot()
            => new("SenderSlot", static sender => sender.IsClosed);

        private static ResourceSlot<ServiceBusManagementClient> CreateManagementSlot()
            => new("ManagementSlot", static client => client.IsClosed);

        private static SessionReceiverSlot CreateSessionReceiverSlot() => new();

        private static Task<ServiceBusReceiver> OpenReceiverResourceAsync(
            ReceiverOpenRequest request,
            CancellationToken cancellationToken)
        {
            var audience = ServiceBusEndpoint.BuildQueueAudience(request.Endpoint.LogicalNamespace, request.QueueName);
            return request.Connection.OpenReceiverAsync(
                request.QueueName, audience, prefetchCredit: 0, cancellationToken);
        }

        private static Task<ServiceBusAmqpSender> OpenSenderResourceAsync(
            SenderOpenRequest request,
            CancellationToken cancellationToken)
        {
            return request.Connection.OpenSenderAsync(request.EntityName, request.Audience, cancellationToken);
        }

        private static Task<ServiceBusManagementClient> OpenManagementResourceAsync(
            ManagementOpenRequest request,
            CancellationToken cancellationToken)
        {
            var audience = ServiceBusEndpoint.BuildQueueAudience(request.Endpoint.LogicalNamespace, request.QueueName);
            return request.Connection.OpenManagementClientAsync(audience, cancellationToken);
        }

        private readonly record struct ReceiverOpenRequest(
            ServiceBusAmqpConnection Connection,
            ServiceBusAmqpEndpoint Endpoint,
            string QueueName);

        private readonly record struct SenderOpenRequest(
            ServiceBusAmqpConnection Connection,
            string EntityName,
            string Audience);

        private readonly record struct ManagementOpenRequest(
            ServiceBusAmqpConnection Connection,
            ServiceBusAmqpEndpoint Endpoint,
            string QueueName);

        public int ReceiverCount => _receivers.Count;
        public int SenderCount => _senders.Count;
        public int SessionReceiverCount => _sessionReceivers.Count;
        public int ManagementClientCount => _managementClients.Count;

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(ConnectionSlot));
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            // Wait for any in-flight creator to finish so we don't leak a
            // connection it's about to publish into _connection, and so we
            // never Release a disposed semaphore.
            try
            {
                await _connectionLock.WaitAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // Already torn down — nothing to do.
                return;
            }
            try
            {
                // Drain-loop per dict: each GetOrPublish*Slot helper
                // checks _disposed after publishing, so any concurrent
                // publish either self-cleans (sees the flag we just set
                // before this method snapshotted) or lands in the dict
                // for us to drain here. Iterating until empty closes
                // the gap where a publisher hasn't reached its
                // disposed-check yet at our first snapshot. Receivers
                // first — they're owned by this slot's connection and
                // SB tears links down cleanly before we close it.
                await DrainSlotsAsync(_receivers).ConfigureAwait(false);
                await DrainSlotsAsync(_senders).ConfigureAwait(false);
                await DrainSlotsAsync(_sessionReceivers).ConfigureAwait(false);
                await DrainSlotsAsync(_managementClients).ConfigureAwait(false);

                var connection = Interlocked.Exchange(ref _connection, null);
                if (connection is not null)
                    await connection.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                _connectionLock.Release();
                _connectionLock.Dispose();
            }
        }
    }

    /// <summary>
    /// Cache key for session receivers. Queue name is case-insensitive
    /// to mirror SB's queue-path matching; session-id is case-sensitive
    /// because SB treats session ids as opaque byte-equal tokens.
    /// </summary>
    private readonly record struct SessionReceiverKey(string QueueName, string SessionId)
    {
        public bool Equals(SessionReceiverKey other) =>
            string.Equals(QueueName, other.QueueName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(SessionId, other.SessionId, StringComparison.Ordinal);

        public override int GetHashCode() =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(QueueName),
                StringComparer.Ordinal.GetHashCode(SessionId));
    }

    private sealed class SessionReceiverSlot : IAsyncDisposable
    {
        private readonly ResourceSlot<ServiceBusReceiver> _slot;
        private long _lastActivityTicks;
        // Idle-sweep state machine: None -> Tentative -> {None (back out) |
        // Committed (dispose)}. Drives the Dekker handshake in TryGetActive /
        // TryBeginEvict so a settle and the sweeper can never both win.
        private const int EvictNone = 0;
        private const int EvictTentative = 1;
        private const int EvictCommitted = 2;
        private int _evicting;

        public SessionReceiverSlot()
            : this(initialReceiver: null)
        {
        }

        private SessionReceiverSlot(ServiceBusReceiver? initialReceiver)
            => _slot = new ResourceSlot<ServiceBusReceiver>(
                nameof(SessionReceiverSlot),
                static receiver => receiver.IsClosed,
                initialReceiver);

        /// <summary>
        /// Records receive/settle activity for the idle-TTL sweeper
        /// (#262). A zero stamp means "never used / open in flight" and
        /// is never considered idle, so a slot that is mid-creation is
        /// safe from eviction.
        /// </summary>
        public void Touch(DateTimeOffset now) => Volatile.Write(ref _lastActivityTicks, now.UtcTicks);

        /// <summary>
        /// True once this slot has been disposed (by the idle-TTL sweeper
        /// or connection eviction). Distinguishes a swept child slot from a
        /// concurrently-replaced stale connection in the
        /// <see cref="ConnectionSlot.GetOrCreateSessionReceiverAsync"/>
        /// retry filter (#262 Fix A).
        /// </summary>
        public bool IsDisposed => _slot.IsDisposed;

        /// <summary>
        /// True when the slot has recorded at least one activity and has
        /// been idle for at least <paramref name="idleTimeout"/> as of
        /// <paramref name="now"/>. Disposed slots report not-idle (the
        /// owner already evicted them).
        /// </summary>
        public bool IsIdle(DateTimeOffset now, TimeSpan idleTimeout)
        {
            if (_slot.IsDisposed) return false;
            var ticks = Volatile.Read(ref _lastActivityTicks);
            if (ticks == 0) return false;
            return now - new DateTimeOffset(ticks, TimeSpan.Zero) >= idleTimeout;
        }

        /// <summary>
        /// Creates a slot that already owns a pre-opened receiver.
        /// Used by the broker-assigned acquire path: the caller opens
        /// the receiver against the connection before the slot key
        /// (session-id) is known, then publishes the slot into the
        /// dict keyed by the resolved session-id.
        /// </summary>
        public static SessionReceiverSlot FromExisting(ServiceBusReceiver receiver)
        {
            ArgumentNullException.ThrowIfNull(receiver);
            return new SessionReceiverSlot(receiver);
        }

        public Task<ServiceBusReceiver> GetOrCreateAsync(
            ServiceBusAmqpConnection connection,
            ServiceBusAmqpEndpoint endpoint,
            string queueName,
            string sessionId,
            CancellationToken cancellationToken)
            => _slot.GetOrCreateAsync(
                new SessionReceiverOpenRequest(connection, endpoint, queueName, sessionId),
                OpenSessionReceiverResourceAsync,
                cancellationToken);

        /// <summary>
        /// Atomically adopts the cached receiver for a settle/peek and stamps
        /// activity, or returns <c>null</c> when no receiver is cached or the
        /// idle-TTL sweeper (#262) is concurrently evicting this slot. The
        /// stamp, full fence, and re-read of <see cref="_evicting"/> form a
        /// Dekker handshake with <see cref="TryBeginEvict"/>: a caller can
        /// never adopt a receiver the sweeper is about to dispose, and the
        /// sweeper can never dispose a receiver a caller just adopted. A
        /// <c>null</c> result is indistinguishable from "no cached receiver",
        /// so the caller simply opens a fresh session.
        /// </summary>
        public ServiceBusReceiver? TryGetActive(DateTimeOffset now)
        {
            var receiver = _slot.TryGetExisting();
            if (receiver is null) return null;
            Volatile.Write(ref _lastActivityTicks, now.UtcTicks);
            // Full fence so the activity stamp is ordered before the eviction
            // re-read (store-load ordering Volatile alone does not guarantee).
            Interlocked.MemoryBarrier();
            var spin = new SpinWait();
            while (true)
            {
                switch (Volatile.Read(ref _evicting))
                {
                    case EvictNone:
                        // Sweeper is not evicting (or backed out after seeing our
                        // stamp). By the Dekker argument, observing EvictNone here
                        // guarantees the sweeper will not commit, so the receiver
                        // is safe to hand out.
                        return receiver;
                    case EvictCommitted:
                        // Sweeper committed and now owns disposal; report "no
                        // cached receiver" so the caller opens a fresh session.
                        return null;
                    default:
                        // EvictTentative: the sweeper is mid-decision and resolves
                        // to EvictNone or EvictCommitted in a few instructions
                        // (no I/O, no await). Spin briefly — never returning a
                        // false null for a slot that stays live, and never busy-
                        // waiting on the slow DisposeAsync (that follows commit).
                        spin.SpinOnce();
                        break;
                }
            }
        }

        /// <summary>
        /// Commits this slot to idle eviction. Returns <c>true</c> only when
        /// the slot is idle AND no concurrent <see cref="TryGetActive"/> won
        /// the Dekker handshake — the caller then owns disposal and every
        /// later <see cref="TryGetActive"/> returns <c>null</c>. Returns
        /// <c>false</c> (leaving the slot fully usable) when the slot is not
        /// idle or an activity stamp landed concurrently.
        /// </summary>
        public bool TryBeginEvict(DateTimeOffset now, TimeSpan idleTimeout)
        {
            if (!IsIdle(now, idleTimeout)) return false;
            Volatile.Write(ref _evicting, EvictTentative);
            // Full fence so the tentative flag is published before re-reading
            // the activity stamp a concurrent TryGetActive may have written.
            Interlocked.MemoryBarrier();
            if (!IsIdle(now, idleTimeout))
            {
                // A settle stamped activity concurrently: back out so the live
                // receiver is not detached, and release any spinning reader.
                Volatile.Write(ref _evicting, EvictNone);
                return false;
            }
            Volatile.Write(ref _evicting, EvictCommitted);
            return true;
        }

        public ValueTask DisposeAsync() => _slot.DisposeAsync();

        private static Task<ServiceBusReceiver> OpenSessionReceiverResourceAsync(
            SessionReceiverOpenRequest request,
            CancellationToken cancellationToken)
        {
            var audience = ServiceBusEndpoint.BuildQueueAudience(request.Endpoint.LogicalNamespace, request.QueueName);
            return request.Connection.OpenSessionReceiverAsync(
                request.QueueName,
                audience,
                request.SessionId,
                prefetchCredit: 0,
                cancellationToken);
        }

        private readonly record struct SessionReceiverOpenRequest(
            ServiceBusAmqpConnection Connection,
            ServiceBusAmqpEndpoint Endpoint,
            string QueueName,
            string SessionId);
    }
}
