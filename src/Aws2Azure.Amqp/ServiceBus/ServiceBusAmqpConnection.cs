using System.Collections.Concurrent;
using Aws2Azure.Amqp.Connection;
using Aws2Azure.Amqp.Framing;
using Aws2Azure.Amqp.Security;
using Aws2Azure.Amqp.Transport;

namespace Aws2Azure.Amqp.ServiceBus;

/// <summary>
/// Service Bus AMQP transport orchestrator. Owns one AMQP connection
/// plus two sessions:
/// <list type="bullet">
///   <item>a dedicated CBS session running an
///   <see cref="CbsAuthenticator"/> for <c>put-token</c> handshakes;</item>
///   <item>a shared data session hosting receiver (and, in later
///   slices, sender) links.</item>
/// </list>
/// <para>
/// The split mirrors what the Azure Service Bus SDK does: keeping CBS
/// isolated lets a transient link-level failure on a queue link disturb
/// only that link, without invalidating the token cache for the rest of
/// the namespace.
/// </para>
/// <para>
/// Authorisation is per-audience and cached for the lifetime of the
/// connection. Token renewal (before <c>expiresAtUtc</c>) is intentionally
/// out of scope for Slice 8a — the <c>resilience-cbs-token-cache</c>
/// follow-up adds proactive refresh.
/// </para>
/// </summary>
internal sealed class ServiceBusAmqpConnection : IAsyncDisposable
{
    private readonly AmqpConnection _connection;
    private readonly AmqpSession _cbsSession;
    private readonly CbsAuthenticator _cbs;
    private readonly SemaphoreSlim _authorizeLock = new(1, 1);
    private readonly SemaphoreSlim _dataSessionLock = new(1, 1);
    private readonly ConcurrentDictionary<string, DateTimeOffset?> _authorized =
        new(StringComparer.Ordinal);
    private AmqpSession? _dataSession;
    private int _disposed;

    private ServiceBusAmqpConnection(
        AmqpConnection connection,
        AmqpSession cbsSession,
        CbsAuthenticator cbs)
    {
        _connection = connection;
        _cbsSession = cbsSession;
        _cbs = cbs;
    }

    /// <summary>The underlying AMQP connection. Exposed for diagnostics.</summary>
    public AmqpConnection Connection => _connection;

