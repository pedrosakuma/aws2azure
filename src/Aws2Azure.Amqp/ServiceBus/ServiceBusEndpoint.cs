namespace Aws2Azure.Amqp.ServiceBus;

/// <summary>
/// Service Bus connectivity helpers. AMQP over TLS uses port 5671 against
/// the namespace FQDN (e.g. <c>my-ns.servicebus.windows.net</c>); the CBS
/// audience for a queue is the absolute <c>amqps://{namespace}/{queueName}</c>
/// URL (Service Bus also accepts the bare scheme-less form for legacy SDKs,
/// but the SDK-canonical form is the <c>amqps://</c> URL — we stick with
/// that for consistency).
/// </summary>
internal static class ServiceBusEndpoint
{
    /// <summary>Default TLS port for AMQP 1.0 (RFC 1700 / IANA).</summary>
    public const int AmqpsPort = 5671;

    /// <summary>Default plain-TCP port for AMQP 1.0 (test / Azurite-style emulators only).</summary>
    public const int AmqpPort = 5672;

    /// <summary>
    /// Returns the CBS audience URI a put-token request must carry for the
    /// given queue under the given namespace. The audience is the AMQP
    /// resource scope SB authorises against, not the wire endpoint.
    /// </summary>
    public static string BuildQueueAudience(string namespaceFqdn, string queueName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceFqdn);
        ArgumentException.ThrowIfNullOrWhiteSpace(queueName);
        return "amqps://" + namespaceFqdn.Trim().ToLowerInvariant() + "/" + queueName.Trim();
    }

    /// <summary>
    /// Returns the AMQP address used as the source terminus of a receiver
    /// link consuming from the given queue. Service Bus is permissive here
    /// and accepts the bare queue name on a session opened against the
    /// namespace; we prefer that form so callers don't accidentally double
    /// the namespace.
    /// </summary>
    public static string BuildReceiverSourceAddress(string queueName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queueName);
        return queueName.Trim();
    }

    /// <summary>
    /// Returns the AMQP address used as the target terminus of a sender
    /// link publishing to the given queue. Symmetric to
    /// <see cref="BuildReceiverSourceAddress"/> — Service Bus accepts the
    /// bare queue name on a session opened against the namespace.
    /// </summary>
    public static string BuildSenderTargetAddress(string queueName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queueName);
        return queueName.Trim();
    }
}
