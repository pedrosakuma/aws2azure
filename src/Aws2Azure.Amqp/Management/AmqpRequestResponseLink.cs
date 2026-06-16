using System.Collections.Concurrent;
using Aws2Azure.Amqp.Connection;
using Aws2Azure.Amqp.Framing;

namespace Aws2Azure.Amqp.Management;

/// <summary>
/// Implements the AMQP request/response pattern (OASIS AMQP Management
/// 1.0 §3, also used by Azure Service Bus <c>$cbs</c>): one sender
/// link writes requests with <c>message-id</c> + <c>reply-to</c>, a
/// paired receiver link delivers responses whose
/// <c>correlation-id</c> matches the original message-id.
/// </summary>
/// <remarks>
/// Provides the generic correlation engine and the receive pump; the
/// CBS-specific <c>put-token</c> wrapper is layered on top by the CBS
/// authenticator.
/// </remarks>
internal sealed class AmqpRequestResponseLink : IAsyncDisposable
{
    private readonly AmqpSession _session;
    private readonly AmqpRequestResponseLinkSettings _settings;
    private readonly string _replyToAddress;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<AmqpMessage>> _pending =
        new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _pumpCts = new();
    private long _nextMessageId;
    private AmqpLink? _sender;
    private AmqpLink? _receiver;
    private Task? _pumpTask;
    private int _disposed;
    private int _faulted;
    private Exception? _faultException;

    public AmqpRequestResponseLink(AmqpSession session, AmqpRequestResponseLinkSettings settings)
    {
        _session = session;
        _settings = settings;
        _replyToAddress = settings.ReplyToAddress
            ?? $"client-reply-to-{Guid.NewGuid():N}";
    }

    /// <summary>The reply-to address stamped on outgoing requests.</summary>
    public string ReplyToAddress => _replyToAddress;

    /// <summary>
    /// True once the receive pump has terminated (peer detached the
    /// sender or receiver, connection closed, or any other faulted
    /// path). Subsequent <see cref="SendRequestAsync"/> calls will
    /// immediately fail with the same exception that broke the pump.
    /// Pool slots use this to evict a dead client before handing it
    /// back to the next caller.
    /// </summary>
    public bool IsClosed =>
        Volatile.Read(ref _faulted) != 0
        || Volatile.Read(ref _disposed) != 0
        || (_sender?.IsClosed ?? false)
        || (_receiver?.IsClosed ?? false);

    /// <summary>
    /// Opens the sender + receiver links and starts the receive pump.
    /// Must be called once before <see cref="SendRequestAsync"/>.
    /// </summary>
    public async Task OpenAsync(CancellationToken cancellationToken = default)
    {
        var senderName = _settings.SenderName ?? $"{_settings.Address}-sender:{Guid.NewGuid():N}";
        var receiverName = _settings.ReceiverName ?? $"{_settings.Address}-receiver:{Guid.NewGuid():N}";

        _sender = await _session.AttachLinkAsync(new AmqpLinkSettings
        {
            Name = senderName,
            Role = AmqpRole.Sender,
            SourceAddress = _replyToAddress,
            TargetAddress = _settings.Address,
            SenderSettleMode = AmqpSenderSettleMode.Settled,
        }, cancellationToken).ConfigureAwait(false);

        _receiver = await _session.AttachLinkAsync(new AmqpLinkSettings
        {
            Name = receiverName,
            Role = AmqpRole.Receiver,
            SourceAddress = _settings.Address,
            TargetAddress = _replyToAddress,
        }, cancellationToken).ConfigureAwait(false);

        await _receiver.GrantCreditAsync(_settings.InitialReceiverCredit, cancellationToken)
            .ConfigureAwait(false);

        _pumpTask = Task.Run(() => ReceivePumpAsync(_pumpCts.Token));
    }

    /// <summary>
    /// Sends <paramref name="request"/> and awaits the matching response.
    /// The request's <c>message-id</c> and <c>reply-to</c> properties
    /// are overwritten by this method.
    /// </summary>
    public async Task<AmqpMessage> SendRequestAsync(
        AmqpMessage request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_sender is null || _receiver is null)
            throw new InvalidOperationException("OpenAsync must be called before SendRequestAsync.");

