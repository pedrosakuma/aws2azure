using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using Aws2Azure.Amqp.Sasl;
using Aws2Azure.Amqp.Transport;

namespace Aws2Azure.Amqp.ServiceBus;

/// <summary>
/// Production entry point that opens a TLS+SASL-negotiated transport
/// against a Service Bus namespace. Returns the raw
/// <see cref="IAmqpTransport"/> ready to be fed into
/// <see cref="ServiceBusAmqpConnection.OpenAsync"/>.
/// <para>
/// Unit tests skip this entirely and inject an in-memory duplex
/// transport — the orchestrator never sees the TLS plumbing. Real-Azure
/// integration tests are the first consumer.
/// </para>
/// </summary>
internal static class ServiceBusAmqpTlsConnector
{
    /// <summary>
    /// Opens a TCP socket to <c>{namespaceFqdn}:5671</c>, wraps it in
    /// TLS 1.2+ with SNI = namespace FQDN, runs SASL ANONYMOUS, and
    /// returns the negotiated transport. The caller owns the returned
    /// transport and must dispose it (transitively closing the socket).
    /// </summary>
    public static async Task<IAmqpTransport> ConnectAsync(
        string namespaceFqdn,
        int port = ServiceBusEndpoint.AmqpsPort,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceFqdn);
        var host = namespaceFqdn.Trim();

        var tcp = new TcpClient { NoDelay = true };
        try
        {
            await tcp.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
            var network = tcp.GetStream();

            var ssl = new SslStream(network, leaveInnerStreamOpen: false);
            try
            {
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = host,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                }, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await ssl.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            // ssl now owns the network stream; the tcp client owns the
            // socket. Hand both to the transport so disposal cascades.
            var transport = new TcpAmqpTransport(tcp, ssl);
            try
            {
                await SaslAnonymousNegotiator.NegotiateAsync(transport, trace: null, cancellationToken).ConfigureAwait(false);
                return transport;
            }
            catch
            {
                await transport.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Transport that owns the TCP client + SSL stream so disposal
    /// cascades through both layers. The pipe wrappers are produced by
    /// <see cref="StreamAmqpTransport"/>.
    /// </summary>
    private sealed class TcpAmqpTransport : IAmqpTransport
    {
        private readonly TcpClient _tcp;
        private readonly SslStream _ssl;
        private readonly StreamAmqpTransport _inner;
        private int _disposed;

        public TcpAmqpTransport(TcpClient tcp, SslStream ssl)
        {
            _tcp = tcp;
            _ssl = ssl;
            _inner = new StreamAmqpTransport(ssl, leaveOpen: true);
        }

        public System.IO.Pipelines.PipeReader Input => _inner.Input;
        public System.IO.Pipelines.PipeWriter Output => _inner.Output;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            await _inner.DisposeAsync().ConfigureAwait(false);
            await _ssl.DisposeAsync().ConfigureAwait(false);
            _tcp.Dispose();
        }
    }
}
