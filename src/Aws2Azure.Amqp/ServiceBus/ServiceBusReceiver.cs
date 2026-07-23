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
    internal const int MaxTrackedInFlight = 1024;

    /// <summary>
    /// AMQP error condition Service Bus expects on a <c>rejected</c>
    /// disposition that should route the message to the entity's DLQ.
    /// </summary>
    private const string DeadLetterCondition = "com.microsoft:dead-letter";

    private readonly AmqpLink _link;
    private readonly ConcurrentDictionary<Guid, InFlightDelivery> _inFlight = new();
    private int _trackedInFlight;
    private long _sessionLockExpiryUtcTicks;
    private int _disposed;

    internal ServiceBusReceiver(AmqpLink link, string queueName)
        : this(link, queueName, sessionId: null) { }

    internal ServiceBusReceiver(AmqpLink link, string queueName, string? sessionId)
    {
        _link = link;
        QueueName = queueName;
        SessionId = sessionId;
    }

    public string QueueName { get; }

    /// <summary>
    /// When the receiver was opened against a Service Bus session
    /// (slice 7 — <c>OpenSessionReceiverAsync</c>), the session-id the
    /// link is bound to. Populated from the broker's echoed
    /// <c>com.microsoft:session-filter</c> on the attach response —
    /// distinct from the value the caller requested (which may have
    /// been <c>null</c> meaning "any available session"). <c>null</c>
    /// for non-session receivers.
    /// </summary>
    public string? SessionId { get; }

    /// <summary>The underlying receiver link. Exposed for diagnostics / advanced flow control.</summary>
    internal AmqpLink Link => _link;

    /// <summary>
    /// True when the underlying AMQP link is no longer attached.
    /// Pool slots check this to evict and rebuild before reuse.
    /// </summary>
    public bool IsClosed => _link.IsClosed;

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
    /// Entries are opportunistically pruned after their broker lock expires
    /// and the receiver reserves at most <see cref="MaxTrackedInFlight"/>
    /// delivery slots, bounding state even when clients abandon receipt
    /// handles without settlement.
    /// </para>
    /// </summary>
    public int InFlightCount => Volatile.Read(ref _trackedInFlight);

    public bool UpdateLockExpiry(Guid lockToken, DateTimeOffset expiresAt)
    {
        if (!_inFlight.TryGetValue(lockToken, out var tracked))
            return false;
        return tracked.TryUpdateExpiry(_inFlight, lockToken, expiresAt);
    }

    public bool ContainsLockToken(Guid lockToken)
    {
        PruneExpired(DateTimeOffset.UtcNow);
        return _inFlight.ContainsKey(lockToken);
    }

    internal LockRenewalLease? TryBeginLockRenewal(Guid lockToken)
    {
        PruneExpired(DateTimeOffset.UtcNow);
        if (!_inFlight.TryGetValue(lockToken, out var tracked) || !tracked.TryClaim())
            return null;
        if (!_inFlight.TryGetValue(lockToken, out var current) || !ReferenceEquals(current, tracked))
        {
            tracked.ReleaseClaim();
            return null;
        }
        return new LockRenewalLease(this, lockToken, tracked);
    }

    public bool UpdateSessionLockExpiry(Guid requiredLockToken, DateTimeOffset expiresAt)
    {
        if (!UpdateLockExpiry(requiredLockToken, expiresAt))
            return false;
        foreach (var tracked in _inFlight.Values)
            tracked.TryUpdateExpiry(expiresAt);
        return true;
    }

    /// <summary>
    /// Receives up to <paramref name="maxMessages"/> messages, blocking
    /// at most <paramref name="maxWait"/>. Returns whatever has been
    /// delivered when either cap fires; an empty result means the
    /// wait elapsed.
    /// <para>
    /// Safe for concurrent callers on the same receiver: the underlying
    /// <see cref="AmqpLink.ReceiveBatchAsync"/> serialises the
    /// credit-accounting critical section under <c>_deliveryLock</c>
    /// (the read-then-grant computation is atomic) and the buffered
    /// <c>Channel&lt;T&gt;</c> drain is MPMC-safe — concurrent callers
    /// may each compute an independent <c>toGrant</c>, leading to
    /// harmless credit overshoot (the broker may send a few extra
    /// messages, drained by whichever caller wins the <c>TryRead</c>),
    /// but no message is lost, duplicated or double-settled — the
    /// in-flight cache below is a <see cref="ConcurrentDictionary{TKey, TValue}"/>
    /// keyed by lock-token which is unique per delivery. The serialising
    /// <c>SemaphoreSlim</c> that used to wrap this method was removed
    /// in #136 because it serialised c=16 SQS receivers down to a
    /// single in-flight call, capping receive-side throughput at ~92 cps
    /// against a SDK baseline of ~340 cps on the same emulator.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<ServiceBusReceivedMessage>> ReceiveBatchAsync(
        int maxMessages,
        TimeSpan maxWait,
        TimeSpan? tailWait = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        PruneExpired(DateTimeOffset.UtcNow);
        var reserved = ReserveSlots(maxMessages);
        if (reserved == 0) return Array.Empty<ServiceBusReceivedMessage>();

        IReadOnlyList<AmqpIncomingDelivery> deliveries;
        try
        {
            deliveries = await _link
                .ReceiveBatchAsync(reserved, maxWait, tailWait, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            ReleaseSlots(reserved);
            throw;
        }
        if (Volatile.Read(ref _disposed) != 0)
        {
            ReleaseSlots(reserved);
            throw new ObjectDisposedException(nameof(ServiceBusReceiver));
        }
        if (deliveries.Count == 0)
        {
            ReleaseSlots(reserved);
            return Array.Empty<ServiceBusReceivedMessage>();
        }
        var result = new ServiceBusReceivedMessage[deliveries.Count];
        var retained = 0;
        for (var i = 0; i < deliveries.Count; i++)
        {
            var msg = new ServiceBusReceivedMessage(deliveries[i]);
            result[i] = msg;
            // Register only deliveries whose tag matches the SB
            // lock-token convention (16 bytes ↔ GUID). Sender-settled
            // deliveries or peers that use a non-GUID tag are still
            // returned to the caller but cannot be looked up later.
            if (msg.LockToken is { } token)
            {
                var expiresAt = msg.Annotations?.LockedUntil ?? DateTimeOffset.UtcNow.AddMinutes(5);
                if (_inFlight.TryAdd(token, new InFlightDelivery(msg, expiresAt)))
                    retained++;
            }
        }
        ReleaseSlots(reserved - retained);
        return result;
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
        if (message.LockToken is { } token)
            return SettleAsync(token, message.Delivery, static (link, delivery, ct) =>
                link.AcceptAsync(delivery, ct), cancellationToken);
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
        return await SettleAsync(
            lockToken,
            static (link, delivery, ct) => link.AcceptAsync(delivery, ct),
            cancellationToken).ConfigureAwait(false);
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
        if (message.LockToken is { } token)
            return SettleAsync(token, message.Delivery, static (link, delivery, ct) =>
                link.ModifyAsync(delivery, deliveryFailed: true, undeliverableHere: null, ct), cancellationToken);
        return _link.ModifyAsync(message.Delivery, deliveryFailed: true, undeliverableHere: null, cancellationToken);
    }

    /// <summary>Lock-token-only overload (see <see cref="CompleteAsync(Guid, CancellationToken)"/>).</summary>
    public async Task<bool> AbandonAsync(Guid lockToken, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return await SettleAsync(
            lockToken,
            static (link, delivery, ct) =>
                link.ModifyAsync(delivery, deliveryFailed: true, undeliverableHere: null, ct),
            cancellationToken).ConfigureAwait(false);
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
        if (message.LockToken is { } token)
            return DeadLetterTrackedAsync(token, reason, description, cancellationToken);
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
        if (!_inFlight.TryGetValue(lockToken, out var tracked) || !tracked.TryClaim())
            return false;
        try
        {
            await RejectInternal(tracked.Message.Delivery, reason, description, cancellationToken).ConfigureAwait(false);
            RemoveTracked(lockToken, tracked);
            return true;
        }
        catch
        {
            tracked.ReleaseClaim();
            throw;
        }
    }

    private Task RejectInternal(
        AmqpIncomingDelivery delivery, string? reason, string? description, CancellationToken cancellationToken)
    {
        // Service Bus reads DeadLetterReason / DeadLetterErrorDescription
        // off the typed fields map on error.info (slice 8c.4). When the
        // info map carries the values we deliberately leave
        // error.description unset so the payload isn't duplicated (the
        // current AmqpError.Write path uses a fixed 4 KiB scratch
        // buffer, and a multi-KiB description would otherwise blow it).
        // When neither field is supplied we still emit the bare condition.
        var info = ServiceBusDeadLetterInfo.Encode(reason, description);
        string? fallbackDescription = null;
        if (info.IsEmpty)
        {
            fallbackDescription = (reason, description) switch
            {
                ({ } r, { } d) => r + ": " + d,
                ({ } r, null) => r,
                (null, { } d) => d,
                _ => null,
            };
        }
        var error = new AmqpError
        {
            Condition = DeadLetterCondition,
            Description = fallbackDescription,
            Info = info,
        };
        return _link.RejectAsync(delivery, error, cancellationToken);
    }

    private async Task<bool> SettleAsync(
        Guid lockToken,
        Func<AmqpLink, AmqpIncomingDelivery, CancellationToken, Task> settle,
        CancellationToken cancellationToken)
    {
        if (!_inFlight.TryGetValue(lockToken, out var tracked) || !tracked.TryClaim())
            return false;
        try
        {
            await settle(_link, tracked.Message.Delivery, cancellationToken).ConfigureAwait(false);
            RemoveTracked(lockToken, tracked);
            return true;
        }
        catch
        {
            tracked.ReleaseClaim();
            throw;
        }
    }

    private async Task DeadLetterTrackedAsync(
        Guid lockToken,
        string? reason,
        string? description,
        CancellationToken cancellationToken)
    {
        if (!_inFlight.TryGetValue(lockToken, out var tracked) || !tracked.TryClaim())
            return;
        try
        {
            await RejectInternal(tracked.Message.Delivery, reason, description, cancellationToken)
                .ConfigureAwait(false);
            RemoveTracked(lockToken, tracked);
        }
        catch
        {
            tracked.ReleaseClaim();
            throw;
        }
    }

    private Task SettleAsync(
        Guid lockToken,
        AmqpIncomingDelivery _,
        Func<AmqpLink, AmqpIncomingDelivery, CancellationToken, Task> settle,
        CancellationToken cancellationToken)
    {
        if (!_inFlight.TryGetValue(lockToken, out var tracked) || !tracked.TryClaim())
            return Task.CompletedTask;
        return SettleClaimedAsync(lockToken, tracked, settle, cancellationToken);
    }

    private async Task SettleClaimedAsync(
        Guid lockToken,
        InFlightDelivery tracked,
        Func<AmqpLink, AmqpIncomingDelivery, CancellationToken, Task> settle,
        CancellationToken cancellationToken)
    {
        try
        {
            await settle(_link, tracked.Message.Delivery, cancellationToken).ConfigureAwait(false);
            RemoveTracked(lockToken, tracked);
        }
        catch
        {
            tracked.ReleaseClaim();
            throw;
        }
    }

    private int ReserveSlots(int requested)
    {
        while (true)
        {
            var current = Volatile.Read(ref _trackedInFlight);
            var available = MaxTrackedInFlight - current;
            if (available <= 0) return 0;
            var reserved = Math.Min(requested, available);
            if (Interlocked.CompareExchange(ref _trackedInFlight, current + reserved, current) == current)
                return reserved;
        }
    }

    private void ReleaseSlots(int count)
    {
        while (count > 0)
        {
            var current = Volatile.Read(ref _trackedInFlight);
            if (current == 0) return;
            var updated = Math.Max(0, current - count);
            if (Interlocked.CompareExchange(ref _trackedInFlight, updated, current) == current)
                return;
        }
    }

    private void RemoveTracked(Guid lockToken, InFlightDelivery tracked)
    {
        if (_inFlight.TryRemove(KeyValuePair.Create(lockToken, tracked)))
            ReleaseSlots(1);
    }

    private void PruneExpired(DateTimeOffset now)
    {
        foreach (var entry in _inFlight)
        {
            if (!IsExpired(entry.Value, now) || !entry.Value.TryClaim()) continue;
            if (!IsExpired(entry.Value, now))
            {
                entry.Value.ReleaseClaim();
                continue;
            }
            if (_inFlight.TryRemove(entry))
                ReleaseSlots(1);
            else
                entry.Value.ReleaseClaim();
        }
    }

    private bool IsExpired(InFlightDelivery delivery, DateTimeOffset now)
    {
        var expiresAtTicks = delivery.ExpiresAt.UtcTicks;
        expiresAtTicks = Math.Max(
            expiresAtTicks,
            Volatile.Read(ref _sessionLockExpiryUtcTicks));
        return expiresAtTicks <= now.UtcTicks;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _inFlight.Clear();
        Volatile.Write(ref _trackedInFlight, 0);
        try { await _link.DetachAsync(closed: true).ConfigureAwait(false); }
        catch { /* best-effort cleanup */ }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(ServiceBusReceiver));
    }

    internal sealed class InFlightDelivery(ServiceBusReceivedMessage message, DateTimeOffset expiresAt)
    {
        private int _claimed;
        private long _expiresAtUtcTicks = expiresAt.UtcTicks;

        public ServiceBusReceivedMessage Message { get; } = message;
        public DateTimeOffset ExpiresAt =>
            new(Volatile.Read(ref _expiresAtUtcTicks), TimeSpan.Zero);

        public bool TryClaim() => Interlocked.CompareExchange(ref _claimed, 1, 0) == 0;
        public void ReleaseClaim() => Volatile.Write(ref _claimed, 0);
        public void UpdateClaimedExpiry(DateTimeOffset value) =>
            Volatile.Write(ref _expiresAtUtcTicks, value.UtcTicks);
        public bool TryUpdateExpiry(DateTimeOffset value)
        {
            if (!TryClaim()) return false;
            Volatile.Write(ref _expiresAtUtcTicks, value.UtcTicks);
            ReleaseClaim();
            return true;
        }

        public bool TryExtendExpiry(DateTimeOffset value)
        {
            if (!TryClaim()) return false;
            if (ExpiresAt < value)
                Volatile.Write(ref _expiresAtUtcTicks, value.UtcTicks);
            ReleaseClaim();
            return true;
        }

        public bool TryUpdateExpiry(
            ConcurrentDictionary<Guid, InFlightDelivery> owner,
            Guid lockToken,
            DateTimeOffset value)
        {
            if (!TryClaim()) return false;
            try
            {
                if (!owner.TryGetValue(lockToken, out var current) || !ReferenceEquals(current, this))
                    return false;
                Volatile.Write(ref _expiresAtUtcTicks, value.UtcTicks);
                return true;
            }
            finally
            {
                ReleaseClaim();
            }
        }

    }

    internal sealed class LockRenewalLease : IDisposable
    {
        private readonly ServiceBusReceiver _owner;
        private readonly Guid _lockToken;
        private InFlightDelivery? _tracked;

        internal LockRenewalLease(
            ServiceBusReceiver owner,
            Guid lockToken,
            InFlightDelivery tracked)
        {
            _owner = owner;
            _lockToken = lockToken;
            _tracked = tracked;
        }

        public bool Complete(DateTimeOffset expiresAt, bool updateSession)
        {
            var tracked = _tracked;
            if (tracked is null
                || !_owner._inFlight.TryGetValue(_lockToken, out var current)
                || !ReferenceEquals(current, tracked))
            {
                return false;
            }

            if (updateSession)
            {
                var requestedTicks = expiresAt.UtcTicks;
                long effectiveTicks;
                while (true)
                {
                    var currentTicks = Volatile.Read(ref _owner._sessionLockExpiryUtcTicks);
                    effectiveTicks = Math.Max(currentTicks, requestedTicks);
                    if (effectiveTicks == currentTicks
                        || Interlocked.CompareExchange(
                            ref _owner._sessionLockExpiryUtcTicks,
                            effectiveTicks,
                            currentTicks) == currentTicks)
                    {
                        break;
                    }
                }
                expiresAt = new DateTimeOffset(effectiveTicks, TimeSpan.Zero);
                tracked.UpdateClaimedExpiry(expiresAt);
                foreach (var other in _owner._inFlight.Values)
                {
                    if (!ReferenceEquals(other, tracked))
                        other.TryExtendExpiry(expiresAt);
                }
            }
            else
            {
                tracked.UpdateClaimedExpiry(expiresAt);
            }
            return true;
        }

        public void Dispose()
        {
            var tracked = Interlocked.Exchange(ref _tracked, null);
            tracked?.ReleaseClaim();
        }
    }
}
