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
/// Slice 5d ships the correlation engine and the receive pump; the
/// CBS-specific <c>put-token</c> wrapper lands in Slice 5e.
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

        var messageId = $"req-{Interlocked.Increment(ref _nextMessageId):x16}";
        request.Properties = request.Properties with
        {
            MessageId = messageId,
            ReplyTo = _replyToAddress,
        };

        var tcs = new TaskCompletionSource<AmqpMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(messageId, tcs))
            throw new InvalidOperationException($"Duplicate request id {messageId}.");

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
