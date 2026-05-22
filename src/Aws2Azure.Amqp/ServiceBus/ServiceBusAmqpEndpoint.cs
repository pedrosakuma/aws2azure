namespace Aws2Azure.Amqp.ServiceBus;

/// <summary>
/// Address + wire-protocol bundle for opening a Service Bus AMQP
/// connection. Pairs the namespace host with the TCP port and whether
/// the transport should be wrapped in TLS. Production calls
/// <see cref="Tls"/> (port 5671, TLS on) against a Service Bus
/// namespace FQDN; the Phase 2.7 integration tests use <see cref="Plain"/>
/// (port 5672, TLS off) against the SB emulator running locally.
///
/// <para>The bundle is the unit of caching in
/// <see cref="ServiceBusAmqpPool"/>: two callers asking for the same
/// (host, port, useTls) tuple share a connection; differing on any of
/// the three forces a separate connection slot.</para>
/// </summary>
internal readonly record struct ServiceBusAmqpEndpoint
{
    /// <summary>Lower-cased host (DNS name or IP) the AMQP TCP socket dials.</summary>
    public string Host { get; }

    /// <summary>TCP port the AMQP socket dials.</summary>
    public int Port { get; }

    /// <summary>
    /// True when the TCP socket must be wrapped in TLS before SASL
    /// negotiation. Production Service Bus is always TLS; the SB
    /// emulator + some sovereign-cloud dev rigs are plain.
    /// </summary>
    public bool UseTls { get; }

    /// <summary>
    /// Namespace label the broker authorises against — populates the
    /// AMQP <c>open.hostname</c> field and the CBS audience
    /// (<c>amqps://{LogicalNamespace}/{queue}</c>). Decoupled from
    /// <see cref="Host"/> because TCP-level connectivity (IP / docker
    /// bridge) routinely differs from the broker's namespace identity.
    /// Production: same as the FQDN host. Emulator: <c>localhost</c>
    /// regardless of which loopback/bridge IP we dial.
    /// </summary>
    public string LogicalNamespace { get; }

    public ServiceBusAmqpEndpoint(string host, int port, bool useTls, string? logicalNamespace = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), port,
                "Port must be a valid TCP port (1-65535).");
        }
        Host = host.Trim().ToLowerInvariant();
        Port = port;
        UseTls = useTls;
        LogicalNamespace = string.IsNullOrWhiteSpace(logicalNamespace)
            ? Host
            : logicalNamespace.Trim().ToLowerInvariant();
    }

    /// <summary>
    /// Production shape: TLS on port 5671 against the given namespace
    /// FQDN. Equivalent to <c>amqps://{host}:5671</c>. Logical namespace
    /// defaults to <paramref name="host"/>.
    /// </summary>
    public static ServiceBusAmqpEndpoint Tls(string host, int port = ServiceBusEndpoint.AmqpsPort, string? logicalNamespace = null) =>
        new(host, port, useTls: true, logicalNamespace);

    /// <summary>
    /// Emulator/dev shape: plain TCP on port 5672. Equivalent to
    /// <c>amqp://{host}:5672</c>. The Service Bus emulator only listens
    /// in this mode and authorises against the logical namespace
    /// <c>localhost</c> regardless of which IP we dial — so the default
    /// for <paramref name="logicalNamespace"/> is <c>"localhost"</c>.
    /// </summary>
    public static ServiceBusAmqpEndpoint Plain(string host, int port = ServiceBusEndpoint.AmqpPort, string? logicalNamespace = null) =>
        new(host, port, useTls: false, logicalNamespace ?? "localhost");

    public override string ToString() =>
        (UseTls ? "amqps://" : "amqp://") + Host + ":" + Port + " (ns=" + LogicalNamespace + ")";
}
