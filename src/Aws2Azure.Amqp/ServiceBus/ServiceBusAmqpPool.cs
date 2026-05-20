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
            var slot = _connections.GetOrAdd(key, static _ => new ConnectionSlot());
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
            var slot = _connections.GetOrAdd(key, static _ => new ConnectionSlot());
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
            var slot = _connections.GetOrAdd(key, static _ => new ConnectionSlot());
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
            var slot = _connections.GetOrAdd(key, static _ => new ConnectionSlot());
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

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        // Snapshot then clear so concurrent InvalidateAsync calls can
        // bail out via the disposed check above without double-disposing.
        var slots = _connections.Values.ToArray();
        _connections.Clear();
        foreach (var slot in slots)
        {
            await slot.DisposeAsync().ConfigureAwait(false);
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
            var slot = _receivers.GetOrAdd(queueName, static _ => new ReceiverSlot());
            return await slot
                .GetOrCreateAsync(connection, key.NamespaceFqdn, queueName, cancellationToken)
                .ConfigureAwait(false);
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
            var slot = _sessionReceivers.GetOrAdd(
                new SessionReceiverKey(queueName, sessionId),
                static _ => new SessionReceiverSlot());
            return await slot
                .GetOrCreateAsync(connection, key.NamespaceFqdn, queueName, sessionId, cancellationToken)
                .ConfigureAwait(false);
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

        public async Task<ServiceBusManagementClient> GetOrCreateManagementClientAsync(
            ServiceBusAmqpConnection connection,
            ServiceBusAmqpConnectionKey key,
            string queueName,
            CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            var slot = _managementClients.GetOrAdd(queueName, static _ => new ManagementSlot());
            return await slot
                .GetOrCreateAsync(connection, key.NamespaceFqdn, queueName, cancellationToken)
                .ConfigureAwait(false);
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
                // Receivers first — they're owned by this slot's connection
                // and SB tears links down cleanly before we close the connection.
                var receivers = _receivers.Values.ToArray();
                _receivers.Clear();
                foreach (var r in receivers)
                    await r.DisposeAsync().ConfigureAwait(false);

                var sessionReceivers = _sessionReceivers.Values.ToArray();
                _sessionReceivers.Clear();
                foreach (var r in sessionReceivers)
                    await r.DisposeAsync().ConfigureAwait(false);

                var clients = _managementClients.Values.ToArray();
                _managementClients.Clear();
                foreach (var c in clients)
                    await c.DisposeAsync().ConfigureAwait(false);

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
