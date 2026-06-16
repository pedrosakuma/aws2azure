using System.Collections.Concurrent;
using Aws2Azure.Amqp.Connection;
using Aws2Azure.Amqp.Security;
using Aws2Azure.Amqp.ServiceBus;
using Aws2Azure.Core.Azure;
using Aws2Azure.Core.Configuration;

namespace Aws2Azure.Modules.Kinesis.EventHubsAmqp;

internal readonly record struct EventHubsAmqpConnectionKey(
    ServiceBusAmqpEndpoint Endpoint,
    string CredentialMarker);

internal readonly record struct EventHubsAmqpConnectionLease(
    EventHubsAmqpConnectionKey Key,
    ServiceBusAmqpEndpoint Endpoint,
    ServiceBusAmqpConnection Connection);

internal interface IEventHubsAmqpConnectionFactory
{
    Task<ServiceBusAmqpConnection> CreateAsync(
        EventHubsAmqpConnectionKey key,
        EventHubsCredentials credentials,
        CancellationToken cancellationToken);
}

internal sealed class EventHubsAmqpConnectionFactory : IEventHubsAmqpConnectionFactory
{
    private readonly EntraIdTokenProvider _tokenProvider;
    private readonly AmqpConnectionSettings _connectionSettings;

    public EventHubsAmqpConnectionFactory(
        EntraIdTokenProvider tokenProvider,
        AmqpConnectionSettings connectionSettings)
    {
        ArgumentNullException.ThrowIfNull(tokenProvider);
        ArgumentNullException.ThrowIfNull(connectionSettings);
        _tokenProvider = tokenProvider;
        _connectionSettings = connectionSettings;
    }

    public async Task<ServiceBusAmqpConnection> CreateAsync(
        EventHubsAmqpConnectionKey key,
        EventHubsCredentials credentials,
        CancellationToken cancellationToken)
    {
        var transport = await ServiceBusAmqpConnector
            .ConnectAsync(key.Endpoint, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var tokenProvider = CreateTokenProvider(credentials);
            var perEndpointSettings = _connectionSettings with { Hostname = key.Endpoint.LogicalNamespace };
            return await ServiceBusAmqpConnection
                .OpenAsync(transport, tokenProvider, perEndpointSettings, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            await transport.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private IAmqpTokenProvider CreateTokenProvider(EventHubsCredentials credentials)
    {
        if (!string.IsNullOrWhiteSpace(credentials.SasKeyName)
            && !string.IsNullOrWhiteSpace(credentials.SasKey))
        {
            return new ServiceBusSasTokenProvider(credentials.SasKeyName, credentials.SasKey);
        }

        return new EventHubsBearerTokenProvider(_tokenProvider, credentials);
    }
}

internal sealed class EventHubsAmqpConnectionPool : IAsyncDisposable
{
    private readonly IEventHubsAmqpConnectionFactory _factory;
    private readonly ConcurrentDictionary<EventHubsAmqpConnectionKey, ConnectionSlot> _connections = new();
    private int _disposed;

    public EventHubsAmqpConnectionPool(
        EntraIdTokenProvider tokenProvider,
        AmqpConnectionSettings connectionSettings)
        : this(new EventHubsAmqpConnectionFactory(tokenProvider, connectionSettings))
    {
    }

    internal EventHubsAmqpConnectionPool(IEventHubsAmqpConnectionFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
    }

    public async Task<EventHubsAmqpConnectionLease> GetConnectionAsync(
        EventHubsCredentials credentials,
        string namespaceFqdn,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceFqdn);
        ThrowIfDisposed();

        var endpoint = EventHubsAmqpEndpointResolver.Resolve(credentials, namespaceFqdn);
        var key = new EventHubsAmqpConnectionKey(endpoint, EventHubsCredentialMarker.Build(credentials));
        while (true)
        {
            var slot = await GetOrCreateSlotAsync(key).ConfigureAwait(false);
            try
            {
                var connection = await slot
                    .GetOrCreateConnectionAsync(_factory, key, credentials, cancellationToken)
                    .ConfigureAwait(false);
                ThrowIfDisposed();
                return new EventHubsAmqpConnectionLease(key, endpoint, connection);
            }
            catch (ObjectDisposedException) when (Volatile.Read(ref _disposed) == 0)
            {
                _connections.TryRemove(KeyValuePair.Create(key, slot));
            }
        }
    }

    public async Task InvalidateConnectionAsync(EventHubsAmqpConnectionKey key)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        if (_connections.TryRemove(key, out var slot))
        {
            await slot.DisposeAsync().ConfigureAwait(false);
        }
    }

    public int ConnectionCount => _connections.Count;

    private async ValueTask<ConnectionSlot> GetOrCreateSlotAsync(EventHubsAmqpConnectionKey key)
    {
        if (_connections.TryGetValue(key, out var existing))
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(EventHubsAmqpConnectionPool));
            return existing;
        }

        var fresh = new ConnectionSlot();
        var slot = _connections.GetOrAdd(key, fresh);
        if (ReferenceEquals(slot, fresh) && Volatile.Read(ref _disposed) != 0)
        {
            _connections.TryRemove(KeyValuePair.Create(key, slot));
            await slot.DisposeAsync().ConfigureAwait(false);
            throw new ObjectDisposedException(nameof(EventHubsAmqpConnectionPool));
        }

        return slot;
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(EventHubsAmqpConnectionPool));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
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
        private readonly ResourceSlot<ServiceBusAmqpConnection> _slot =
            new(nameof(EventHubsAmqpConnectionPool), static connection => connection.IsClosed);

        public Task<ServiceBusAmqpConnection> GetOrCreateConnectionAsync(
            IEventHubsAmqpConnectionFactory factory,
            EventHubsAmqpConnectionKey key,
            EventHubsCredentials credentials,
            CancellationToken cancellationToken)
            => _slot.GetOrCreateAsync(
                new OpenRequest(factory, key, credentials),
                static (request, ct) => request.Factory.CreateAsync(request.Key, request.Credentials, ct),
                cancellationToken);

        public ValueTask DisposeAsync() => _slot.DisposeAsync();

        private readonly record struct OpenRequest(
            IEventHubsAmqpConnectionFactory Factory,
            EventHubsAmqpConnectionKey Key,
            EventHubsCredentials Credentials);
    }
}
