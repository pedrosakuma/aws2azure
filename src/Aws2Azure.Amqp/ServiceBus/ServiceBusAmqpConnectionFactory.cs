using Aws2Azure.Amqp.Connection;
using Aws2Azure.Amqp.Security;

namespace Aws2Azure.Amqp.ServiceBus;

/// <summary>
/// Production <see cref="IServiceBusAmqpConnectionFactory"/>: opens a
/// TLS+SASL transport to <c>{namespace}:5671</c>, instantiates a SAS
/// token provider, and hands both to
/// <see cref="ServiceBusAmqpConnection.OpenAsync"/>. Lifts the wiring
/// out of the pool so the pool's unit tests can swap in a fake
/// factory.
/// </summary>
internal sealed class ServiceBusAmqpConnectionFactory : IServiceBusAmqpConnectionFactory
{
    private readonly AmqpConnectionSettings _connectionSettings;

    public ServiceBusAmqpConnectionFactory(AmqpConnectionSettings connectionSettings)
    {
        ArgumentNullException.ThrowIfNull(connectionSettings);
        _connectionSettings = connectionSettings;
    }

    public async Task<ServiceBusAmqpConnection> CreateAsync(
        string namespaceFqdn,
        string sasKeyName,
        string sasKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceFqdn);
        ArgumentException.ThrowIfNullOrWhiteSpace(sasKeyName);
        ArgumentException.ThrowIfNullOrWhiteSpace(sasKey);

        var transport = await ServiceBusAmqpTlsConnector
            .ConnectAsync(namespaceFqdn, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var tokenProvider = new ServiceBusSasTokenProvider(sasKeyName, sasKey);
            return await ServiceBusAmqpConnection
                .OpenAsync(transport, tokenProvider, _connectionSettings, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            await transport.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
