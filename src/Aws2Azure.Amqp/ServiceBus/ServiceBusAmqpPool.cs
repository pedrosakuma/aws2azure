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

        public int ReceiverCount => _receivers.Count;

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
}
