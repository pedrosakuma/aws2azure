using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Aws2Azure.Amqp.Codec;
using Aws2Azure.Amqp.Connection;
using Aws2Azure.Amqp.Framing;
using Aws2Azure.Amqp.Security;
using Aws2Azure.Amqp.ServiceBus;
using Aws2Azure.Core.Azure;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Modules.Kinesis.EventHubsRest;

namespace Aws2Azure.Modules.Kinesis.EventHubsAmqp;

public interface IEventHubsAmqpReceiver
{
    Task<EventHubsReceiveResult> ReceiveAsync(
        EventHubsCredentials credentials,
        string namespaceFqdn,
        string entityPath,
        string consumerGroup,
        int partitionId,
        EventHubsReceivePosition position,
        int maxMessages,
        TimeSpan quiescentTimeout,
        CancellationToken cancellationToken);
}

public abstract record EventHubsReceivePosition
{
    private EventHubsReceivePosition()
    {
    }

    public sealed record FromStart : EventHubsReceivePosition;
    public sealed record FromLatest : EventHubsReceivePosition;
    public sealed record FromEnqueuedTime(DateTimeOffset Value) : EventHubsReceivePosition;
    public sealed record FromOffsetExclusive(string Value) : EventHubsReceivePosition;
    public sealed record FromSequenceExclusive(long Value) : EventHubsReceivePosition;
}

public sealed record EventHubsReceiveResult(IReadOnlyList<EventHubsReceivedMessage> Messages);

public sealed record EventHubsReceivedMessage(
    ReadOnlyMemory<byte> Body,
    IReadOnlyDictionary<string, object> Annotations,
    string? Offset,
    long? SequenceNumber,
    DateTimeOffset? EnqueuedTime,
    string? PartitionKey);

internal sealed class EventHubsAmqpReceiver : IEventHubsAmqpReceiver, IAsyncDisposable
{
    private readonly EntraIdTokenProvider _tokenProvider;
    private readonly AmqpConnectionSettings _connectionSettings;
    private readonly ConcurrentDictionary<ConnectionKey, ConnectionSlot> _connections = new();
    private readonly ConcurrentDictionary<ReceiverKey, ReceiverSlot> _receivers = new();
    private int _disposed;

    public EventHubsAmqpReceiver(
        EntraIdTokenProvider tokenProvider,
        AmqpConnectionSettings connectionSettings)
    {
        ArgumentNullException.ThrowIfNull(tokenProvider);
        ArgumentNullException.ThrowIfNull(connectionSettings);
        _tokenProvider = tokenProvider;
        _connectionSettings = connectionSettings;
    }

    // EH partition consumers want "drain the burst, don't block waiting
    // to fill the full batch ceiling once data is flowing" — the same
    // semantic Azure SDK's PartitionReceiver.ReceiveBatchAsync exposes.
    // 25 ms gives the broker plenty of time to pipeline the rest of a
    // credit window of transfers without holding the caller for the
    // full 500 ms quiescent timeout once the first messages arrive.
    private static readonly TimeSpan EventHubsTailWait = TimeSpan.FromMilliseconds(25);

