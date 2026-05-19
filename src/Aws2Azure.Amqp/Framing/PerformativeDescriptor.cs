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
}
