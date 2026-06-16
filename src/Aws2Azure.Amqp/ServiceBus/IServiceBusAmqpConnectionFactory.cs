using Aws2Azure.Amqp.Security;

namespace Aws2Azure.Amqp.ServiceBus;

/// <summary>
/// Strategy for materialising a fresh
/// <see cref="ServiceBusAmqpConnection"/>. Lifted to an interface so the
/// pool can be unit-tested with an in-memory factory that bypasses
/// TCP/TLS plumbing.
/// </summary>
internal interface IServiceBusAmqpConnectionFactory
{
    /// <summary>
    /// Opens a new connection (TCP → optional TLS → SASL → AMQP open →
    /// CBS session) and authorises the supplied endpoint using the
    /// given SAS credentials. The caller takes ownership and must
    /// dispose the returned connection.
    /// </summary>
    Task<ServiceBusAmqpConnection> CreateAsync(
        ServiceBusAmqpEndpoint endpoint,
        string sasKeyName,
        string sasKey,
        CancellationToken cancellationToken);

    /// <summary>
    /// Opens a new connection using a caller-supplied CBS token provider.
    /// <paramref name="credentialMarker"/> identifies the credential in
    /// pool keys and diagnostics; the provider owns token generation.
    /// </summary>
    Task<ServiceBusAmqpConnection> CreateAsync(
        ServiceBusAmqpEndpoint endpoint,
        string credentialMarker,
        IAmqpTokenProvider tokenProvider,
        CancellationToken cancellationToken)
        => throw new NotSupportedException("This factory does not support caller-supplied AMQP token providers.");
}
