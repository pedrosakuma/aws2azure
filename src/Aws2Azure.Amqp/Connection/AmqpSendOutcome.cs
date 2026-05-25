namespace Aws2Azure.Amqp.Connection;

/// <summary>
/// Result of an outbound <see cref="AmqpLink.SendMessageAsync"/> call.
/// Carries the disposition outcome plus, when the broker rejected the
/// delivery and included an <c>error</c> composite, the AMQP condition
/// symbol and human-readable description. Higher layers (e.g. the SQS
/// AMQP send handler) use the condition to classify failures into
/// retryable / throttled / fatal categories via
/// <see cref="Framing.AmqpErrorClassifier"/> instead of pattern-matching
/// the outcome alone.
/// </summary>
internal readonly record struct AmqpSendOutcome(
    AmqpDispositionOutcome Outcome,
    string? Condition,
    string? Description)
{
    public static AmqpSendOutcome Accepted { get; } =
        new(AmqpDispositionOutcome.Accepted, null, null);
}
