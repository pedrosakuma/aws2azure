using System;
using Aws2Azure.Amqp.Connection;
using Aws2Azure.Amqp.Framing;
using Aws2Azure.Amqp.ServiceBus;
using Aws2Azure.Modules.Sqs.Errors;

namespace Aws2Azure.Modules.Sqs.Operations;

internal static class AmqpErrorMapper
{
    /// <summary>
    /// Translates an exception from the AMQP stack into a structured
    /// SQS error mapping. <see cref="AmqpLinkException"/> and
    /// <see cref="AmqpConnectionException"/> carry the spec-classified
    /// <see cref="AmqpErrorKind"/> plus the peer's condition symbol,
    /// which <see cref="SqsErrorMapping.FromAmqp"/> uses to choose the
    /// right SQS code (lock-lost → <c>MessageNotInflight</c>,
    /// not-found → <c>NonExistentQueue</c>, throttled → 503, etc.).
    /// Anything else falls back to a generic 500 — but unlike the
    /// previous slice we no longer concatenate <c>ex.Message</c> into
    /// the response body (it can leak broker-internal diagnostics).
    /// </summary>
    internal static SqsErrorMapping.Mapping MapAmqpException(Exception ex, string operation)
    {
        return ex switch
        {
            AmqpLinkException link => SqsErrorMapping.FromAmqp(link.Kind, link.PeerCondition, operation),
            AmqpConnectionException conn => SqsErrorMapping.FromAmqp(conn.Kind, conn.PeerCondition, operation),
            _ => SqsErrorMapping.InternalError($"aws2azure: AMQP {operation} failed."),
        };
    }

    /// <summary>
    /// Settle-path (<see cref="ServiceBusReceiver.CompleteAsync"/> /
    /// <see cref="ServiceBusReceiver.AbandonAsync"/>) variant of
    /// <see cref="MapAmqpException"/>. A receiver disposed out from
    /// under an in-flight settle — the idle-TTL sweeper (#262) evicting
    /// a long-idle session link, or pool/connection teardown — surfaces
    /// as <see cref="ObjectDisposedException"/>. The Service Bus session
    /// lock is therefore gone, which is SQS-faithfully a stale / expired
    /// receipt handle (<c>ReceiptHandleIsInvalid</c>), not an internal
    /// error: a session is only swept after the idle window, well past
    /// its lock duration, so the message is already redeliverable.
    /// </summary>
    internal static SqsErrorMapping.Mapping MapSettleException(Exception ex, string operation)
        => ex is ObjectDisposedException
            ? SqsErrorMapping.ReceiptHandleInvalid()
            : MapAmqpException(ex, operation);

    /// <summary>
    /// Translates a <see cref="ServiceBusManagementException"/> (raised
    /// by the <c>$management</c> request-response link when SB returns a
    /// non-2xx <c>statusCode</c>) onto an SQS-shaped error. Prefers the
    /// AMQP error condition for classification when present; falls back
    /// to HTTP-style status-code buckets when the broker omits it.
    /// </summary>
    internal static SqsErrorMapping.Mapping MapManagementException(
        ServiceBusManagementException ex, string operation)
    {
        if (!string.IsNullOrEmpty(ex.ErrorCondition))
        {
            var kind = AmqpErrorClassifier.Classify(ex.ErrorCondition);
            return SqsErrorMapping.FromAmqp(kind, ex.ErrorCondition, operation);
        }
        // 404 / 410 from SB $management = lock no longer valid for this
        // receiver — looks like an expired/settled message to the caller.
        if (ex.StatusCode == 404 || ex.StatusCode == 410)
            return SqsErrorMapping.MessageNotInflight();
        if (ex.StatusCode == 401 || ex.StatusCode == 403)
            return SqsErrorMapping.FromAmqp(AmqpErrorKind.Auth, condition: null, operation);
        if (ex.StatusCode == 429 || ex.StatusCode == 503)
            return SqsErrorMapping.FromAmqp(AmqpErrorKind.Throttled, condition: null, operation);
        if (ex.StatusCode >= 500)
            return SqsErrorMapping.FromAmqp(AmqpErrorKind.ServerFatal, condition: null, operation);
        return SqsErrorMapping.FromAmqp(AmqpErrorKind.ClientFatal, condition: null, operation);
    }

}
