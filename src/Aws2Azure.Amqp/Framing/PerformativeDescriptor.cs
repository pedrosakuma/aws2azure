namespace Aws2Azure.Amqp.Framing;

/// <summary>
/// Descriptor codes for the AMQP 1.0 transport performatives
/// (OASIS AMQP 1.0 "AMQP 1.0 Transport" §2.7). Each performative is
/// encoded as a described type whose descriptor is the matching
/// <c>ulong</c> below and whose value is a list of fields in
/// spec-defined positional order.
/// </summary>
internal static class PerformativeDescriptor
{
    public const ulong Open = 0x0000_0000_0000_0010UL;
    public const ulong Begin = 0x0000_0000_0000_0011UL;
    public const ulong Attach = 0x0000_0000_0000_0012UL;
    public const ulong Flow = 0x0000_0000_0000_0013UL;
    public const ulong Transfer = 0x0000_0000_0000_0014UL;
    public const ulong Disposition = 0x0000_0000_0000_0015UL;
    public const ulong Detach = 0x0000_0000_0000_0016UL;
    public const ulong End = 0x0000_0000_0000_0017UL;
    public const ulong Close = 0x0000_0000_0000_0018UL;

    // SASL sub-protocol performatives (AMQP 1.0 §5.3.3).
    public const ulong SaslMechanisms = 0x0000_0000_0000_0040UL;
    public const ulong SaslInit = 0x0000_0000_0000_0041UL;
    public const ulong SaslChallenge = 0x0000_0000_0000_0042UL;
    public const ulong SaslResponse = 0x0000_0000_0000_0043UL;
    public const ulong SaslOutcome = 0x0000_0000_0000_0044UL;
}

/// <summary>
/// SASL outcome codes (§5.3.3.6): the single ubyte value carried in
/// <c>sasl-outcome.code</c>. <see cref="Ok"/> means authentication
/// succeeded and the peer may now exchange the AMQP protocol header.
/// </summary>
internal enum AmqpSaslOutcomeCode : byte
{
    Ok = 0,
    Auth = 1,
    Sys = 2,
    SysPerm = 3,
    SysTemp = 4,
}

/// <summary>
/// Categorical kind of an AMQP transport performative, returned by
/// <see cref="PerformativeCodec.PeekKind"/> so callers can dispatch
/// without re-decoding the described-type envelope twice.
/// </summary>
internal enum PerformativeKind
{
    Unknown = 0,
    Open,
    Begin,
    Attach,
    Flow,
    Transfer,
    Disposition,
    Detach,
    End,
    Close,
    SaslMechanisms,
    SaslInit,
    SaslChallenge,
    SaslResponse,
    SaslOutcome,
}
