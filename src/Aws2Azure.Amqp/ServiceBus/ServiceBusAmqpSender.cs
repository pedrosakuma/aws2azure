using Aws2Azure.Amqp.Connection;
using Aws2Azure.Amqp.Framing;

namespace Aws2Azure.Amqp.ServiceBus;

/// <summary>
/// Service Bus AMQP sender wrapping a single <see cref="AmqpLink"/> in
/// sender role. Mirrors <see cref="ServiceBusReceiver"/>: the wrapper
/// owns the link lifecycle and exposes a high-level
/// <see cref="SendAsync"/> that handles the AMQP transfer + disposition
/// flow.
///
/// <para>By default sends are <b>unsettled</b> — the broker is expected
/// to settle each delivery with an <c>accepted</c> outcome before
/// <see cref="SendAsync"/> completes. A <c>rejected</c> outcome surfaces
/// as <see cref="ServiceBusSendException"/> carrying the broker's
/// error condition / description, so SQS handlers can translate the
/// failure into the appropriate AWS error code.</para>
///
/// <para>Disposed when the parent connection is disposed (the link is
/// detached transitively); callers may also dispose explicitly to
/// release the link without closing the connection.</para>
/// </summary>
internal sealed class ServiceBusAmqpSender : IAsyncDisposable
{
    private readonly AmqpLink _link;
    // Serialises concurrent SendAsync callers so the delivery-id
    // allocation + the transfer write are atomic from the perspective of
    // the wire: two sends on the same sender link must not interleave
    // their multi-frame transfer fragments. AmqpLink already takes the
    // connection write-lock per frame, but the delivery-id sequence is
    // per-link so the gate guarantees disposition cookies match outcomes.
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private int _disposed;

    internal ServiceBusAmqpSender(AmqpLink link, string queueName)
    {
        _link = link;
        QueueName = queueName;
    }

    public string QueueName { get; }

    /// <summary>The underlying sender link. Exposed for diagnostics.</summary>
    internal AmqpLink Link => _link;

    /// <summary>
    /// Publishes <paramref name="message"/> to the queue. By default
    /// waits for the broker's disposition before returning; pass
    /// <paramref name="settled"/>=<c>true</c> for fire-and-forget
    /// semantics (returns as soon as the wire write completes).
    /// </summary>
    /// <exception cref="ServiceBusSendException">
    /// The broker rejected the delivery (<c>rejected</c> outcome) or
    /// released it without accepting (<c>released</c> / <c>modified</c>).
    /// Contains the broker-supplied error condition when available.
    /// </exception>
    /// <exception cref="ObjectDisposedException">
    /// The sender has been disposed.
    /// </exception>
    public async Task SendAsync(
        AmqpMessage message,
        bool settled = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ThrowIfDisposed();
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var outcome = await _link
                .SendMessageAsync(message, settled, cancellationToken)
                .ConfigureAwait(false);
            if (outcome != AmqpDispositionOutcome.Accepted)
            {
                throw new ServiceBusSendException(
                    $"Service Bus rejected the message on queue '{QueueName}' with outcome '{outcome}'.",
                    outcome);
            }
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { await _link.DetachAsync(closed: true).ConfigureAwait(false); }
        catch { /* best-effort cleanup */ }
        _sendGate.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(ServiceBusAmqpSender));
    }
}

/// <summary>
/// Thrown by <see cref="ServiceBusAmqpSender.SendAsync"/> when the
/// broker settles a transfer with a non-accepted outcome. Carries the
/// raw AMQP outcome so callers (typically the SQS module's send-handler
/// translation layer) can map it to a service-specific error code.
/// </summary>
internal sealed class ServiceBusSendException : Exception
{
    public ServiceBusSendException(string message, AmqpDispositionOutcome outcome)
        : base(message)
    {
        Outcome = outcome;
    }

    public AmqpDispositionOutcome Outcome { get; }
}
