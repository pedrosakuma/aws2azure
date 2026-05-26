using System.Buffers;
using System.Collections.Concurrent;
using System.Text.Json;
using Aws2Azure.Amqp.Connection;
using Aws2Azure.Amqp.Framing;
using Aws2Azure.Amqp.Security;
using Aws2Azure.Amqp.ServiceBus;
using Aws2Azure.Core.Azure;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Modules.Kinesis.EventHubsRest;

namespace Aws2Azure.Modules.Kinesis.EventHubsAmqp;

public interface IEventHubsAmqpSender
{
    Task SendAsync(
        EventHubsCredentials credentials,
        string namespaceFqdn,
        string entityPath,
        ReadOnlyMemory<byte> body,
        IReadOnlyDictionary<string, object>? annotations,
        CancellationToken cancellationToken);

    Task<EventHubsBatchSendResult> SendBatchAsync(
        EventHubsCredentials credentials,
        string namespaceFqdn,
        string entityPath,
        IReadOnlyList<(ReadOnlyMemory<byte> body, IReadOnlyDictionary<string, object>? annotations)> messages,
        CancellationToken cancellationToken);
}

public sealed record EventHubsBatchSendResult(IReadOnlyList<EventHubsBatchSendOutcome> Outcomes);

public sealed record EventHubsBatchSendOutcome(bool Succeeded, string? ErrorCode, string? ErrorMessage);

internal enum EventHubsAmqpFailureKind
{
    Unknown = 0,
    Transient,
    Throttled,
    Auth,
    ClientFatal,
    ServerFatal,
    Redirect,
}

internal sealed class EventHubsAmqpException : Exception
{
    public EventHubsAmqpException(
        string message,
        Exception innerException,
        EventHubsAmqpFailureKind kind,
        string? condition = null,
        string? description = null)
        : base(message, innerException)
    {
        Kind = kind;
        Condition = condition;
        Description = description;
    }

    public EventHubsAmqpFailureKind Kind { get; }
    public string? Condition { get; }
    public string? Description { get; }
}

internal sealed class EventHubsAmqpSender : IEventHubsAmqpSender, IAsyncDisposable
{
    private readonly EntraIdTokenProvider _tokenProvider;
    private readonly AmqpConnectionSettings _connectionSettings;
    private readonly ConcurrentDictionary<ConnectionKey, ConnectionSlot> _connections = new();
    private int _disposed;

    public EventHubsAmqpSender(
        EntraIdTokenProvider tokenProvider,
        AmqpConnectionSettings connectionSettings)
    {
        ArgumentNullException.ThrowIfNull(tokenProvider);
        ArgumentNullException.ThrowIfNull(connectionSettings);
        _tokenProvider = tokenProvider;
        _connectionSettings = connectionSettings;
    }

