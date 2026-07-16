using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using Aws2Azure.Amqp.Codec;
using Aws2Azure.Amqp.Connection;
using Aws2Azure.Amqp.Framing;
using Aws2Azure.Amqp.ServiceBus;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Modules.Kinesis.ShardIterators;

namespace Aws2Azure.Modules.Kinesis.EventHubsAmqp;

public interface IEventHubsAmqpReceiver
{
    Task<EventHubsReceiveResult> ReceiveAsync(
        EventHubsCredentials credentials,
        string namespaceFqdn,
        string entityPath,
        string consumerGroup,
        int partitionId,
        string iteratorId,
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
    private static readonly TimeSpan EventHubsTailWait = TimeSpan.FromMilliseconds(25);

    private readonly EventHubsAmqpConnectionPool _connectionPool;
    private readonly ConcurrentDictionary<ReceiverKey, ReceiverSlot> _receivers = new();
    private int _disposed;

    public EventHubsAmqpReceiver(EventHubsAmqpConnectionPool connectionPool)
    {
        ArgumentNullException.ThrowIfNull(connectionPool);
        _connectionPool = connectionPool;
    }

    public async Task<EventHubsReceiveResult> ReceiveAsync(
        EventHubsCredentials credentials,
        string namespaceFqdn,
        string entityPath,
        string consumerGroup,
        int partitionId,
        string iteratorId,
        EventHubsReceivePosition position,
        int maxMessages,
        TimeSpan quiescentTimeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceFqdn);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerGroup);
        ArgumentNullException.ThrowIfNull(iteratorId);
        ArgumentNullException.ThrowIfNull(position);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxMessages, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(quiescentTimeout, TimeSpan.Zero);
        ThrowIfDisposed();