    /// <summary>
    /// Opens the AMQP connection, the CBS + data sessions, and the CBS
    /// link. The caller is responsible for handing in a transport that is
    /// already past TLS + SASL negotiation — see
    /// <see cref="ServiceBusAmqpTlsConnector"/> for the production path,
    /// or pass an in-process duplex transport directly from tests.
    /// </summary>
    public static async Task<ServiceBusAmqpConnection> OpenAsync(
        IAmqpTransport transport,
        IAmqpTokenProvider tokenProvider,
        AmqpConnectionSettings connectionSettings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(tokenProvider);
        ArgumentNullException.ThrowIfNull(connectionSettings);

        var connection = new AmqpConnection(transport, connectionSettings);
        AmqpSession? cbsSession = null;
        CbsAuthenticator? cbs = null;
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            cbsSession = await connection.BeginSessionAsync(new AmqpSessionSettings(), cancellationToken).ConfigureAwait(false);

            cbs = new CbsAuthenticator(cbsSession, tokenProvider);
            await cbs.OpenAsync(cancellationToken).ConfigureAwait(false);

            // The data session is opened lazily on first OpenReceiverAsync,
            // strictly *after* the first successful put-token. This matches
            // the Service Bus protocol expectation:
            // open → begin(cbs) → attach cbs → put-token → begin(data) → attach receiver.
            return new ServiceBusAmqpConnection(connection, cbsSession, cbs);
        }
        catch
        {
            if (cbs is not null) await cbs.DisposeAsync().ConfigureAwait(false);
            // Sessions close transitively when the connection does — no
            // dedicated cleanup needed here, the connection's close path
            // tears them down.
            try { await connection.CloseAsync(cancellationToken: CancellationToken.None).ConfigureAwait(false); }
            catch { /* swallow during cleanup */ }
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Authorises <paramref name="audience"/> via CBS (no-op if already
    /// authorised on this connection) and opens a receiver link against
    /// <paramref name="queueName"/> on the shared data session.
    /// </summary>
    /// <param name="prefetchCredit">
    /// Initial link credit issued at attach time. Pass <c>0</c> when the
    /// caller intends to manage credit explicitly via
    /// <see cref="ServiceBusReceiver.ReceiveBatchAsync(int, TimeSpan, CancellationToken)"/>
    /// (which grants credit additively itself).
    /// </param>
    public async Task<ServiceBusReceiver> OpenReceiverAsync(
        string queueName,
        string audience,
        uint prefetchCredit,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queueName);
        ArgumentException.ThrowIfNullOrWhiteSpace(audience);
        ThrowIfDisposed();

        await EnsureAuthorizedAsync(audience, cancellationToken).ConfigureAwait(false);
        var dataSession = await EnsureDataSessionAsync(cancellationToken).ConfigureAwait(false);

        var settings = new AmqpLinkSettings
        {
            Name = $"aws2azure-recv-{queueName}-{Guid.NewGuid():N}",
            Role = AmqpRole.Receiver,
            SourceAddress = ServiceBusEndpoint.BuildReceiverSourceAddress(queueName),
            TargetAddress = null,
            SenderSettleMode = AmqpSenderSettleMode.Unsettled,
            ReceiverSettleMode = AmqpReceiverSettleMode.First,
            InitialDeliveryCount = null,
        };

        var link = await dataSession.AttachLinkAsync(settings, cancellationToken).ConfigureAwait(false);
        try
        {
            if (prefetchCredit > 0)
                await link.GrantCreditAsync(prefetchCredit, cancellationToken).ConfigureAwait(false);
            return new ServiceBusReceiver(link, queueName);
        }
        catch
        {
            await TryDetachAsync(link).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Opens a session-bound receiver link against
    /// <paramref name="queueName"/> using the Service Bus
    /// <c>com.microsoft:session-filter</c> (slice 7). Pass a specific
    /// <paramref name="sessionId"/> to bind to a known session, or
    /// <c>null</c> to ask the broker to assign any available session —
    /// the actual bound session is returned via
    /// <see cref="ServiceBusReceiver.SessionId"/>, read from the source
    /// filter the broker echoes on the attach response.
    /// </summary>
    /// <param name="prefetchCredit">
    /// Initial link credit issued at attach time. Pass <c>0</c> when the
    /// caller intends to manage credit explicitly.
    /// </param>
    public async Task<ServiceBusReceiver> OpenSessionReceiverAsync(
        string queueName,
        string audience,
        string? sessionId,
        uint prefetchCredit,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queueName);
        ArgumentException.ThrowIfNullOrWhiteSpace(audience);
        ThrowIfDisposed();

        await EnsureAuthorizedAsync(audience, cancellationToken).ConfigureAwait(false);
        var dataSession = await EnsureDataSessionAsync(cancellationToken).ConfigureAwait(false);

        var filter = ServiceBusSessionFilter.Encode(sessionId);
        var settings = new AmqpLinkSettings
        {
            Name = $"aws2azure-recv-{queueName}-session-{Guid.NewGuid():N}",
            Role = AmqpRole.Receiver,
            SourceAddress = ServiceBusEndpoint.BuildReceiverSourceAddress(queueName),
            SourceFilter = filter,
            TargetAddress = null,
            SenderSettleMode = AmqpSenderSettleMode.Unsettled,
            ReceiverSettleMode = AmqpReceiverSettleMode.First,
            InitialDeliveryCount = null,
        };

        var link = await dataSession.AttachLinkAsync(settings, cancellationToken).ConfigureAwait(false);
        try
        {
            // Decode the broker-echoed session-id from the attach
            // response's source.filter. If the broker honoured our
            // requested session-id verbatim it'll match; if we asked
            // for "any" (null) it'll carry the assigned session.
            string? boundSessionId = sessionId;
            var remoteSource = link.RemoteAttach.Source;
            if (!remoteSource.IsEmpty)
            {
                AmqpSource.Read(remoteSource, out var src, out _);
                if (!src.Filter.IsEmpty &&
                    ServiceBusSessionFilter.TryDecode(src.Filter, out var assigned) &&
                    assigned is not null)
                {
                    boundSessionId = assigned;
                }
            }

            // A null sessionId means the caller asked the broker to
            // assign any available session. If we get back here without
            // a concrete bound id, the broker either didn't echo a
            // session-filter or echoed a null value — both of which
            // mean no session was actually bound. Detach and surface
            // a clear protocol error rather than handing back a
            // misleading "session receiver".
            if (boundSessionId is null)
            {
                throw new InvalidOperationException(
                    $"Service Bus did not bind a session to receiver link '{settings.Name}': " +
                    "the attach response carried no com.microsoft:session-filter " +
                    "with an assigned session-id.");
            }

            if (prefetchCredit > 0)
                await link.GrantCreditAsync(prefetchCredit, cancellationToken).ConfigureAwait(false);
            return new ServiceBusReceiver(link, queueName, boundSessionId);
        }
        catch
        {
            await TryDetachAsync(link).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Re-runs CBS <c>put-token</c> for <paramref name="audience"/>
    /// regardless of cache state. Exposed so a future renewal task can
    /// push fresh tokens without tearing down existing links.
    /// </summary>
    public async Task<DateTimeOffset?> RefreshAuthorizationAsync(
        string audience, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(audience);
        ThrowIfDisposed();
        await _authorizeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var expiry = await _cbs.PutTokenAsync(audience, cancellationToken).ConfigureAwait(false);
            _authorized[audience] = expiry;
            return expiry;
        }
        finally
        {
            _authorizeLock.Release();
        }
    }

    /// <summary>
    /// Opens a Service Bus <c>$management</c> request-response client
    /// scoped to <paramref name="audience"/>. CBS-authorises the
    /// audience if needed, then attaches the paired sender/receiver on
    /// the shared data session. Caller owns the returned client and
    /// must dispose it when done.
    /// </summary>
    /// <remarks>
    /// Note the <c>$management</c> node is per-connection on Service
    /// Bus — the address is literally <c>$management</c> regardless of
    /// queue. The CBS-authorised audience scopes which operations the
    /// broker accepts. For renew-lock the audience must match the
    /// entity whose lock-token is being renewed.
    /// </remarks>
    public async Task<ServiceBusManagementClient> OpenManagementClientAsync(
        string audience, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(audience);
        ThrowIfDisposed();

        await EnsureAuthorizedAsync(audience, cancellationToken).ConfigureAwait(false);
        var dataSession = await EnsureDataSessionAsync(cancellationToken).ConfigureAwait(false);
        return await ServiceBusManagementClient.OpenAsync(dataSession, cancellationToken).ConfigureAwait(false);
    }


    private async Task EnsureAuthorizedAsync(string audience, CancellationToken cancellationToken)
    {
        // Lock-free fast path: ConcurrentDictionary makes the read safe to
        // race against concurrent writers under the lock.
        if (_authorized.ContainsKey(audience)) return;
        await _authorizeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_authorized.ContainsKey(audience)) return;
            var expiry = await _cbs.PutTokenAsync(audience, cancellationToken).ConfigureAwait(false);
            _authorized[audience] = expiry;
        }
        finally
        {
            _authorizeLock.Release();
        }
    }

    private async Task<AmqpSession> EnsureDataSessionAsync(CancellationToken cancellationToken)
    {
        var existing = Volatile.Read(ref _dataSession);
        if (existing is not null) return existing;
        await _dataSessionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            existing = _dataSession;
            if (existing is not null) return existing;
            var created = await _connection
                .BeginSessionAsync(new AmqpSessionSettings(), cancellationToken)
                .ConfigureAwait(false);
            Volatile.Write(ref _dataSession, created);
            return created;
        }
        finally
        {
            _dataSessionLock.Release();
        }
    }

    private static async Task TryDetachAsync(AmqpLink link)
    {
        try { await link.DetachAsync(closed: true).ConfigureAwait(false); }
        catch { /* best-effort cleanup */ }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(ServiceBusAmqpConnection));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _cbs.DisposeAsync().ConfigureAwait(false);
        try { await _connection.CloseAsync(cancellationToken: CancellationToken.None).ConfigureAwait(false); }
        catch { /* swallow during shutdown */ }
        await _connection.DisposeAsync().ConfigureAwait(false);
        _authorizeLock.Dispose();
        _dataSessionLock.Dispose();
    }
}
