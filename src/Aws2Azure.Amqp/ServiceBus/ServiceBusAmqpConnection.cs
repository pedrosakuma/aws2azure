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
    private readonly TimeProvider _clock;
    private readonly TimeSpan _refreshSafetyWindow;
    private AmqpSession? _dataSession;
    private int _disposed;

    private ServiceBusAmqpConnection(
        AmqpConnection connection,
        AmqpSession cbsSession,
        CbsAuthenticator cbs,
        TimeProvider clock,
        TimeSpan refreshSafetyWindow)
    {
        _connection = connection;
        _cbsSession = cbsSession;
        _cbs = cbs;
        _clock = clock;
        _refreshSafetyWindow = refreshSafetyWindow;
    }

    /// <summary>The underlying AMQP connection. Exposed for diagnostics.</summary>
    public AmqpConnection Connection => _connection;

    /// <summary>
    /// True when the underlying AMQP connection is no longer in the
    /// <c>Opened</c> state (closed by peer, closing locally, or final).
    /// Pool slots check this to evict and rebuild before handing the
    /// connection back to the next caller.
    /// </summary>
    public bool IsClosed => _connection.IsClosed;

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
        => await OpenAsync(transport, tokenProvider, connectionSettings, clock: null, refreshSafetyWindow: null, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Opens the connection with explicit clock + refresh-safety overrides.
    /// Tests inject a fake <see cref="TimeProvider"/> to drive the
    /// proactive CBS-token renewal path deterministically.
    /// </summary>
    internal static async Task<ServiceBusAmqpConnection> OpenAsync(
        IAmqpTransport transport,
        IAmqpTokenProvider tokenProvider,
        AmqpConnectionSettings connectionSettings,
        TimeProvider? clock,
        TimeSpan? refreshSafetyWindow,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(tokenProvider);
        ArgumentNullException.ThrowIfNull(connectionSettings);

        var effectiveClock = clock ?? TimeProvider.System;
        // 5-minute window mirrors EntraIdTokenProvider; Service Bus rejects
        // tokens that are about to expire and detaches links unceremoniously,
        // so we want the refresh to happen well before the broker notices.
        var effectiveSafety = refreshSafetyWindow ?? TimeSpan.FromMinutes(5);

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
            return new ServiceBusAmqpConnection(connection, cbsSession, cbs, effectiveClock, effectiveSafety);
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
    public Task<ServiceBusReceiver> OpenReceiverAsync(
        string queueName,
        string audience,
        uint prefetchCredit,
        CancellationToken cancellationToken = default)
        => OpenReceiverAsync(queueName, audience, prefetchCredit, ReadOnlyMemory<byte>.Empty, cancellationToken);

    internal async Task<ServiceBusReceiver> OpenReceiverAsync(
        string queueName,
        string audience,
        uint prefetchCredit,
        ReadOnlyMemory<byte> sourceFilter,
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
            SourceFilter = sourceFilter,
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
        CancellationToken cancellationToken = default,
        TimeSpan? acceptNextTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queueName);
        ArgumentException.ThrowIfNullOrWhiteSpace(audience);
        ThrowIfDisposed();

        await EnsureAuthorizedAsync(audience, cancellationToken).ConfigureAwait(false);
        var dataSession = await EnsureDataSessionAsync(cancellationToken).ConfigureAwait(false);

        var filter = ServiceBusSessionFilter.Encode(sessionId);
        var linkName = $"aws2azure-recv-{queueName}-session-{Guid.NewGuid():N}";
        var settings = new AmqpLinkSettings
        {
            Name = linkName,
            Role = AmqpRole.Receiver,
            SourceAddress = ServiceBusEndpoint.BuildReceiverSourceAddress(queueName),
            SourceFilter = filter,
            TargetAddress = linkName,
            SenderSettleMode = AmqpSenderSettleMode.Unsettled,
            ReceiverSettleMode = AmqpReceiverSettleMode.Second,
            InitialDeliveryCount = null,
            Properties = sessionId is null && acceptNextTimeout is { } timeout
                ? ServiceBusSessionFilter.EncodeAcceptNextProperties(timeout)
                : ReadOnlyMemory<byte>.Empty,
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
            if (sessionId is null)
            {
                if (remoteSource.IsEmpty)
                    throw CreateBrokerAssignedSessionUnavailable(settings.Name);

                var src = DecodeRemoteSource(remoteSource, settings.Name);
                if (src.Filter.IsEmpty)
                    throw CreateBrokerAssignedSessionUnavailable(settings.Name);
                boundSessionId = DecodeAssignedSessionFilter(src.Filter, settings.Name);
            }
            else if (!remoteSource.IsEmpty)
            {
                var src = DecodeRemoteSource(remoteSource, settings.Name);
                if (!src.Filter.IsEmpty &&
                    ServiceBusSessionFilter.TryDecode(src.Filter, out var assigned) &&
                    assigned is not null)
                    boundSessionId = assigned;
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

    private static BrokerAssignedSessionUnavailableException CreateBrokerAssignedSessionUnavailable(
        string linkName) =>
        new(
            $"Service Bus did not bind a session to receiver link '{linkName}': " +
            "the attach response carried no com.microsoft:session-filter.");

    private static AmqpSource DecodeRemoteSource(
        ReadOnlyMemory<byte> source,
        string linkName)
    {
        try
        {
            AmqpSource.Read(source, out var decoded, out _);
            return decoded;
        }
        catch (Exception ex) when (
            ex is InvalidDataException
                or System.Text.DecoderFallbackException
                or ArgumentException
                or IndexOutOfRangeException
                or OverflowException)
        {
            throw new InvalidDataException(
                $"Service Bus returned a malformed source for receiver link '{linkName}'.",
                ex);
        }
    }

    private static string DecodeAssignedSessionFilter(
        ReadOnlyMemory<byte> filter,
        string linkName)
    {
        try
        {
            if (ServiceBusSessionFilter.TryDecode(filter, out var assigned))
            {
                if (assigned is null)
                    throw CreateBrokerAssignedSessionUnavailable(linkName);
                return assigned;
            }
        }
        catch (BrokerAssignedSessionUnavailableException) { throw; }
        catch (Exception ex) when (
            ex is System.Text.DecoderFallbackException
                or ArgumentException
                or IndexOutOfRangeException
                or OverflowException)
        {
            throw new InvalidDataException(
                $"Service Bus returned a malformed session filter for receiver link '{linkName}'.",
                ex);
        }

        throw new InvalidDataException(
            $"Service Bus returned an invalid session filter for receiver link '{linkName}'.");
    }

    /// <summary>
    /// Signals that an accept-next session attach completed without a bound
    /// session because no active unlocked session was available.
    /// </summary>
    internal sealed class BrokerAssignedSessionUnavailableException : InvalidOperationException
    {
        public BrokerAssignedSessionUnavailableException(string message) : base(message) { }
    }

    /// <summary>
    /// Opens a sender link against <paramref name="queueName"/> after
    /// CBS-authorising <paramref name="audience"/>. Symmetric to
    /// <see cref="OpenReceiverAsync"/>.
    /// </summary>
    public async Task<ServiceBusAmqpSender> OpenSenderAsync(
        string queueName,
        string audience,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queueName);
        ArgumentException.ThrowIfNullOrWhiteSpace(audience);
        ThrowIfDisposed();

        await EnsureAuthorizedAsync(audience, cancellationToken).ConfigureAwait(false);
        var dataSession = await EnsureDataSessionAsync(cancellationToken).ConfigureAwait(false);

        var settings = new AmqpLinkSettings
        {
            Name = $"aws2azure-send-{queueName}-{Guid.NewGuid():N}",
            Role = AmqpRole.Sender,
            SourceAddress = null,
            TargetAddress = ServiceBusEndpoint.BuildSenderTargetAddress(queueName),
            SenderSettleMode = AmqpSenderSettleMode.Unsettled,
            ReceiverSettleMode = AmqpReceiverSettleMode.First,
            InitialDeliveryCount = 0,
        };

        var link = await dataSession.AttachLinkAsync(settings, cancellationToken).ConfigureAwait(false);
        try
        {
            return new ServiceBusAmqpSender(link, queueName);
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
    /// Opens a Service Bus entity-scoped <c>$management</c>
    /// request-response client. CBS-authorises
    /// <paramref name="managementAudience"/> if needed, then attaches the
    /// paired sender/receiver at <paramref name="managementAddress"/> on the
    /// shared data session. Caller owns the returned client and must dispose
    /// it when done.
    /// </summary>
    public async Task<ServiceBusManagementClient> OpenManagementClientAsync(
        string managementAddress,
        string managementAudience,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(managementAddress);
        ArgumentException.ThrowIfNullOrWhiteSpace(managementAudience);
        ThrowIfDisposed();

        await EnsureAuthorizedAsync(managementAudience, cancellationToken).ConfigureAwait(false);
        var dataSession = await EnsureDataSessionAsync(cancellationToken).ConfigureAwait(false);
        return await ServiceBusManagementClient
            .OpenAsync(dataSession, managementAddress, cancellationToken)
            .ConfigureAwait(false);
    }


    private async Task EnsureAuthorizedAsync(string audience, CancellationToken cancellationToken)
    {
        // Lock-free fast path: ConcurrentDictionary makes the read safe to
        // race against concurrent writers under the lock. We refresh the
        // token when its expiry is within `_refreshSafetyWindow` from now —
        // Service Bus rejects requests carrying an about-to-expire token
        // and detaches links without warning, so we proactively renew well
        // before the broker would notice. A null cached expiry means
        // "the token has no advertised expiry" (some custom providers) and
        // is treated as never-expires.
        if (_authorized.TryGetValue(audience, out var cached) && !ShouldRefresh(cached)) return;

        await _authorizeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Re-check under the lock — another waiter may have refreshed.
            if (_authorized.TryGetValue(audience, out cached) && !ShouldRefresh(cached)) return;
            var expiry = await _cbs.PutTokenAsync(audience, cancellationToken).ConfigureAwait(false);
            _authorized[audience] = expiry;
        }
        finally
        {
            _authorizeLock.Release();
        }
    }

    private bool ShouldRefresh(DateTimeOffset? cachedExpiry)
        => ShouldRefreshAuthorization(cachedExpiry, _clock.GetUtcNow(), _refreshSafetyWindow);

    /// <summary>
    /// Pure decision function for the CBS proactive-renewal cache.
    /// Refreshes when <paramref name="cachedExpiry"/> is within
    /// <paramref name="safetyWindow"/> of <paramref name="now"/>; never
    /// refreshes when the cached entry has no expiry. Extracted to be
    /// directly unit-testable without spinning up the AMQP broker stub.
    /// </summary>
    internal static bool ShouldRefreshAuthorization(
        DateTimeOffset? cachedExpiry,
        DateTimeOffset now,
        TimeSpan safetyWindow)
    {
        if (cachedExpiry is not { } expiry) return false;
        return now + safetyWindow >= expiry;
    }

    private async Task<AmqpSession> EnsureDataSessionAsync(CancellationToken cancellationToken)
    {
        var existing = Volatile.Read(ref _dataSession);
        if (existing is { IsClosed: false }) return existing;
        await _dataSessionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            existing = _dataSession;
            if (existing is { IsClosed: false }) return existing;
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
