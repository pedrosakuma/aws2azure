using Aws2Azure.Amqp.Connection;
using Aws2Azure.Amqp.Security;

namespace Aws2Azure.Amqp.ServiceBus;

/// <summary>
/// Production <see cref="IServiceBusAmqpConnectionFactory"/>: opens a
/// TCP (+ optional TLS) + SASL transport to
/// <c>{endpoint.Host}:{endpoint.Port}</c>, instantiates a SAS token
/// provider, and hands both to
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
        ServiceBusAmqpEndpoint endpoint,
        string sasKeyName,
        string sasKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sasKeyName);
        ArgumentException.ThrowIfNullOrWhiteSpace(sasKey);
        return await CreateAsync(
                endpoint,
                sasKeyName,
                new ServiceBusSasTokenProvider(sasKeyName, sasKey),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<ServiceBusAmqpConnection> CreateAsync(
        ServiceBusAmqpEndpoint endpoint,
        string credentialMarker,
        IAmqpTokenProvider tokenProvider,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialMarker);
        ArgumentNullException.ThrowIfNull(tokenProvider);

        var transport = await ServiceBusAmqpConnector
            .ConnectAsync(endpoint, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            // Clone+override Hostname so the AMQP open frame's hostname
            // field matches the broker's logical namespace identity
            // rather than the docker bridge IP / loopback we happen to
            // be dialling. Service Bus (and the emulator) authorise on
            // this label, not on the TCP target.
            var perEndpointSettings = _connectionSettings with { Hostname = endpoint.LogicalNamespace };
            return await ServiceBusAmqpConnection
                .OpenAsync(transport, tokenProvider, perEndpointSettings, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            await transport.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
