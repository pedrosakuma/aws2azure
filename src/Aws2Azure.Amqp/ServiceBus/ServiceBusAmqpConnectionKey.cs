namespace Aws2Azure.Amqp.ServiceBus;

/// <summary>
/// Identifies a Service Bus AMQP connection for pool lookup. A pool slot
/// is shared by every queue that lives under the same namespace and is
/// authorised by the same SAS key; the key itself is intentionally
/// excluded so callers may rotate the key value between pool calls
/// without fragmenting the cache. The validator guarantees a non-empty
/// namespace + key name, but we still null-check defensively here.
/// </summary>
internal readonly record struct ServiceBusAmqpConnectionKey
{
    public string NamespaceFqdn { get; }
    public string SasKeyName { get; }

    public ServiceBusAmqpConnectionKey(string namespaceFqdn, string sasKeyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceFqdn);
        ArgumentException.ThrowIfNullOrWhiteSpace(sasKeyName);
        NamespaceFqdn = namespaceFqdn.Trim().ToLowerInvariant();
        SasKeyName = sasKeyName;
    }
}
