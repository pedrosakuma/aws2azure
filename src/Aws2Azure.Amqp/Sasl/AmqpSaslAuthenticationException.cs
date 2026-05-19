namespace Aws2Azure.Amqp.Sasl;

/// <summary>
/// Thrown when SASL negotiation fails: the server does not offer the
/// requested mechanism, the outcome code is not <c>ok</c>, or the peer
/// sends an unexpected performative for the current state.
/// </summary>
internal sealed class AmqpSaslAuthenticationException : Exception
{
    public AmqpSaslAuthenticationException(string message) : base(message) { }

    public AmqpSaslAuthenticationException(string message, Exception inner) : base(message, inner) { }

    /// <summary>
    /// When the failure is a SASL outcome with a non-ok code, this carries
    /// the raw outcome byte. <c>null</c> for other failures.
    /// </summary>
    public byte? OutcomeCode { get; init; }
}
