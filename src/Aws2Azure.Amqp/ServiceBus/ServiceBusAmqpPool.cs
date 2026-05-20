using System.Collections.Concurrent;

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
/// Failure semantics: this slice does <b>not</b> attempt to detect a
/// half-dead connection or auto-reconnect. When a handler observes a
/// link/connection-level failure it must call
/// <see cref="InvalidateAsync"/> to evict the entry; the next
/// <see cref="GetReceiverAsync"/> call will rebuild from scratch. The
/// next slice (8b.4) wires this contract into the SQS handler's retry
/// loop. Active reconnection is intentionally deferred.
/// </para>
/// </summary>
internal sealed class ServiceBusAmqpPool : IAsyncDisposable
{
    private readonly IServiceBusAmqpConnectionFactory _factory;
    private readonly ConcurrentDictionary<ServiceBusAmqpConnectionKey, ConnectionSlot> _connections
        = new();
    private int _disposed;

    public ServiceBusAmqpPool(IServiceBusAmqpConnectionFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
    }

    /// <summary>
    /// Returns the shared <see cref="ServiceBusReceiver"/> for the
    /// given (namespace, key, queue) tuple, creating the connection
    /// and the receiver link on first use. The returned receiver is
    /// owned by the pool — callers must <b>not</b> dispose it
    /// directly; call <see cref="InvalidateAsync"/> instead when a
    /// failure occurs.
    /// </summary>
    public async Task<ServiceBusReceiver> GetReceiverAsync(
        string namespaceFqdn,
        string sasKeyName,
        string sasKey,
        string queueName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceFqdn);
        ArgumentException.ThrowIfNullOrWhiteSpace(sasKeyName);
        ArgumentException.ThrowIfNullOrWhiteSpace(sasKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(queueName);
        ThrowIfDisposed();

        var key = new ServiceBusAmqpConnectionKey(namespaceFqdn, sasKeyName);
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
    /// <see cref="ServiceBusReceiver.SessionId"/> (future slice 7c).
    /// </para>
    /// </summary>
    public async Task<ServiceBusReceiver> GetSessionReceiverAsync(
        string namespaceFqdn,
        string sasKeyName,
        string sasKey,
        string queueName,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceFqdn);
        ArgumentException.ThrowIfNullOrWhiteSpace(sasKeyName);
        ArgumentException.ThrowIfNullOrWhiteSpace(sasKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(queueName);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ThrowIfDisposed();

        var key = new ServiceBusAmqpConnectionKey(namespaceFqdn, sasKeyName);
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
        string namespaceFqdn,
        string sasKeyName,
        string sasKey,
        string queueName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceFqdn);
        ArgumentException.ThrowIfNullOrWhiteSpace(sasKeyName);
        ArgumentException.ThrowIfNullOrWhiteSpace(sasKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(queueName);
        ThrowIfDisposed();

        var key = new ServiceBusAmqpConnectionKey(namespaceFqdn, sasKeyName);
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
        string namespaceFqdn,
        string sasKeyName,
        string queueName,
        string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceFqdn);
        ArgumentException.ThrowIfNullOrWhiteSpace(sasKeyName);
        ArgumentException.ThrowIfNullOrWhiteSpace(queueName);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (Volatile.Read(ref _disposed) != 0) return;

        var key = new ServiceBusAmqpConnectionKey(namespaceFqdn, sasKeyName);
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
    /// </summary>
    public ServiceBusReceiver? TryGetExistingSessionReceiver(
        string namespaceFqdn,
        string sasKeyName,
        string queueName,
        string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceFqdn);
        ArgumentException.ThrowIfNullOrWhiteSpace(sasKeyName);
        ArgumentException.ThrowIfNullOrWhiteSpace(queueName);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (Volatile.Read(ref _disposed) != 0) return null;

        var key = new ServiceBusAmqpConnectionKey(namespaceFqdn, sasKeyName);
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
        string namespaceFqdn,
        string sasKeyName,
        string sasKey,
        string queueName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceFqdn);
        ArgumentException.ThrowIfNullOrWhiteSpace(sasKeyName);
        ArgumentException.ThrowIfNullOrWhiteSpace(sasKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(queueName);
        ThrowIfDisposed();

        var key = new ServiceBusAmqpConnectionKey(namespaceFqdn, sasKeyName);
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
    public async Task InvalidateAsync(
        string namespaceFqdn,
        string sasKeyName,
        string queueName,
        bool closeConnection = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceFqdn);
        ArgumentException.ThrowIfNullOrWhiteSpace(sasKeyName);
        ArgumentException.ThrowIfNullOrWhiteSpace(queueName);
        if (Volatile.Read(ref _disposed) != 0) return;

        var key = new ServiceBusAmqpConnectionKey(namespaceFqdn, sasKeyName);
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
    /// Evicts the cached management client for (namespace, key, queue)
    /// without touching the receiver or connection. Next call to
    /// <see cref="GetManagementClientAsync"/> rebuilds it.
    /// </summary>
    public async Task InvalidateManagementClientAsync(
        string namespaceFqdn,
        string sasKeyName,
        string queueName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceFqdn);
        ArgumentException.ThrowIfNullOrWhiteSpace(sasKeyName);
        ArgumentException.ThrowIfNullOrWhiteSpace(queueName);
        if (Volatile.Read(ref _disposed) != 0) return;

        var key = new ServiceBusAmqpConnectionKey(namespaceFqdn, sasKeyName);
        if (!_connections.TryGetValue(key, out var slot)) return;
        await slot.InvalidateManagementClientAsync(queueName).ConfigureAwait(false);
    }