        var receiverAddress = BuildReceiverAddress(entityPath, consumerGroup, partitionId);
        var lease = await _connectionPool
            .GetConnectionAsync(credentials, namespaceFqdn, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            var audience = EventHubsAmqpEndpointResolver.BuildAudience(namespaceFqdn, receiverAddress);
            var receiverKey = new ReceiverKey(lease.Key, receiverAddress, iteratorId);
            await EvictIdleReceiversAsync().ConfigureAwait(false);
            var receiverSlot = GetOrCreateReservedReceiverSlot(receiverKey);

            var messages = new List<EventHubsReceivedMessage>(Math.Min(maxMessages, 256));
            try
            {
                var batch = await receiverSlot.ExecuteReceiveAsync(
                        lease.Connection,
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
            finally
            {
                receiverSlot.ReleaseReservation();
            }

            return new EventHubsReceiveResult(messages);
        }
        catch (Exception ex) when (TryWrap(ex, out var wrapped))
        {
            // The receiver slot is already evicted above. Tear down the SHARED
            // Event Hubs connection only for connection-fatal classes; sender
            // (PutRecord) links now share this connection, so a link-level /
            // transient / throttled GetRecords failure must not drop them.
            // Mirrors EventHubsAmqpSender.InvalidateOnFailureAsync.
            if (wrapped.Kind is EventHubsAmqpFailureKind.Auth
                or EventHubsAmqpFailureKind.ServerFatal
                or EventHubsAmqpFailureKind.Redirect)
            {
                await InvalidateConnectionAsync(lease.Key).ConfigureAwait(false);
            }

            throw wrapped;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        while (!_receivers.IsEmpty)
        {
            foreach (var entry in _receivers.ToArray())
            {
                if (_receivers.TryRemove(entry))
                {
                    await entry.Value.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }

    private ReceiverSlot GetOrCreateReservedReceiverSlot(ReceiverKey key)
    {
        ThrowIfDisposed();
        while (true)
        {
            var slot = _receivers.GetOrAdd(key, static _ => new ReceiverSlot());
            if (slot.TryReserve())
            {
                return slot;
            }

            Thread.Yield();
        }
    }

    private async Task EvictIdleReceiversAsync()
    {
        var now = Environment.TickCount64;
        foreach (var entry in _receivers.ToArray())
        {
            if (!entry.Value.TryBeginIdleEviction(now, ShardIteratorTokenCodec.MaxAgeSeconds * 1000L))
            {
                continue;
            }

            if (((ICollection<KeyValuePair<ReceiverKey, ReceiverSlot>>)_receivers).Remove(entry))
            {
                await entry.Value.DisposeAsync().ConfigureAwait(false);
            }
        }
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

        var kvp = new KeyValuePair<ReceiverKey, ReceiverSlot>(key, expected);
        if (((ICollection<KeyValuePair<ReceiverKey, ReceiverSlot>>)_receivers).Remove(kvp))
        {
            await expected.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task InvalidateConnectionAsync(EventHubsAmqpConnectionKey key)
    {
        foreach (var entry in _receivers.ToArray())
        {
            if (entry.Key.Connection.Equals(key) && _receivers.TryRemove(entry))
            {
                await entry.Value.DisposeAsync().ConfigureAwait(false);
            }
        }

        await _connectionPool.InvalidateConnectionAsync(key).ConfigureAwait(false);
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

    internal static bool TryWrap(Exception exception, out EventHubsAmqpException wrapped)
        => EventHubsAmqpExceptionMapper.TryWrap(exception, EventHubsAmqpOperation.Receive, out wrapped);

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(EventHubsAmqpReceiver));
        }
    }

    private readonly record struct ReceiverKey(
        EventHubsAmqpConnectionKey Connection,
        string ReceiverAddress,
        string IteratorId)
    {
        public bool Equals(ReceiverKey other)
            => Connection.Equals(other.Connection)
                && string.Equals(ReceiverAddress, other.ReceiverAddress, StringComparison.OrdinalIgnoreCase)
                && string.Equals(IteratorId, other.IteratorId, StringComparison.Ordinal);

        public override int GetHashCode()
            => HashCode.Combine(
                Connection,
                StringComparer.OrdinalIgnoreCase.GetHashCode(ReceiverAddress),
                StringComparer.Ordinal.GetHashCode(IteratorId));
    }

    private sealed class ReceiverSlot : IAsyncDisposable
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly SemaphoreSlim _receiveGate = new(1, 1);
        private ServiceBusReceiver? _receiver;
        private long _lastUsedTick = Environment.TickCount64;
        private int _reservations;
        private int _evicting;
        private int _disposed;

        public bool TryReserve()
        {
            if (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _evicting) != 0)
            {
                return false;
            }

            Interlocked.Increment(ref _reservations);
            if (Volatile.Read(ref _disposed) == 0 && Volatile.Read(ref _evicting) == 0)
            {
                Volatile.Write(ref _lastUsedTick, Environment.TickCount64);
                return true;
            }

            Interlocked.Decrement(ref _reservations);
            return false;
        }

        public void ReleaseReservation()
        {
            Volatile.Write(ref _lastUsedTick, Environment.TickCount64);
            Interlocked.Decrement(ref _reservations);
        }

        public bool TryBeginIdleEviction(long now, long idleMilliseconds)
        {
            if (unchecked(now - Volatile.Read(ref _lastUsedTick)) < idleMilliseconds
                || Interlocked.CompareExchange(ref _evicting, 1, 0) != 0)
            {
                return false;
            }

            if (Volatile.Read(ref _reservations) == 0)
            {
                return true;
            }

            Volatile.Write(ref _evicting, 0);
            return false;
        }

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
}

internal static class EventHubsSelectorFilter
{
    private const string FilterSymbol = "apache.org:selector-filter:string";
    private const int StackallocThreshold = 1024;

    public static ReadOnlyMemory<byte> Encode(string expression)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);

        var capacity = (FilterSymbol.Length * 2) + (expression.Length * 4) + 32;
        if (capacity <= StackallocThreshold)
        {
            Span<byte> stackElements = stackalloc byte[capacity];
            return Encode(expression, stackElements);
        }

        var rented = ArrayPool<byte>.Shared.Rent(capacity);
        try
        {
            return Encode(expression, rented.AsSpan(0, capacity));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static ReadOnlyMemory<byte> Encode(string expression, Span<byte> elements)
    {
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