    public async Task<EventHubsReceiveResult> ReceiveAsync(
        EventHubsCredentials credentials,
        string namespaceFqdn,
        string entityPath,
        string consumerGroup,
        int partitionId,
        EventHubsReceivePosition position,
        int maxMessages,
        TimeSpan quiescentTimeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceFqdn);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerGroup);
        ArgumentNullException.ThrowIfNull(position);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxMessages, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(quiescentTimeout, TimeSpan.Zero);
        ThrowIfDisposed();

        var receiverAddress = BuildReceiverAddress(entityPath, consumerGroup, partitionId);
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
            var audience = BuildAudience(namespaceFqdn, receiverAddress);
            var receiverKey = new ReceiverKey(key, receiverAddress);
            var receiverSlot = GetOrCreateReceiverSlot(receiverKey);

            var messages = new List<EventHubsReceivedMessage>(Math.Min(maxMessages, 256));
            try
            {
                // Serialise concurrent receives on the same (connection,
                // partition consumer-group) slot via the slot's receive
                // gate: a single AMQP link has a single broker cursor, so
                // racing receives would steal each other's records.
                // Single ReceiveBatchAsync call: the link now honours
                // tailWait so it returns promptly once a burst lands,
                // and a partial batch is an explicit "broker paused"
                // signal — looping here just re-waits the full
                // quiescentTimeout for a second burst the caller will
                // pick up on the NEXT GetRecords RPC anyway.
                var batch = await receiverSlot.ExecuteReceiveAsync(
                        connection,
                        receiverAddress,
                        audience,
                        () => EventHubsSelectorFilter.Encode(BuildSelectorExpression(position)),
                        (receiver, ct) => receiver.ReceiveBatchAsync(
                            maxMessages,
                            quiescentTimeout,
                            EventHubsTailWait,
                            ct),
                        cancellationToken)
                    .ConfigureAwait(false);

                if (batch.Count > 0)
                {
                    messages.Capacity = Math.Max(messages.Capacity, batch.Count);
                    for (var i = 0; i < batch.Count; i++)
                    {
                        messages.Add(Map(batch[i]));
                    }
                }
            }
            catch
            {
                await InvalidateReceiverAsync(receiverKey, receiverSlot).ConfigureAwait(false);
                throw;
            }

            return new EventHubsReceiveResult(messages);
        }
        catch (Exception ex) when (TryWrap(ex, out var wrapped))
        {
            await InvalidateConnectionAsync(key).ConfigureAwait(false);
            throw wrapped;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var entry in _receivers.ToArray())
        {
            if (_receivers.TryRemove(entry))
            {
                await entry.Value.DisposeAsync().ConfigureAwait(false);
            }
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

    private ReceiverSlot GetOrCreateReceiverSlot(ReceiverKey key)
    {
        ThrowIfDisposed();
        return _receivers.GetOrAdd(key, static _ => new ReceiverSlot());
    }

    private async Task InvalidateReceiverAsync(ReceiverKey key, ReceiverSlot? expected = null)
    {
        if (expected is null)
        {
            if (_receivers.TryRemove(key, out var slot))
            {
                await slot.DisposeAsync().ConfigureAwait(false);
            }

            return;
        }

        // Slot-identity-aware removal: only drop the slot if it is still
        // the one the failing caller observed. A concurrent caller that
        // already replaced the slot keeps its fresh receiver.
        var kvp = new KeyValuePair<ReceiverKey, ReceiverSlot>(key, expected);
        if (((ICollection<KeyValuePair<ReceiverKey, ReceiverSlot>>)_receivers).Remove(kvp))
        {
            await expected.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task InvalidateConnectionAsync(ConnectionKey key)
    {
        // Drop every receiver dependent on this connection first so we
        // don't surface a stale ServiceBusReceiver wrapping a closed link
        // on the next call.
        foreach (var entry in _receivers.ToArray())
        {
            if (entry.Key.Connection.Equals(key) && _receivers.TryRemove(entry))
            {
                await entry.Value.DisposeAsync().ConfigureAwait(false);
            }
        }

        if (_connections.TryRemove(key, out var slot))
        {
            await slot.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static EventHubsReceivedMessage Map(ServiceBusReceivedMessage message)
    {
        var annotations = new Dictionary<string, object>(StringComparer.Ordinal);
        var parsed = message.Annotations;
        if (parsed?.Offset is { } offset)
        {
            annotations[AmqpMessageAnnotations.KeyOffset] = offset;
        }

        if (parsed?.SequenceNumber is { } sequenceNumber)
        {
            annotations[AmqpMessageAnnotations.KeySequenceNumber] = sequenceNumber;
        }

        if (parsed?.EnqueuedTime is { } enqueuedTime)
        {
            annotations[AmqpMessageAnnotations.KeyEnqueuedTime] = enqueuedTime;
        }

        if (parsed?.PartitionKey is { } partitionKey)
        {
            annotations[AmqpMessageAnnotations.KeyPartitionKey] = partitionKey;
        }

        return new EventHubsReceivedMessage(
            message.Body,
            annotations,
            parsed?.Offset,
            parsed?.SequenceNumber,
            parsed?.EnqueuedTime,
            parsed?.PartitionKey);
    }

    private static string BuildReceiverAddress(string entityPath, string consumerGroup, int partitionId)
        => entityPath.Trim().Trim('/')
            + "/ConsumerGroups/"
            + consumerGroup.Trim().Trim('/')
            + "/Partitions/"
            + partitionId.ToString(CultureInfo.InvariantCulture);

    private static string BuildSelectorExpression(EventHubsReceivePosition position)
        => position switch
        {
            EventHubsReceivePosition.FromStart => "amqp.annotation.x-opt-offset > '-1'",
            EventHubsReceivePosition.FromLatest => "amqp.annotation.x-opt-offset > '@latest'",
            EventHubsReceivePosition.FromEnqueuedTime fromEnqueuedTime
                => "amqp.annotation.x-opt-enqueued-time > '"
                    + fromEnqueuedTime.Value.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)
                    + "'",
            EventHubsReceivePosition.FromOffsetExclusive fromOffset
                => "amqp.annotation.x-opt-offset > '"
                    + EscapeFilterValue(fromOffset.Value)
                    + "'",
            EventHubsReceivePosition.FromSequenceExclusive fromSequence
                => "amqp.annotation.x-opt-sequence-number > '"
                    + fromSequence.Value.ToString(CultureInfo.InvariantCulture)
                    + "'",
            _ => throw new ArgumentOutOfRangeException(nameof(position)),
        };

    private static string EscapeFilterValue(string value)
        => value.Replace("'", "''", StringComparison.Ordinal);

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
            return ServiceBusAmqpEndpoint.Tls(
                namespaceUri.Host,
                namespaceUri.Port,
                namespaceFqdn.Trim().ToLowerInvariant());
        }

        return ServiceBusAmqpEndpoint.Tls(namespaceFqdn.Trim().ToLowerInvariant());
    }

    private static string BuildAudience(string namespaceFqdn, string receiverAddress)
        => "amqps://" + namespaceFqdn.Trim().TrimEnd('/') + "/" + receiverAddress.Trim().TrimStart('/');

    private static string BuildCredentialMarker(EventHubsCredentials credentials)
    {
        if (!string.IsNullOrWhiteSpace(credentials.SasKeyName))
        {
            return "sas|" + credentials.SasKeyName.Trim();
        }

        return "aad|" + credentials.TenantId + "|" + credentials.ClientId;
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
                    "Event Hubs AMQP receive timed out.",
                    timeoutException,
                    EventHubsAmqpFailureKind.Transient,
                    AmqpErrorCondition.Timeout,
                    timeoutException.Message);
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

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(EventHubsAmqpReceiver));
        }
    }

    private readonly record struct ConnectionKey(ServiceBusAmqpEndpoint Endpoint, string CredentialMarker);

    // Pooled per (connection, receiverAddress). The receiverAddress
    // already encodes (entityPath, consumerGroup, partitionId), so a
    // single slot serves all calls against the same partition. The
    // EH broker advances the cursor on the link, so we open with the
    // caller's filter ONCE and treat all subsequent ReceiveAsync calls
    // as continuations — the caller's position parameter is honoured
    // only on attach.
    private readonly record struct ReceiverKey(ConnectionKey Connection, string ReceiverAddress)
    {
        public bool Equals(ReceiverKey other)
            => Connection.Equals(other.Connection)
                && string.Equals(ReceiverAddress, other.ReceiverAddress, StringComparison.OrdinalIgnoreCase);

        public override int GetHashCode()
            => HashCode.Combine(Connection, StringComparer.OrdinalIgnoreCase.GetHashCode(ReceiverAddress));
    }

    private sealed class ReceiverSlot : IAsyncDisposable
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly SemaphoreSlim _receiveGate = new(1, 1);
        private ServiceBusReceiver? _receiver;
        private int _disposed;

        public async Task<T> ExecuteReceiveAsync<T>(
            ServiceBusAmqpConnection connection,
            string receiverAddress,
            string audience,
            Func<ReadOnlyMemory<byte>> sourceFilterFactory,
            Func<ServiceBusReceiver, CancellationToken, Task<T>> receive,
            CancellationToken cancellationToken)
        {
            await _receiveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                var receiver = await GetOrCreateReceiverAsync(
                        connection,
                        receiverAddress,
                        audience,
                        sourceFilterFactory,
                        cancellationToken)
                    .ConfigureAwait(false);
                return await receive(receiver, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _receiveGate.Release();
            }
        }

        private async Task<ServiceBusReceiver> GetOrCreateReceiverAsync(
            ServiceBusAmqpConnection connection,
            string receiverAddress,
            string audience,
            Func<ReadOnlyMemory<byte>> sourceFilterFactory,
            CancellationToken cancellationToken)
        {
            var existing = Volatile.Read(ref _receiver);
            if (existing is not null
                && !existing.IsClosed
                && Volatile.Read(ref _disposed) == 0)
            {
                return existing;
            }

            ThrowIfDisposed();
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                existing = _receiver;
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

                    Volatile.Write(ref _receiver, null);
                }

                var sourceFilter = sourceFilterFactory();
                var created = await connection.OpenReceiverAsync(
                        receiverAddress,
                        audience,
                        prefetchCredit: 0,
                        sourceFilter,
                        cancellationToken)
                    .ConfigureAwait(false);
                Volatile.Write(ref _receiver, created);
                return created;
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

            // Wait out any in-flight receive before disposing the link.
            await _receiveGate.WaitAsync().ConfigureAwait(false);
            try
            {
                await _gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (_receiver is not null)
                    {
                        try
                        {
                            await _receiver.DisposeAsync().ConfigureAwait(false);
                        }
                        catch
                        {
                        }

                        Volatile.Write(ref _receiver, null);
                    }
                }
                finally
                {
                    _gate.Release();
                    _gate.Dispose();
                }
            }
            finally
            {
                _receiveGate.Release();
                _receiveGate.Dispose();
            }
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                throw new ObjectDisposedException(nameof(ReceiverSlot));
            }
        }
    }

    private sealed class ConnectionSlot : IAsyncDisposable
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private ServiceBusAmqpConnection? _connection;
        private int _disposed;

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

                var reader = new Utf8JsonReader(rented.AsSpan(0, written));
                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.PropertyName
                        && reader.ValueTextEquals("exp"u8))
                    {
                        if (!reader.Read() || reader.TokenType != JsonTokenType.Number || !reader.TryGetInt64(out var exp))
                        {
                            return null;
                        }

                        return DateTimeOffset.FromUnixTimeSeconds(exp);
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
            => ((encodedLength + 3) / 4) * 3;

        private static bool TryDecodeBase64Url(ReadOnlySpan<char> input, Span<byte> destination, out int written)
        {
            written = 0;
            var padding = (4 - (input.Length % 4)) % 4;
            Span<char> base64 = input.Length + padding <= 512
                ? stackalloc char[input.Length + padding]
                : new char[input.Length + padding];

            for (var i = 0; i < input.Length; i++)
            {
                base64[i] = input[i] switch
                {
                    '-' => '+',
                    '_' => '/',
                    _ => input[i],
                };
            }

            for (var i = 0; i < padding; i++)
            {
                base64[input.Length + i] = '=';
            }

            return Convert.TryFromBase64Chars(base64, destination, out written);
        }
    }
}

internal static class EventHubsSelectorFilter
{
    private const string FilterSymbol = "apache.org:selector-filter:string";

    public static ReadOnlyMemory<byte> Encode(string expression)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);

        var capacity = (FilterSymbol.Length * 2) + (expression.Length * 4) + 32;
        Span<byte> elements = stackalloc byte[capacity];
        var offset = 0;

        AmqpVariableWriter.WriteSymbol(elements[offset..], FilterSymbol, out var keyLength);
        offset += keyLength;

        elements[offset++] = AmqpFormatCode.Described;
        AmqpVariableWriter.WriteSymbol(elements[offset..], FilterSymbol, out var descriptorLength);
        offset += descriptorLength;
        AmqpVariableWriter.WriteString(elements[offset..], expression, out var valueLength);
        offset += valueLength;

        var encoded = new byte[offset + 16];
        AmqpCompoundWriter.WriteMap(encoded, elements[..offset], pairCount: 1, out var written);
        Array.Resize(ref encoded, written);
        return encoded;
    }
}