        // Fail fast if the pump has already terminated. Otherwise the new
        // pending TCS would be enqueued with no pump left to complete it,
        // hanging until the caller's cancellation fires.
        ThrowIfClosed();

        var messageId = $"req-{Interlocked.Increment(ref _nextMessageId):x16}";
        request.Properties = request.Properties with
        {
            MessageId = messageId,
            ReplyTo = _replyToAddress,
        };

        var tcs = new TaskCompletionSource<AmqpMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(messageId, tcs))
            throw new InvalidOperationException($"Duplicate request id {messageId}.");

        // Recheck under happens-after: the pump may have faulted between
        // our ThrowIfClosed() above and TryAdd. FailAllPending only
        // reaps entries it can observe in _pending; an entry added after
        // the reap would otherwise leak. Order is important — _faulted
        // is set before FailAllPending walks _pending, so reading
        // _faulted after a successful TryAdd is sufficient.
        if (Volatile.Read(ref _faulted) != 0 || Volatile.Read(ref _disposed) != 0)
        {
            _pending.TryRemove(messageId, out _);
            ThrowIfClosed();
        }

        try
        {
            await _sender.SendMessageAsync(request, settled: true, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            _pending.TryRemove(messageId, out _);
            throw;
        }

        using (cancellationToken.Register(() =>
        {
            if (_pending.TryRemove(messageId, out var pending))
                pending.TrySetCanceled(cancellationToken);
        }))
        {
            return await tcs.Task.ConfigureAwait(false);
        }
    }

    private void ThrowIfClosed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(AmqpRequestResponseLink));
        if (Volatile.Read(ref _faulted) != 0)
            throw _faultException
                ?? new InvalidOperationException("Request/response link faulted.");
    }

    private async Task ReceivePumpAsync(CancellationToken ct)
    {
        var receiver = _receiver!;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                AmqpIncomingDelivery delivery;
                try
                {
                    delivery = await receiver.ReceiveMessageAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch (InvalidOperationException)
                {
                    FailAllPending(new InvalidOperationException("Request/response link detached."));
                    break;
                }

                var msg = delivery.ToMessage();
                try { await receiver.AcceptAsync(delivery, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                catch { /* best-effort settle */ }

                // Replenish one credit per accept so the broker keeps
                // feeding us. Simple and good enough for management
                // traffic; the alloc-free polish slice will replace
                // this with windowed top-up once live credit is
                // exposed on AmqpLink.
                try { await receiver.GrantCreditAsync(1, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                catch { }

                var correlationId = msg.Properties.CorrelationId;
                if (correlationId is not null
                    && _pending.TryRemove(correlationId, out var tcs))
                {
                    tcs.TrySetResult(msg);
                }
                // Unmatched responses are silently dropped — typical for
                // brokers that emit unsolicited admin chatter on the node.
            }
        }
        catch (Exception ex)
        {
            FailAllPending(ex);
        }
    }

    private void FailAllPending(Exception ex)
    {
        // Publish the terminal exception before flipping _faulted so any
        // SendRequestAsync caller that observes _faulted via
        // ThrowIfClosed sees the same root cause rather than the generic
        // "Request/response link faulted." fallback.
        Interlocked.CompareExchange(ref _faultException, ex, null);
        Interlocked.Exchange(ref _faulted, 1);
        foreach (var key in _pending.Keys.ToArray())
        {
            if (_pending.TryRemove(key, out var tcs))
                tcs.TrySetException(ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _pumpCts.Cancel();
        try { if (_pumpTask is not null) await _pumpTask.ConfigureAwait(false); } catch { }
        FailAllPending(new ObjectDisposedException(nameof(AmqpRequestResponseLink)));
        try { if (_receiver is not null) await _receiver.DetachAsync().ConfigureAwait(false); } catch { }
        try { if (_sender is not null) await _sender.DetachAsync().ConfigureAwait(false); } catch { }
        _pumpCts.Dispose();
    }
}
