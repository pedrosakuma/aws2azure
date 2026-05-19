namespace Aws2Azure.Amqp.Connection;

/// <summary>
/// Per-session configuration knobs. The defaults match what Azure Service
/// Bus advertises for ad-hoc management sessions and are conservative
/// enough for control-plane links (CBS, request/response).
/// </summary>
internal sealed record AmqpSessionSettings
{
    /// <summary>
    /// Initial value of <c>begin.next-outgoing-id</c> (§2.5.6). Per spec
    /// the choice is arbitrary; we always start at 0 to keep tests
    /// deterministic.
    /// </summary>
    public uint NextOutgoingId { get; init; } = 0;

    /// <summary>
    /// Initial <c>incoming-window</c> in transfers (§2.5.6). Sized to
    /// comfortably hold a CBS request/response burst.
    /// </summary>
    public uint IncomingWindow { get; init; } = 2048;

    /// <summary>Initial <c>outgoing-window</c> in transfers (§2.5.6).</summary>
    public uint OutgoingWindow { get; init; } = 2048;

    /// <summary>
    /// Maximum link handle this session is willing to allocate
    /// (<c>begin.handle-max</c>, §2.5.6). 255 is plenty for CBS + a
    /// handful of session/receiver links.
    /// </summary>
    public uint HandleMax { get; init; } = 255;
}
