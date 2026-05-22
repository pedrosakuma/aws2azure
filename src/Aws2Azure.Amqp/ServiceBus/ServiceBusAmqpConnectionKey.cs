namespace Aws2Azure.Amqp.ServiceBus;

/// <summary>
/// Identifies a Service Bus AMQP connection for pool lookup. A pool slot
/// is shared by every queue that lives under the same wire endpoint
/// (host + port + TLS flag) and is authorised by the same SAS key name;
/// the key itself is intentionally excluded so callers may rotate the
/// key value between pool calls without fragmenting the cache. The
/// validator guarantees a non-empty endpoint + key name, but we still
/// null-check defensively here.
/// </summary>
internal readonly record struct ServiceBusAmqpConnectionKey
{
    public ServiceBusAmqpEndpoint Endpoint { get; }
    public string SasKeyName { get; }

    /// <summary>Convenience accessor for the endpoint host (used as the CBS audience namespace).</summary>
    public string NamespaceFqdn => Endpoint.Host;

    public ServiceBusAmqpConnectionKey(ServiceBusAmqpEndpoint endpoint, string sasKeyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sasKeyName);
        if (string.IsNullOrWhiteSpace(endpoint.Host))
        {
            throw new ArgumentException("Endpoint host must be set.", nameof(endpoint));
        }
        Endpoint = endpoint;
        SasKeyName = sasKeyName;
    }
}

