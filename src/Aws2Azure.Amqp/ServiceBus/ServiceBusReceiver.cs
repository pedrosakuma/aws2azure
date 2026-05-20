using System.Collections.Concurrent;
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
    private readonly ConcurrentDictionary<Guid, ServiceBusReceivedMessage> _inFlight = new();
    // Serialises concurrent ReceiveBatchAsync callers so the credit
    // top-up computation in AmqpLink (read of _linkCredit followed by
    // GrantCreditAsync) is not raced when several SQS callers hit the
    // same pooled receiver simultaneously.
    private readonly SemaphoreSlim _receiveGate = new(1, 1);
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
    /// In-flight deliveries that have been handed to a caller but are not
    /// yet settled, keyed by the 16-byte delivery-tag (lock-token) GUID.
    /// Used by the lock-token-only settlement overloads so a stateless
    /// HTTP request (e.g. SQS <c>DeleteMessage</c>) can settle a
    /// previously-received message without holding a reference to the
    /// original <see cref="ServiceBusReceivedMessage"/>.
    /// <para>
    /// Entries are added on a successful
    /// <see cref="ReceiveBatchAsync"/> for messages whose tag is exactly
    /// 16 bytes (the SB lock-token convention) and removed by
    /// <see cref="CompleteAsync(Guid, CancellationToken)"/> /
    /// <see cref="AbandonAsync(Guid, CancellationToken)"/> /
    /// <see cref="DeadLetterAsync(Guid, string?, string?, CancellationToken)"/>.
    /// Cap: callers must settle (or invalidate the receiver) within the
    /// queue's lock duration; this slice does no time-based eviction.
    /// </para>
    /// </summary>
    public int InFlightCount => _inFlight.Count;

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
        await _receiveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var deliveries = await _link.ReceiveBatchAsync(maxMessages, maxWait, cancellationToken).ConfigureAwait(false);
            if (deliveries.Count == 0) return Array.Empty<ServiceBusReceivedMessage>();
            var result = new ServiceBusReceivedMessage[deliveries.Count];
            for (var i = 0; i < deliveries.Count; i++)
            {
                var msg = new ServiceBusReceivedMessage(deliveries[i]);
                result[i] = msg;
                // Register only deliveries whose tag matches the SB
                // lock-token convention (16 bytes ↔ GUID). Sender-settled
                // deliveries or peers that use a non-GUID tag are still
                // returned to the caller but cannot be looked up later.
                if (msg.LockToken is { } token)
                    _inFlight[token] = msg;
            }
            return result;
        }
        finally
        {
            _receiveGate.Release();
        }
    }

    /// <summary>
    /// Accepts the message — SB removes it. When the message carries a
    /// lock-token, settlement is gated by an atomic <c>TryRemove</c> so a
    /// concurrent call against the same delivery (via the lock-token
    /// overload or another message-instance call) cannot double-settle.
    /// </summary>
    public Task CompleteAsync(ServiceBusReceivedMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ThrowIfDisposed();
        if (message.LockToken is { } token && !_inFlight.TryRemove(token, out _))
            return Task.CompletedTask;
        return _link.AcceptAsync(message.Delivery, cancellationToken);
    }

    /// <summary>
    /// Lock-token-only overload. Looks the delivery up in the in-flight
    /// cache (populated by <see cref="ReceiveBatchAsync"/>) and accepts
    /// it. Returns <c>false</c> when the lock-token is unknown — either
    /// the message was already settled, the lock expired, or it came in
    /// over a different receiver instance.
    /// </summary>
    public async Task<bool> CompleteAsync(Guid lockToken, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!_inFlight.TryRemove(lockToken, out var message)) return false;
        await _link.AcceptAsync(message.Delivery, cancellationToken).ConfigureAwait(false);
        return true;
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
        if (message.LockToken is { } token && !_inFlight.TryRemove(token, out _))
            return Task.CompletedTask;
        return _link.ModifyAsync(message.Delivery, deliveryFailed: true, undeliverableHere: null, cancellationToken);
    }

    /// <summary>Lock-token-only overload (see <see cref="CompleteAsync(Guid, CancellationToken)"/>).</summary>
    public async Task<bool> AbandonAsync(Guid lockToken, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!_inFlight.TryRemove(lockToken, out var message)) return false;
        await _link.ModifyAsync(message.Delivery, deliveryFailed: true, undeliverableHere: null, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Dead-letters the message via the SB-specific rejected variant.
    /// <para>
    /// The <c>error.info</c> map is encoded with the well-known
    /// <c>DeadLetterReason</c> + <c>DeadLetterErrorDescription</c>
    /// symbol-keyed string entries; Service Bus copies them onto the
    /// dead-lettered message's application-properties so SDK clients
    /// reading off the DLQ see them as native fields. The same values
    /// are mirrored into <c>error.description</c> as a fallback for
    /// peers that ignore the info map.
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
        if (message.LockToken is { } token && !_inFlight.TryRemove(token, out _))
            return Task.CompletedTask;
        return RejectInternal(message.Delivery, reason, description, cancellationToken);
    }

    /// <summary>Lock-token-only overload (see <see cref="CompleteAsync(Guid, CancellationToken)"/>).</summary>
    public async Task<bool> DeadLetterAsync(
        Guid lockToken,
        string? reason = null,
        string? description = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!_inFlight.TryRemove(lockToken, out var message)) return false;
        await RejectInternal(message.Delivery, reason, description, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private Task RejectInternal(
        AmqpIncomingDelivery delivery, string? reason, string? description, CancellationToken cancellationToken)
    {
        // Primary channel for the values is the typed fields map on
        // error.info (slice 8c.4) — SB reads from there. Mirror into
        // error.description as a fallback for peers that don't decode
        // the info map.
        var fallback = (reason, description) switch
        {
            ({ } r, { } d) => r + ": " + d,
            ({ } r, null) => r,
            (null, { } d) => d,
            _ => null,
        };
        var error = new AmqpError
        {
            Condition = DeadLetterCondition,
            Description = fallback,
            Info = ServiceBusDeadLetterInfo.Encode(reason, description),
        };
        return _link.RejectAsync(delivery, error, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _inFlight.Clear();
        try { await _link.DetachAsync(closed: true).ConfigureAwait(false); }
        catch { /* best-effort cleanup */ }
        _receiveGate.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(ServiceBusReceiver));
    }
}
