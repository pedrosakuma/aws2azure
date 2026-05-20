namespace Aws2Azure.Core.Configuration;

/// <summary>
/// Resolves the effective <see cref="SqsTransport"/> for a given queue
/// name against a <see cref="ServiceBusCredentials"/>. Per-queue
/// overrides win over the namespace-wide default. The lookup is
/// case-insensitive on the queue name to match the SQS handler's
/// canonicalisation rules.
/// </summary>
public static class SqsTransportResolver
{
    public static SqsTransport Resolve(ServiceBusCredentials credentials, string queueName)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentException.ThrowIfNullOrEmpty(queueName);

        if (credentials.Queues is { Count: > 0 } queues)
        {
            foreach (var pair in queues)
            {
                if (string.Equals(pair.Key, queueName, StringComparison.OrdinalIgnoreCase)
                    && pair.Value?.Transport is { } perQueue)
                {
                    return perQueue;
                }
            }
        }

        return credentials.Transport;
    }
}
