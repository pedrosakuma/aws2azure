namespace Aws2Azure.Amqp.Connection;

/// <summary>
/// Local configuration proposed during the AMQP <c>open</c> handshake
/// (§2.7.1). The peer's <c>open</c> may further constrain these values;
/// the negotiated effective values live on <see cref="AmqpConnection"/>.
/// </summary>
internal sealed record AmqpConnectionSettings
{
    /// <summary>
    /// REQUIRED. Stable identifier for this client container. Service Bus
    /// surfaces it in management/diagnostics so prefer something unique
    /// per process (e.g. <c>"aws2azure/{hostname}/{pid}"</c>).
    /// </summary>
    public required string ContainerId { get; init; }

    /// <summary>
    /// SNI/virtual host advertised to the peer. Service Bus expects the
    /// namespace FQDN here (e.g. <c>ns.servicebus.windows.net</c>).
    /// </summary>
    public string? Hostname { get; init; }

    /// <summary>
    /// Largest frame size we are willing to receive. Spec minimum 512.
    /// Default 64 KiB — enough for typical SQS messages once the AMQP
    /// session is in place. Negotiated: peer's <c>open.max-frame-size</c>
    /// caps what *we* may send.
    /// </summary>
    public uint MaxFrameSize { get; init; } = 64 * 1024;

    /// <summary>Highest session channel we will use; default 0xFFFF.</summary>
    public ushort ChannelMax { get; init; } = ushort.MaxValue;

    /// <summary>
    /// Idle timeout we advertise to the peer. If <see cref="TimeSpan.Zero"/>
    /// the field is omitted (peer never required to send heartbeats).
    /// Default 60s; Service Bus advertises 240s on its side.
    /// </summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(60);
}
