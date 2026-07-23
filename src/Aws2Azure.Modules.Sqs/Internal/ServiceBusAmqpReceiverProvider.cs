using System;
using System.Threading;
using System.Threading.Tasks;
using Aws2Azure.Amqp.ServiceBus;
using Aws2Azure.Core.Configuration;

namespace Aws2Azure.Modules.Sqs.Internal;

/// <summary>
/// Production <see cref="IAmqpReceiverProvider"/>. Binds a
/// <see cref="ServiceBusAmqpPool"/> to the (namespace, SAS key name,
/// SAS key) tuple from a resolved
/// <see cref="ServiceBusCredentials"/> so the per-request handler only
/// needs to know the queue name.
/// </summary>
internal sealed class ServiceBusAmqpReceiverProvider : IAmqpReceiverProvider
{
    private readonly ServiceBusAmqpPool _pool;
    private readonly ServiceBusAmqpEndpoint _endpoint;
    private readonly string _sasKeyName;
    private readonly string _sasKey;

    public ServiceBusAmqpReceiverProvider(ServiceBusAmqpPool pool, ServiceBusCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(credentials);

        // ServiceBusCredentials.Namespace can be either a bare DNS label
        // ("myns" → myns.servicebus.windows.net) or an absolute http(s)
        // URL (emulator / sovereign cloud). The REST-side ServiceBusClient
        // already normalises both; we just lift the host out so the AMQP
        // pool gets the FQDN it expects.
        _pool = pool;
        _endpoint = ServiceBusClient.ResolveAmqpEndpoint(credentials.Namespace);
        _sasKeyName = credentials.SasKeyName;
        _sasKey = credentials.SasKey;
    }

    public Task<ServiceBusReceiver> GetReceiverAsync(string queueName, CancellationToken cancellationToken) =>
        _pool.GetReceiverAsync(_endpoint, _sasKeyName, _sasKey, queueName, cancellationToken);

    public ServiceBusReceiver? TryGetExistingReceiver(string queueName) =>
        _pool.TryGetExistingReceiver(_endpoint, _sasKeyName, queueName);

    public Task<ServiceBusReceiver> GetSessionReceiverAsync(string queueName, string sessionId, CancellationToken cancellationToken) =>
        _pool.GetSessionReceiverAsync(_endpoint, _sasKeyName, _sasKey, queueName, sessionId, cancellationToken);

    public ServiceBusReceiver? TryGetExistingSessionReceiver(string queueName, string sessionId) =>
        _pool.TryGetExistingSessionReceiver(_endpoint, _sasKeyName, queueName, sessionId);

    public AmqpReceiverLease? TryAcquireExistingSessionReceiver(
        string queueName,
        string sessionId)
    {
        var lease = _pool.TryAcquireExistingSessionReceiver(
            _endpoint, _sasKeyName, queueName, sessionId);
        return lease is null ? null : new AmqpReceiverLease(lease.Receiver, lease);
    }

    public Task<BrokerAssignedSessionReceiverResult> AcquireBrokerAssignedSessionReceiverAsync(
        string queueName,
        TimeSpan maxBrokerWait,
        CancellationToken cancellationToken) =>
        _pool.AcquireBrokerAssignedSessionReceiverAsync(
            _endpoint, _sasKeyName, _sasKey, queueName, maxBrokerWait, cancellationToken);

    public Task InvalidateSessionReceiverAsync(string queueName, string sessionId) =>
        _pool.InvalidateSessionReceiverAsync(_endpoint, _sasKeyName, queueName, sessionId);

    public Task<ServiceBusManagementClient> GetManagementClientAsync(string queueName, CancellationToken cancellationToken) =>
        _pool.GetManagementClientAsync(_endpoint, _sasKeyName, _sasKey, queueName, cancellationToken);

    public Task InvalidateAsync(string queueName, bool closeConnection) =>
        _pool.InvalidateReceiverAsync(_endpoint, _sasKeyName, queueName, closeConnection);

    public Task InvalidateManagementClientAsync(string queueName) =>
        _pool.InvalidateManagementClientAsync(_endpoint, _sasKeyName, queueName);
}