    /// <summary>Number of cached connections; useful in tests.</summary>
    public int ConnectionCount => _connections.Count;

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
        var fresh = new ConnectionSlot();
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
        private readonly SemaphoreSlim _connectionLock = new(1, 1);
        private readonly ConcurrentDictionary<string, ReceiverSlot> _receivers
            = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<SessionReceiverKey, SessionReceiverSlot> _sessionReceivers
            = new();
        private readonly ConcurrentDictionary<string, ManagementSlot> _managementClients
            = new(StringComparer.OrdinalIgnoreCase);
        private ServiceBusAmqpConnection? _connection;
        private int _disposed;

        public async Task<ServiceBusAmqpConnection> GetOrCreateConnectionAsync(
            IServiceBusAmqpConnectionFactory factory,
            ServiceBusAmqpConnectionKey key,
            string sasKey,
            CancellationToken cancellationToken)
        {
            var existing = Volatile.Read(ref _connection);
            if (existing is not null) return existing;
            ThrowIfDisposed();

            await _connectionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                existing = _connection;
                if (existing is not null) return existing;

                var created = await factory
                    .CreateAsync(key.NamespaceFqdn, key.SasKeyName, sasKey, cancellationToken)
                    .ConfigureAwait(false);
                Volatile.Write(ref _connection, created);
                return created;
            }
            finally
            {
                _connectionLock.Release();
            }
        }

        public async Task<ServiceBusReceiver> GetOrCreateReceiverAsync(
            ServiceBusAmqpConnection connection,
            ServiceBusAmqpConnectionKey key,
            string queueName,
            CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            var slot = await GetOrPublishReceiverSlotAsync(queueName).ConfigureAwait(false);
            return await slot
                .GetOrCreateAsync(connection, key.NamespaceFqdn, queueName, cancellationToken)
                .ConfigureAwait(false);
        }

        private async ValueTask<ReceiverSlot> GetOrPublishReceiverSlotAsync(string queueName)
        {
            if (_receivers.TryGetValue(queueName, out var existing))
            {
                if (Volatile.Read(ref _disposed) != 0)
                    throw new ObjectDisposedException(nameof(ConnectionSlot));
                return existing;
            }
            var fresh = new ReceiverSlot();
            var slot = _receivers.GetOrAdd(queueName, fresh);
            if (ReferenceEquals(slot, fresh) && Volatile.Read(ref _disposed) != 0)
            {
                _receivers.TryRemove(KeyValuePair.Create(queueName, slot));
                await slot.DisposeAsync().ConfigureAwait(false);
                throw new ObjectDisposedException(nameof(ConnectionSlot));
            }
            return slot;
        }

        public async Task InvalidateReceiverAsync(string queueName)
        {
            if (_receivers.TryRemove(queueName, out var slot))
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
            var slot = await GetOrPublishSessionReceiverSlotAsync(slotKey, static () => new SessionReceiverSlot())
                .ConfigureAwait(false);
            return await slot
                .GetOrCreateAsync(connection, key.NamespaceFqdn, queueName, sessionId, cancellationToken)
                .ConfigureAwait(false);
        }

        private async ValueTask<SessionReceiverSlot> GetOrPublishSessionReceiverSlotAsync(
            SessionReceiverKey slotKey,
            Func<SessionReceiverSlot> factory)
        {
            if (_sessionReceivers.TryGetValue(slotKey, out var existing))
            {
                if (Volatile.Read(ref _disposed) != 0)
                    throw new ObjectDisposedException(nameof(ConnectionSlot));
                return existing;
            }
            var fresh = factory();
            var slot = _sessionReceivers.GetOrAdd(slotKey, fresh);
            if (ReferenceEquals(slot, fresh) && Volatile.Read(ref _disposed) != 0)
            {
                _sessionReceivers.TryRemove(KeyValuePair.Create(slotKey, slot));
                await slot.DisposeAsync().ConfigureAwait(false);
                throw new ObjectDisposedException(nameof(ConnectionSlot));
            }
            return slot;
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
                    return fresh;
                }

                // Raced — another caller already adopted a receiver for the
                // same resolved session-id. Drop ours and return the cached
                // one. Dispose our slot (which also disposes 'fresh').
                await ourSlot.DisposeAsync().ConfigureAwait(false);
                try
                {
                    return await inserted
                        .GetOrCreateAsync(connection, key.NamespaceFqdn, queueName, resolvedId, cancellationToken)
                        .ConfigureAwait(false);
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
            return slot.TryGetExisting();
        }

        public async Task<ServiceBusManagementClient> GetOrCreateManagementClientAsync(
            ServiceBusAmqpConnection connection,
            ServiceBusAmqpConnectionKey key,
            string queueName,
            CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            var slot = await GetOrPublishManagementSlotAsync(queueName).ConfigureAwait(false);
            return await slot
                .GetOrCreateAsync(connection, key.NamespaceFqdn, queueName, cancellationToken)
                .ConfigureAwait(false);
        }

        private async ValueTask<ManagementSlot> GetOrPublishManagementSlotAsync(string queueName)
        {
            if (_managementClients.TryGetValue(queueName, out var existing))
            {
                if (Volatile.Read(ref _disposed) != 0)
                    throw new ObjectDisposedException(nameof(ConnectionSlot));
                return existing;
            }
            var fresh = new ManagementSlot();
            var slot = _managementClients.GetOrAdd(queueName, fresh);
            if (ReferenceEquals(slot, fresh) && Volatile.Read(ref _disposed) != 0)
            {
                _managementClients.TryRemove(KeyValuePair.Create(queueName, slot));
                await slot.DisposeAsync().ConfigureAwait(false);
                throw new ObjectDisposedException(nameof(ConnectionSlot));
            }
            return slot;
        }

        public async Task InvalidateManagementClientAsync(string queueName)
        {
            if (_managementClients.TryRemove(queueName, out var slot))
                await slot.DisposeAsync().ConfigureAwait(false);
        }

        public int ReceiverCount => _receivers.Count;
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
                while (!_receivers.IsEmpty)
                {
                    foreach (var entry in _receivers.ToArray())
                    {
                        if (_receivers.TryRemove(entry))
                            await entry.Value.DisposeAsync().ConfigureAwait(false);
                    }
                }

                while (!_sessionReceivers.IsEmpty)
                {
                    foreach (var entry in _sessionReceivers.ToArray())
                    {
                        if (_sessionReceivers.TryRemove(entry))
                            await entry.Value.DisposeAsync().ConfigureAwait(false);
                    }
                }

                while (!_managementClients.IsEmpty)
                {
                    foreach (var entry in _managementClients.ToArray())
                    {
                        if (_managementClients.TryRemove(entry))
                            await entry.Value.DisposeAsync().ConfigureAwait(false);
                    }
                }

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

    private sealed class ReceiverSlot : IAsyncDisposable
    {
        private readonly SemaphoreSlim _lock = new(1, 1);
        private ServiceBusReceiver? _receiver;
        private int _disposed;

        public async Task<ServiceBusReceiver> GetOrCreateAsync(
            ServiceBusAmqpConnection connection,
            string namespaceFqdn,
            string queueName,
            CancellationToken cancellationToken)
        {
            var existing = Volatile.Read(ref _receiver);
            if (existing is not null) return existing;
            ThrowIfDisposed();

            await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                existing = _receiver;
                if (existing is not null) return existing;

                var audience = ServiceBusEndpoint.BuildQueueAudience(namespaceFqdn, queueName);
                var created = await connection
                    .OpenReceiverAsync(queueName, audience, prefetchCredit: 0, cancellationToken)
                    .ConfigureAwait(false);
                Volatile.Write(ref _receiver, created);
                return created;
            }
            finally
            {
                _lock.Release();
            }
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(ReceiverSlot));
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try
            {
                await _lock.WaitAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            try
            {
                var receiver = Interlocked.Exchange(ref _receiver, null);
                if (receiver is not null)
                    await receiver.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                _lock.Release();
                _lock.Dispose();
            }
        }
    }

    private sealed class ManagementSlot : IAsyncDisposable
    {
        private readonly SemaphoreSlim _lock = new(1, 1);
        private ServiceBusManagementClient? _client;
        private int _disposed;

        public async Task<ServiceBusManagementClient> GetOrCreateAsync(
            ServiceBusAmqpConnection connection,
            string namespaceFqdn,
            string queueName,
            CancellationToken cancellationToken)
        {
            var existing = Volatile.Read(ref _client);
            if (existing is not null) return existing;
            ThrowIfDisposed();

            await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                existing = _client;
                if (existing is not null) return existing;

                var audience = ServiceBusEndpoint.BuildQueueAudience(namespaceFqdn, queueName);
                var created = await connection
                    .OpenManagementClientAsync(audience, cancellationToken)
                    .ConfigureAwait(false);
                Volatile.Write(ref _client, created);
                return created;
            }
            finally
            {
                _lock.Release();
            }
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(ManagementSlot));
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try
            {
                await _lock.WaitAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            try
            {
                var client = Interlocked.Exchange(ref _client, null);
                if (client is not null)
                    await client.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                _lock.Release();
                _lock.Dispose();
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
        private readonly SemaphoreSlim _lock = new(1, 1);
        private ServiceBusReceiver? _receiver;
        private int _disposed;

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
            var slot = new SessionReceiverSlot();
            slot._receiver = receiver;
            return slot;
        }

        public async Task<ServiceBusReceiver> GetOrCreateAsync(
            ServiceBusAmqpConnection connection,
            string namespaceFqdn,
            string queueName,
            string sessionId,
            CancellationToken cancellationToken)
        {
            var existing = Volatile.Read(ref _receiver);
            if (existing is not null) return existing;
            ThrowIfDisposed();

            await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                existing = _receiver;
                if (existing is not null) return existing;

                var audience = ServiceBusEndpoint.BuildQueueAudience(namespaceFqdn, queueName);
                var created = await connection
                    .OpenSessionReceiverAsync(queueName, audience, sessionId, prefetchCredit: 0, cancellationToken)
                    .ConfigureAwait(false);
                Volatile.Write(ref _receiver, created);
                return created;
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// Returns the cached receiver if one has already been opened
        /// (or adopted from the broker-assigned acquire path), without
        /// opening a fresh session. Returns <c>null</c> when the slot
        /// has not yet materialised a receiver — used by settle paths
        /// (DeleteMessage, CMV=0) where opening a new session-lock
        /// just to immediately fail the lock-token lookup would starve
        /// the message group.
        /// </summary>
        public ServiceBusReceiver? TryGetExisting()
        {
            if (Volatile.Read(ref _disposed) != 0) return null;
            return Volatile.Read(ref _receiver);
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(SessionReceiverSlot));
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try
            {
                await _lock.WaitAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            try
            {
                var receiver = Interlocked.Exchange(ref _receiver, null);
                if (receiver is not null)
                    await receiver.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                _lock.Release();
                _lock.Dispose();
            }
        }
    }
}
