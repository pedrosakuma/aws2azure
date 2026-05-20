using Aws2Azure.Amqp.Connection;
using Aws2Azure.Amqp.Framing;

namespace Aws2Azure.Amqp.ServiceBus;

/// <summary>
/// Service Bus AMQP receiver wrapping a single <see cref="AmqpLink"/> in
/// receiver role. The wrapper translates SB-shaped settlement semantics
/// onto the AMQP outcomes:
/// <list type="bullet">
///   <item><b>Complete</b> → <c>accepted</c> — SB removes the message.</item>
///   <item><b>Abandon</b> → <c>modified{delivery-failed=true}</c> —
///   SB releases the lock and bumps <c>delivery-count</c>; the message
///   becomes re-deliverable after the queue's lock duration.</item>
///   <item><b>DeadLetter</b> → <c>rejected</c> with the SB-specific
///   <c>com.microsoft:dead-letter</c> error condition and reason /
///   description carried in <c>error.info</c>.</item>
/// </list>
/// <para>
/// Disposed when the parent connection is disposed (the link is detached
/// transitively); callers may also dispose explicitly to release the
/// link without closing the connection.
/// </para>
/// </summary>
internal sealed class ServiceBusReceiver : IAsyncDisposable
{
    /// <summary>
    /// AMQP error condition Service Bus expects on a <c>rejected</c>
    /// disposition that should route the message to the entity's DLQ.
    /// </summary>
    private const string DeadLetterCondition = "com.microsoft:dead-letter";

    private readonly AmqpLink _link;
    private int _disposed;

    internal ServiceBusReceiver(AmqpLink link, string queueName)
    {
        _link = link;
        QueueName = queueName;
    }

    public string QueueName { get; }

    /// <summary>The underlying receiver link. Exposed for diagnostics / advanced flow control.</summary>
    internal AmqpLink Link => _link;

    /// <summary>
    /// Receives up to <paramref name="maxMessages"/> messages, blocking
    /// at most <paramref name="maxWait"/>. Returns whatever has been
    /// delivered when either cap fires; an empty result means the
    /// wait elapsed.
    /// </summary>
    public async Task<IReadOnlyList<ServiceBusReceivedMessage>> ReceiveBatchAsync(
        int maxMessages,
        TimeSpan maxWait,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var deliveries = await _link.ReceiveBatchAsync(maxMessages, maxWait, cancellationToken).ConfigureAwait(false);
        if (deliveries.Count == 0) return Array.Empty<ServiceBusReceivedMessage>();
        var result = new ServiceBusReceivedMessage[deliveries.Count];
        for (var i = 0; i < deliveries.Count; i++)
            result[i] = new ServiceBusReceivedMessage(deliveries[i]);
        return result;
    }

    /// <summary>Accepts the message — SB removes it.</summary>
    public Task CompleteAsync(ServiceBusReceivedMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ThrowIfDisposed();
        return _link.AcceptAsync(message.Delivery, cancellationToken);
    }

    /// <summary>
    /// Releases the lock and increments delivery-count so SB redelivers
    /// after the lock duration. Maps to AMQP <c>modified</c> with
    /// <c>delivery-failed=true</c> (SB's canonical "abandon" outcome
    /// for clients that want delivery-count to advance on each retry).
    /// </summary>
    public Task AbandonAsync(ServiceBusReceivedMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ThrowIfDisposed();
        return _link.ModifyAsync(message.Delivery, deliveryFailed: true, undeliverableHere: null, cancellationToken);
    }

    /// <summary>
    /// Dead-letters the message via the SB-specific rejected variant.
    /// <para>
    /// <b>Slice 8a limitation:</b> Service Bus normally surfaces
    /// <c>DeadLetterReason</c> / <c>DeadLetterErrorDescription</c> on the
    /// DLQ copy by reading them from the <c>error.info</c> map on the
    /// rejected disposition. Encoding a typed AMQP map into that opaque
    /// field is deferred to Slice 8c (<c>ChangeMessageVisibility</c> +
    /// error mapping). Until then, only <paramref name="description"/> is
    /// carried — via the <c>error.description</c> string, which SB
    /// preserves on the dead-lettered message's
    /// <c>x-opt-deadletter-description</c> annotation.
    /// </para>
    /// </summary>
    public Task DeadLetterAsync(
        ServiceBusReceivedMessage message,
        string? reason = null,
        string? description = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ThrowIfDisposed();

        // reason is intentionally collapsed into the description until
        // Slice 8c can encode it into the AMQP error.info map. Surface it
        // anyway so callers don't lose information.
        var combined = (reason, description) switch
        {
            ({ } r, { } d) => r + ": " + d,
            ({ } r, null) => r,
            (null, { } d) => d,
            _ => null,
        };

        var error = new AmqpError
        {
            Condition = DeadLetterCondition,
            Description = combined,
        };
        return _link.RejectAsync(message.Delivery, error, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { await _link.DetachAsync(closed: true).ConfigureAwait(false); }
        catch { /* best-effort cleanup */ }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(ServiceBusReceiver));
    }
}