    public async Task SendAsync(
        EventHubsCredentials credentials,
        string namespaceFqdn,
        string entityPath,
        ReadOnlyMemory<byte> body,
        IReadOnlyDictionary<string, object>? annotations,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceFqdn);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityPath);
        ThrowIfDisposed();

        var endpoint = ResolveEndpoint(credentials, namespaceFqdn);
        var key = new ConnectionKey(endpoint, BuildCredentialMarker(credentials));
        var slot = GetOrCreateSlot(key);
        var connection = await slot.GetOrCreateConnectionAsync(
                _tokenProvider,
                _connectionSettings,
                credentials,
                endpoint,
                cancellationToken)
            .ConfigureAwait(false);

        try
        {
            var sender = await slot.GetOrCreateSenderAsync(
                    connection,
                    entityPath,
                    BuildAudience(namespaceFqdn, entityPath),
                    cancellationToken)
                .ConfigureAwait(false);
            await sender.SendAsync(
                    new AmqpMessage
                    {
                        Body = body,
                        MessageAnnotations = CreateAnnotations(annotations),
                    },
                    settled: false,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (TryWrap(ex, out var wrapped))
        {
            await InvalidateOnFailureAsync(key, entityPath, wrapped).ConfigureAwait(false);
            throw wrapped;
        }
    }

    public async Task<EventHubsBatchSendResult> SendBatchAsync(
        EventHubsCredentials credentials,
        string namespaceFqdn,
        string entityPath,
        IReadOnlyList<(ReadOnlyMemory<byte> body, IReadOnlyDictionary<string, object>? annotations)> messages,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceFqdn);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityPath);
        ArgumentNullException.ThrowIfNull(messages);
        ThrowIfDisposed();

        if (messages.Count == 0)
        {
            return new EventHubsBatchSendResult([]);
        }

        var endpoint = ResolveEndpoint(credentials, namespaceFqdn);
        var key = new ConnectionKey(endpoint, BuildCredentialMarker(credentials));
        var slot = GetOrCreateSlot(key);
        var connection = await slot.GetOrCreateConnectionAsync(
                _tokenProvider,
                _connectionSettings,
                credentials,
                endpoint,
                cancellationToken)
            .ConfigureAwait(false);

        var outcomes = new EventHubsBatchSendOutcome[messages.Count];
        try
        {
            var sender = await slot.GetOrCreateSenderAsync(
                    connection,
                    entityPath,
                    BuildAudience(namespaceFqdn, entityPath),
                    cancellationToken)
                .ConfigureAwait(false);

            for (var i = 0; i < messages.Count; i++)
            {
                var message = messages[i];
                try
                {
                    await sender.SendAsync(
                            new AmqpMessage
                            {
                                Body = message.body,
                                MessageAnnotations = CreateAnnotations(message.annotations),
                            },
                            settled: false,
                            cancellationToken)
                        .ConfigureAwait(false);
                    outcomes[i] = new EventHubsBatchSendOutcome(true, null, null);
                }
                catch (ServiceBusSendException sendException)
                {
                    outcomes[i] = CreateBatchOutcome(new EventHubsAmqpException(
                        "Event Hubs rejected the AMQP transfer.",
                        sendException,
                        MapOutcome(sendException.Outcome)));
                }
                catch (Exception ex) when (TryWrap(ex, out var wrapped))
                {
                    await InvalidateOnFailureAsync(key, entityPath, wrapped).ConfigureAwait(false);
                    if (wrapped.Kind == EventHubsAmqpFailureKind.Auth)
                    {
                        throw wrapped;
                    }

                    var failureOutcome = CreateBatchOutcome(wrapped);
                    for (var remaining = i; remaining < outcomes.Length; remaining++)
                    {
                        outcomes[remaining] = failureOutcome;
                    }

                    return new EventHubsBatchSendResult(outcomes);
                }
            }

            return new EventHubsBatchSendResult(outcomes);
        }
        catch (Exception ex) when (TryWrap(ex, out var wrapped))
        {
            await InvalidateOnFailureAsync(key, entityPath, wrapped).ConfigureAwait(false);
            if (wrapped.Kind == EventHubsAmqpFailureKind.Auth)
            {
                throw wrapped;
            }

            var failureOutcome = CreateBatchOutcome(wrapped);
            for (var i = 0; i < outcomes.Length; i++)
            {
                outcomes[i] = failureOutcome;
            }

            return new EventHubsBatchSendResult(outcomes);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var entry in _connections.ToArray())
        {
            if (_connections.TryRemove(entry))
            {
                await entry.Value.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private ConnectionSlot GetOrCreateSlot(ConnectionKey key)
    {
        ThrowIfDisposed();
        return _connections.GetOrAdd(key, static _ => new ConnectionSlot());
    }

    private async Task InvalidateConnectionAsync(ConnectionKey key)
    {
        if (_connections.TryRemove(key, out var slot))
        {
            await slot.DisposeAsync().ConfigureAwait(false);
        }
    }

    // Decides at the failure-kind granularity what to tear down. Auth /
    // ServerFatal / Redirect signals that the entire connection is
    // unusable (token expired, broker moved, peer state corrupted) so we
    // drop the whole slot. Throttled / Transient / ClientFatal only
    // implicates the specific sender link, so we evict just that sender
    // and let the next call re-attach a fresh link on the same connection
    // — preserving the cached CBS handshake and connection-level state.
    private async Task InvalidateOnFailureAsync(ConnectionKey key, string entityPath, EventHubsAmqpException failure)
    {
        switch (failure.Kind)
        {
            case EventHubsAmqpFailureKind.Auth:
            case EventHubsAmqpFailureKind.ServerFatal:
            case EventHubsAmqpFailureKind.Redirect:
                await InvalidateConnectionAsync(key).ConfigureAwait(false);
                return;
            default:
                if (_connections.TryGetValue(key, out var slot))
                {
                    await slot.InvalidateSenderAsync(entityPath).ConfigureAwait(false);
                }
                return;
        }
    }

    private static ServiceBusAmqpEndpoint ResolveEndpoint(EventHubsCredentials credentials, string namespaceFqdn)
    {
        if (!string.IsNullOrWhiteSpace(credentials.Endpoint)
            && Uri.TryCreate(credentials.Endpoint, UriKind.Absolute, out var endpointUri))
        {
            var logicalNamespace = namespaceFqdn.Trim().ToLowerInvariant();
            if (string.Equals(endpointUri.Scheme, "amqp", StringComparison.OrdinalIgnoreCase)
                || string.Equals(endpointUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            {
                return ServiceBusAmqpEndpoint.Plain(
                    endpointUri.Host,
                    endpointUri.IsDefaultPort ? ServiceBusEndpoint.AmqpPort : endpointUri.Port,
                    logicalNamespace);
            }

            return ServiceBusAmqpEndpoint.Tls(
                endpointUri.Host,
                endpointUri.IsDefaultPort ? ServiceBusEndpoint.AmqpsPort : endpointUri.Port,
                logicalNamespace);
        }

        if (Uri.TryCreate("amqps://" + namespaceFqdn.Trim(), UriKind.Absolute, out var namespaceUri))
        {
            // namespaceUri.Port is -1 when no explicit port is in the URI
            var port = namespaceUri.Port > 0 ? namespaceUri.Port : ServiceBusEndpoint.AmqpsPort;
            return ServiceBusAmqpEndpoint.Tls(
                namespaceUri.Host,
                port,
                namespaceFqdn.Trim().ToLowerInvariant());
        }

        return ServiceBusAmqpEndpoint.Tls(namespaceFqdn.Trim().ToLowerInvariant());
    }

    private static string BuildAudience(string namespaceFqdn, string entityPath)
        => "amqps://" + namespaceFqdn.Trim().TrimEnd('/') + "/" + entityPath.Trim().TrimStart('/');

    private static string BuildCredentialMarker(EventHubsCredentials credentials)
    {
        if (!string.IsNullOrWhiteSpace(credentials.SasKeyName))
        {
            return "sas|" + credentials.SasKeyName.Trim();
        }

        return "aad|" + credentials.TenantId + "|" + credentials.ClientId;
    }

    private static AmqpMessageAnnotations? CreateAnnotations(IReadOnlyDictionary<string, object>? annotations)
    {
        if (annotations is null || annotations.Count == 0)
        {
            return null;
        }

        annotations.TryGetValue(AmqpMessageAnnotations.KeyPartitionKey, out var partitionKey);
        annotations.TryGetValue(AmqpMessageAnnotations.KeyViaPartitionKey, out var viaPartitionKey);
        annotations.TryGetValue(AmqpMessageAnnotations.KeyScheduledEnqueueTime, out var scheduledEnqueueTime);

        if (partitionKey is not string && viaPartitionKey is not string && scheduledEnqueueTime is not DateTimeOffset)
        {
            return null;
        }

        return new AmqpMessageAnnotations
        {
            PartitionKey = partitionKey as string,
            ViaPartitionKey = viaPartitionKey as string,
            ScheduledEnqueueTime = scheduledEnqueueTime as DateTimeOffset?,
        };
    }

    private static EventHubsBatchSendOutcome CreateBatchOutcome(EventHubsAmqpException exception)
    {
        if (exception.Kind == EventHubsAmqpFailureKind.Throttled)
        {
            return new EventHubsBatchSendOutcome(
                false,
                "ProvisionedThroughputExceededException",
                "Azure Event Hubs throttled the batch send.");
        }

        return new EventHubsBatchSendOutcome(
            false,
            "InternalFailure",
            BuildFailureMessage(exception));
    }

    private static string BuildFailureMessage(EventHubsAmqpException exception)
    {
        var message = string.Equals(exception.Condition, AmqpErrorCondition.Timeout, StringComparison.Ordinal)
            ? "Azure Event Hubs AMQP send timed out."
            : "Azure Event Hubs AMQP send failed.";

        if (!string.IsNullOrWhiteSpace(exception.Description))
        {
            message += " " + exception.Description;
        }

        return message;
    }

    private static bool TryWrap(Exception exception, out EventHubsAmqpException wrapped)
    {
        switch (exception)
        {
            case EventHubsAmqpException alreadyWrapped:
                wrapped = alreadyWrapped;
                return true;
            case CbsAuthenticationException cbsAuthentication:
                wrapped = new EventHubsAmqpException(
                    "Event Hubs AMQP authorization failed.",
                    cbsAuthentication,
                    EventHubsAmqpFailureKind.Auth,
                    description: cbsAuthentication.StatusDescription);
                return true;
            case ServiceBusSendException sendException:
                wrapped = new EventHubsAmqpException(
                    "Event Hubs rejected the AMQP transfer.",
                    sendException,
                    MapOutcome(sendException.Outcome));
                return true;
            case AmqpLinkException linkException:
                wrapped = new EventHubsAmqpException(
                    linkException.Message,
                    linkException,
                    MapKind(linkException.Kind),
                    linkException.PeerCondition,
                    linkException.PeerDescription);
                return true;
            case AmqpConnectionException connectionException:
                wrapped = new EventHubsAmqpException(
                    connectionException.Message,
                    connectionException,
                    MapKind(connectionException.Kind));
                return true;
            case TimeoutException timeoutException:
                wrapped = new EventHubsAmqpException(
                    "Event Hubs AMQP send timed out.",
                    timeoutException,
                    EventHubsAmqpFailureKind.Transient,
                    AmqpErrorCondition.Timeout,
                    timeoutException.Message);
                return true;
            case ObjectDisposedException disposedException:
                // A pooled sender link can be disposed by InvalidateSenderAsync
                // while a concurrent SendAsync is still holding the reference;
                // ServiceBusAmqpSender then surfaces the disposal as a raw
                // ObjectDisposedException on its internal gate. Translate it
                // into a transient EH failure so the caller (and the granular
                // invalidate path in this sender) handle it like any other
                // recoverable AMQP error.
                wrapped = new EventHubsAmqpException(
                    "Event Hubs AMQP sender link was closed during send.",
                    disposedException,
                    EventHubsAmqpFailureKind.Transient);
                return true;
            default:
                wrapped = default!;
                return false;
        }
    }

    private static EventHubsAmqpFailureKind MapKind(AmqpErrorKind kind) => kind switch
    {
        AmqpErrorKind.Transient => EventHubsAmqpFailureKind.Transient,
        AmqpErrorKind.Throttled => EventHubsAmqpFailureKind.Throttled,
        AmqpErrorKind.Auth => EventHubsAmqpFailureKind.Auth,
        AmqpErrorKind.ClientFatal => EventHubsAmqpFailureKind.ClientFatal,
        AmqpErrorKind.ServerFatal => EventHubsAmqpFailureKind.ServerFatal,
        AmqpErrorKind.Redirect => EventHubsAmqpFailureKind.Redirect,
        _ => EventHubsAmqpFailureKind.Unknown,
    };

    private static EventHubsAmqpFailureKind MapOutcome(AmqpDispositionOutcome outcome) => outcome switch
    {
        AmqpDispositionOutcome.Released => EventHubsAmqpFailureKind.Transient,
        AmqpDispositionOutcome.Modified => EventHubsAmqpFailureKind.Throttled,
        _ => EventHubsAmqpFailureKind.Unknown,
    };

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(EventHubsAmqpSender));
        }
    }

    private readonly record struct ConnectionKey(ServiceBusAmqpEndpoint Endpoint, string CredentialMarker);

    private sealed class ConnectionSlot : IAsyncDisposable
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly ConcurrentDictionary<string, SenderSlot> _senders = new(StringComparer.Ordinal);
        private ServiceBusAmqpConnection? _connection;
        private int _disposed;

        public async Task<ServiceBusAmqpSender> GetOrCreateSenderAsync(
            ServiceBusAmqpConnection connection,
            string entityPath,
            string audience,
            CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            // Two races to defend against:
            //   (a) GetOrCreateSenderAsync vs ConnectionSlot.DisposeAsync:
            //       caller passes ThrowIfDisposed, then DisposeAsync sets
            //       _disposed=1 and snapshots _senders.Keys; caller then
            //       publishes a fresh slot that survives the drain.
            //   (b) GetOrCreateSenderAsync vs InvalidateSenderAsync: an
            //       existing slot is removed + disposed between
            //       _senders.GetOrAdd and slot.GetOrCreateAsync, so the
            //       latter throws ObjectDisposedException on its disposed
            //       gate. We retry with a fresh slot.
            while (true)
            {
                var fresh = new SenderSlot();
                var slot = _senders.GetOrAdd(entityPath, fresh);

                if (Volatile.Read(ref _disposed) != 0)
                {
                    if (ReferenceEquals(slot, fresh))
                    {
                        _senders.TryRemove(new KeyValuePair<string, SenderSlot>(entityPath, slot));
                        try { await slot.DisposeAsync().ConfigureAwait(false); }
                        catch { /* drain race */ }
                    }
                    throw new ObjectDisposedException(nameof(ConnectionSlot));
                }

                try
                {
                    return await slot.GetOrCreateAsync(connection, entityPath, audience, cancellationToken).ConfigureAwait(false);
                }
                catch (ObjectDisposedException) when (Volatile.Read(ref _disposed) == 0)
                {
                    // Slot was invalidated mid-flight. Drop the stale
                    // reference and retry with a fresh slot.
                    _senders.TryRemove(new KeyValuePair<string, SenderSlot>(entityPath, slot));
                    continue;
                }
            }
        }

        public async Task InvalidateSenderAsync(string entityPath)
        {
            if (_senders.TryRemove(entityPath, out var slot))
            {
                await slot.DisposeAsync().ConfigureAwait(false);
            }
        }

        public async Task<ServiceBusAmqpConnection> GetOrCreateConnectionAsync(
            EntraIdTokenProvider tokenProvider,
            AmqpConnectionSettings connectionSettings,
            EventHubsCredentials credentials,
            ServiceBusAmqpEndpoint endpoint,
            CancellationToken cancellationToken)
        {
            var existing = Volatile.Read(ref _connection);
            if (existing is not null && !existing.IsClosed)
            {
                return existing;
            }

            ThrowIfDisposed();
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                existing = _connection;
                if (existing is not null && !existing.IsClosed)
                {
                    return existing;
                }

                if (existing is not null)
                {
                    try
                    {
                        await existing.DisposeAsync().ConfigureAwait(false);
                    }
                    catch
                    {
                    }

                    Volatile.Write(ref _connection, null);
                }

                var transport = await ServiceBusAmqpConnector.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
                try
                {
                    IAmqpTokenProvider amqpTokenProvider = CreateTokenProvider(tokenProvider, credentials);
                    var perEndpointSettings = connectionSettings with { Hostname = endpoint.LogicalNamespace };
                    var created = await ServiceBusAmqpConnection.OpenAsync(
                            transport,
                            amqpTokenProvider,
                            perEndpointSettings,
                            cancellationToken)
                        .ConfigureAwait(false);
                    Volatile.Write(ref _connection, created);
                    return created;
                }
                catch
                {
                    await transport.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                foreach (var entityPath in _senders.Keys.ToArray())
                {
                    if (_senders.TryRemove(entityPath, out var senderSlot))
                    {
                        try
                        {
                            await senderSlot.DisposeAsync().ConfigureAwait(false);
                        }
                        catch
                        {
                        }
                    }
                }

                if (_connection is not null)
                {
                    await _connection.DisposeAsync().ConfigureAwait(false);
                    Volatile.Write(ref _connection, null);
                }
            }
            finally
            {
                _gate.Release();
                _gate.Dispose();
            }
        }

        private static IAmqpTokenProvider CreateTokenProvider(EntraIdTokenProvider tokenProvider, EventHubsCredentials credentials)
        {
            if (!string.IsNullOrWhiteSpace(credentials.SasKeyName)
                && !string.IsNullOrWhiteSpace(credentials.SasKey))
            {
                return new ServiceBusSasTokenProvider(credentials.SasKeyName, credentials.SasKey);
            }

            return new EventHubsBearerTokenProvider(tokenProvider, credentials);
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                throw new ObjectDisposedException(nameof(ConnectionSlot));
            }
        }

        private sealed class SenderSlot : IAsyncDisposable
        {
            private readonly SemaphoreSlim _lock = new(1, 1);
            private ServiceBusAmqpSender? _sender;
            private int _disposed;

            public async Task<ServiceBusAmqpSender> GetOrCreateAsync(
                ServiceBusAmqpConnection connection,
                string entityPath,
                string audience,
                CancellationToken cancellationToken)
            {
                var existing = Volatile.Read(ref _sender);
                if (existing is not null && !existing.IsClosed) return existing;
                ThrowIfDisposed();

                await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    ThrowIfDisposed();
                    existing = _sender;
                    if (existing is not null && !existing.IsClosed) return existing;

                    if (existing is not null)
                    {
                        try { await existing.DisposeAsync().ConfigureAwait(false); }
                        catch { /* swallow during eviction */ }
                        Volatile.Write(ref _sender, null);
                    }

                    var created = await connection
                        .OpenSenderAsync(entityPath, audience, cancellationToken)
                        .ConfigureAwait(false);
                    Volatile.Write(ref _sender, created);
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
                    throw new ObjectDisposedException(nameof(SenderSlot));
            }

            public async ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                try { await _lock.WaitAsync().ConfigureAwait(false); }
                catch (ObjectDisposedException) { return; }
                try
                {
                    var sender = Interlocked.Exchange(ref _sender, null);
                    if (sender is not null)
                        await sender.DisposeAsync().ConfigureAwait(false);
                }
                finally
                {
                    _lock.Release();
                    _lock.Dispose();
                }
            }
        }
    }

    private sealed class EventHubsBearerTokenProvider : IAmqpTokenProvider
    {
        private readonly EntraIdTokenProvider _tokenProvider;
        private readonly EventHubsCredentials _credentials;

        public EventHubsBearerTokenProvider(EntraIdTokenProvider tokenProvider, EventHubsCredentials credentials)
        {
            ArgumentNullException.ThrowIfNull(tokenProvider);
            ArgumentNullException.ThrowIfNull(credentials);
            _tokenProvider = tokenProvider;
            _credentials = credentials;
        }

        public string TokenType => "jwt";

        public AmqpToken GetToken(string audience)
        {
            _ = audience;
            var token = _tokenProvider
                .GetTokenAsync(
                    _credentials.TenantId!,
                    _credentials.ClientId!,
                    _credentials.ClientSecret!,
                    EventHubsAuthenticator.EventHubsScope)
                .AsTask()
                .GetAwaiter()
                .GetResult();
            return new AmqpToken(token, TryParseJwtExpiry(token));
        }

        private static DateTimeOffset? TryParseJwtExpiry(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return null;
            }

            var firstDot = token.IndexOf('.');
            if (firstDot < 0)
            {
                return null;
            }

            var secondDot = token.IndexOf('.', firstDot + 1);
            if (secondDot < 0)
            {
                return null;
            }

            var payload = token.AsSpan(firstDot + 1, secondDot - firstDot - 1);
            if (payload.IsEmpty)
            {
                return null;
            }

            var requiredLength = GetBase64UrlDecodedLength(payload.Length);
            var rented = ArrayPool<byte>.Shared.Rent(requiredLength);
            try
            {
                if (!TryDecodeBase64Url(payload, rented, out var written))
                {
                    return null;
                }

                var reader = new Utf8JsonReader(rented.AsSpan(0, written), isFinalBlock: true, state: default);
                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.PropertyName
                        && reader.ValueTextEquals("exp")
                        && reader.Read()
                        && reader.TokenType == JsonTokenType.Number
                        && reader.TryGetInt64(out var seconds))
                    {
                        return DateTimeOffset.FromUnixTimeSeconds(seconds);
                    }
                }

                return null;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        private static int GetBase64UrlDecodedLength(int encodedLength)
        {
            var paddedLength = encodedLength + ((4 - (encodedLength % 4)) % 4);
            return (paddedLength / 4) * 3;
        }

        private static bool TryDecodeBase64Url(ReadOnlySpan<char> encoded, Span<byte> destination, out int written)
        {
            var paddedLength = encoded.Length + ((4 - (encoded.Length % 4)) % 4);
            var rented = ArrayPool<char>.Shared.Rent(paddedLength);
            try
            {
                for (var i = 0; i < encoded.Length; i++)
                {
                    rented[i] = encoded[i] switch
                    {
                        '-' => '+',
                        '_' => '/',
                        var value => value,
                    };
                }

                for (var i = encoded.Length; i < paddedLength; i++)
                {
                    rented[i] = '=';
                }

                return Convert.TryFromBase64Chars(rented.AsSpan(0, paddedLength), destination, out written);
            }
            finally
            {
                ArrayPool<char>.Shared.Return(rented);
            }
        }
    }
}
