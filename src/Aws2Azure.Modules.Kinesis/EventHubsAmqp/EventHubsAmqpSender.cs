using System.Collections.Concurrent;
using Aws2Azure.Amqp.Connection;
using Aws2Azure.Amqp.Framing;
using Aws2Azure.Amqp.ServiceBus;
using Aws2Azure.Core.Configuration;

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

internal sealed class EventHubsAmqpSender : IEventHubsAmqpSender, IAsyncDisposable
{
    private readonly EventHubsAmqpConnectionPool _connectionPool;
    private readonly ConcurrentDictionary<SenderKey, ResourceSlot<ServiceBusAmqpSender>> _senders = new();
    private int _disposed;

    public EventHubsAmqpSender(EventHubsAmqpConnectionPool connectionPool)
    {
        ArgumentNullException.ThrowIfNull(connectionPool);
        _connectionPool = connectionPool;
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

        var lease = await _connectionPool
            .GetConnectionAsync(credentials, namespaceFqdn, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            var sender = await GetOrCreateSenderAsync(
                    lease,
                    entityPath,
                    EventHubsAmqpEndpointResolver.BuildAudience(namespaceFqdn, entityPath),
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
            await InvalidateOnFailureAsync(lease.Key, entityPath, wrapped).ConfigureAwait(false);
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

        var lease = await _connectionPool
            .GetConnectionAsync(credentials, namespaceFqdn, cancellationToken)
            .ConfigureAwait(false);

        var outcomes = new EventHubsBatchSendOutcome[messages.Count];
        try
        {
            var sender = await GetOrCreateSenderAsync(
                    lease,
                    entityPath,
                    EventHubsAmqpEndpointResolver.BuildAudience(namespaceFqdn, entityPath),
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
                        sendException.Outcome switch
                        {
                            AmqpDispositionOutcome.Released => EventHubsAmqpFailureKind.Transient,
                            AmqpDispositionOutcome.Modified => EventHubsAmqpFailureKind.Throttled,
                            _ => EventHubsAmqpFailureKind.Unknown,
                        }));
                }
                catch (Exception ex) when (TryWrap(ex, out var wrapped))
                {
                    await InvalidateOnFailureAsync(lease.Key, entityPath, wrapped).ConfigureAwait(false);
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
            await InvalidateOnFailureAsync(lease.Key, entityPath, wrapped).ConfigureAwait(false);
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

        await DrainSendersAsync().ConfigureAwait(false);
    }

    private async Task<ServiceBusAmqpSender> GetOrCreateSenderAsync(
        EventHubsAmqpConnectionLease lease,
        string entityPath,
        string audience,
        CancellationToken cancellationToken)
    {
        var key = new SenderKey(lease.Key, entityPath);
        while (true)
        {
            var slot = await GetOrPublishSenderSlotAsync(key).ConfigureAwait(false);
            try
            {
                return await slot
                    .GetOrCreateAsync(
                        new SenderOpenRequest(lease.Connection, entityPath, audience),
                        static (request, ct) => request.Connection.OpenSenderAsync(request.EntityPath, request.Audience, ct),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (ObjectDisposedException) when (Volatile.Read(ref _disposed) == 0 && slot.IsDisposed)
            {
                _senders.TryRemove(KeyValuePair.Create(key, slot));
            }
        }
    }

    private async ValueTask<ResourceSlot<ServiceBusAmqpSender>> GetOrPublishSenderSlotAsync(SenderKey key)
    {
        if (_senders.TryGetValue(key, out var existing))
        {
            ThrowIfDisposed();
            return existing;
        }

        var fresh = new ResourceSlot<ServiceBusAmqpSender>(nameof(EventHubsAmqpSender), static sender => sender.IsClosed);
        var slot = _senders.GetOrAdd(key, fresh);
        if (ReferenceEquals(slot, fresh) && Volatile.Read(ref _disposed) != 0)
        {
            _senders.TryRemove(KeyValuePair.Create(key, slot));
            await slot.DisposeAsync().ConfigureAwait(false);
            throw new ObjectDisposedException(nameof(EventHubsAmqpSender));
        }

        return slot;
    }

    private async Task InvalidateSenderAsync(EventHubsAmqpConnectionKey connectionKey, string entityPath)
    {
        if (_senders.TryRemove(new SenderKey(connectionKey, entityPath), out var slot))
        {
            await slot.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task InvalidateConnectionAsync(EventHubsAmqpConnectionKey key)
    {
        foreach (var entry in _senders.ToArray())
        {
            if (entry.Key.Connection.Equals(key) && _senders.TryRemove(entry))
            {
                await entry.Value.DisposeAsync().ConfigureAwait(false);
            }
        }

        await _connectionPool.InvalidateConnectionAsync(key).ConfigureAwait(false);
    }

    private async Task DrainSendersAsync()
    {
        while (!_senders.IsEmpty)
        {
            foreach (var entry in _senders.ToArray())
            {
                if (_senders.TryRemove(entry))
                {
                    await entry.Value.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }

    private async Task InvalidateOnFailureAsync(EventHubsAmqpConnectionKey key, string entityPath, EventHubsAmqpException failure)
    {
        switch (failure.Kind)
        {
            case EventHubsAmqpFailureKind.Auth:
            case EventHubsAmqpFailureKind.ServerFatal:
            case EventHubsAmqpFailureKind.Redirect:
                await InvalidateConnectionAsync(key).ConfigureAwait(false);
                return;
            default:
                await InvalidateSenderAsync(key, entityPath).ConfigureAwait(false);
                return;
        }
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

    internal static bool TryWrap(Exception exception, out EventHubsAmqpException wrapped)
        => EventHubsAmqpExceptionMapper.TryWrap(exception, EventHubsAmqpOperation.Send, out wrapped);

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(EventHubsAmqpSender));
        }
    }

    private readonly record struct SenderKey(EventHubsAmqpConnectionKey Connection, string EntityPath)
    {
        public bool Equals(SenderKey other)
            => Connection.Equals(other.Connection)
                && string.Equals(EntityPath, other.EntityPath, StringComparison.OrdinalIgnoreCase);

        public override int GetHashCode()
            => HashCode.Combine(Connection, StringComparer.OrdinalIgnoreCase.GetHashCode(EntityPath));
    }

    private readonly record struct SenderOpenRequest(
        ServiceBusAmqpConnection Connection,
        string EntityPath,
        string Audience);
}
