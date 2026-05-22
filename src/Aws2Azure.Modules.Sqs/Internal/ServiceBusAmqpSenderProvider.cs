using System;
using System.Threading;
using System.Threading.Tasks;
using Aws2Azure.Amqp.ServiceBus;
using Aws2Azure.Core.Configuration;

namespace Aws2Azure.Modules.Sqs.Internal;

/// <summary>
/// Production <see cref="IAmqpSenderProvider"/>. Mirrors
/// <see cref="ServiceBusAmqpReceiverProvider"/> — binds a
/// <see cref="ServiceBusAmqpPool"/> to the (namespace, SAS key name,
/// SAS key) tuple lifted from a resolved
/// <see cref="ServiceBusCredentials"/> so per-request handlers only
/// need the queue name.
/// </summary>
internal sealed class ServiceBusAmqpSenderProvider : IAmqpSenderProvider
{
    private readonly ServiceBusAmqpPool _pool;
    private readonly string _namespaceFqdn;
    private readonly string _sasKeyName;
    private readonly string _sasKey;

    public ServiceBusAmqpSenderProvider(ServiceBusAmqpPool pool, ServiceBusCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(credentials);

        var endpoint = ServiceBusClient.ResolveEndpoint(credentials.Namespace);
        _pool = pool;
        _namespaceFqdn = endpoint.Host;
        _sasKeyName = credentials.SasKeyName;
        _sasKey = credentials.SasKey;
    }

    public Task<ServiceBusAmqpSender> GetSenderAsync(string queueName, CancellationToken cancellationToken) =>
        _pool.GetSenderAsync(_namespaceFqdn, _sasKeyName, _sasKey, queueName, cancellationToken);

    public Task InvalidateSenderAsync(string queueName, bool closeConnection) =>
        _pool.InvalidateSenderAsync(_namespaceFqdn, _sasKeyName, queueName, closeConnection);
}
