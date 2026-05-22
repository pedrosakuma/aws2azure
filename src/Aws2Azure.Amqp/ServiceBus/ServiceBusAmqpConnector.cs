using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using Aws2Azure.Amqp.Sasl;
using Aws2Azure.Amqp.Transport;

namespace Aws2Azure.Amqp.ServiceBus;

/// <summary>
/// Production entry point that opens a TCP (+ optional TLS) + SASL-
/// negotiated transport against a Service Bus AMQP endpoint. Returns
/// the raw <see cref="IAmqpTransport"/> ready to be fed into
/// <see cref="ServiceBusAmqpConnection.OpenAsync"/>.
/// <para>
/// Unit tests skip this entirely and inject an in-memory duplex
/// transport — the orchestrator never sees the TCP/TLS plumbing.
/// Integration tests (against real Azure or the SB emulator) are the
/// first consumers of the real connector.
/// </para>
/// </summary>
internal static class ServiceBusAmqpConnector
{
    /// <summary>
    /// Opens a TCP socket to <c>{endpoint.Host}:{endpoint.Port}</c>,
    /// optionally wraps it in TLS 1.2+ with SNI = host
    /// (<see cref="ServiceBusAmqpEndpoint.UseTls"/>), runs SASL
    /// ANONYMOUS, and returns the negotiated transport. The caller
    /// owns the returned transport and must dispose it (transitively
    /// closing the socket).
    /// </summary>
    public static async Task<IAmqpTransport> ConnectAsync(
        ServiceBusAmqpEndpoint endpoint,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(endpoint.Host))
        {
            throw new ArgumentException("Endpoint host must be set.", nameof(endpoint));
        }
        var host = endpoint.Host;
        var port = endpoint.Port;

        var tcp = new TcpClient { NoDelay = true };
        try
        {
            await tcp.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
            var network = tcp.GetStream();

            if (!endpoint.UseTls)
            {
                // Plain TCP: hand the network stream straight into the
                // AMQP transport. Emulator-only path — Azure Service
                // Bus production listeners reject non-TLS.
                var plain = new TcpAmqpTransport(tcp, sslStream: null, network);
                try
                {
                    await SaslAnonymousNegotiator
                        .NegotiateAsync(plain, trace: null, cancellationToken)
                        .ConfigureAwait(false);
                    return plain;
                }
                catch
                {
                    await plain.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
            }

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
            var transport = new TcpAmqpTransport(tcp, ssl, ssl);
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
    /// Transport that owns the TCP client and (optionally) the SSL
    /// stream so disposal cascades through both layers. The pipe
    /// wrappers are produced by <see cref="StreamAmqpTransport"/>.
    /// </summary>
    private sealed class TcpAmqpTransport : IAmqpTransport
    {
        private readonly TcpClient _tcp;
        private readonly SslStream? _ssl;
        private readonly StreamAmqpTransport _inner;
        private int _disposed;

        public TcpAmqpTransport(TcpClient tcp, SslStream? sslStream, System.IO.Stream amqpStream)
        {
            _tcp = tcp;
            _ssl = sslStream;
            _inner = new StreamAmqpTransport(amqpStream, leaveOpen: true);
        }

        public System.IO.Pipelines.PipeReader Input => _inner.Input;
        public System.IO.Pipelines.PipeWriter Output => _inner.Output;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            await _inner.DisposeAsync().ConfigureAwait(false);
            if (_ssl is not null) await _ssl.DisposeAsync().ConfigureAwait(false);
            _tcp.Dispose();
        }
    }
}

