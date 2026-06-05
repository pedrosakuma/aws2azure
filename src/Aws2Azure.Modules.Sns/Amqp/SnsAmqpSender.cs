using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Aws2Azure.Amqp.Connection;
using Aws2Azure.Amqp.Framing;
using Aws2Azure.Amqp.Security;
using Aws2Azure.Amqp.ServiceBus;
using Aws2Azure.Core.Azure;
using Aws2Azure.Core.Configuration;

namespace Aws2Azure.Modules.Sns.Amqp;

internal interface ISnsAmqpSender
{
    Task SendAsync(
        ServiceBusTopicsCredentials credentials,
        string namespaceFqdn,
        string topicName,
        SnsAmqpSendMessage message,
        CancellationToken cancellationToken);

    Task<SnsBatchSendResult> SendBatchAsync(
        ServiceBusTopicsCredentials credentials,
        string namespaceFqdn,
        string topicName,
        IReadOnlyList<SnsAmqpSendMessage> messages,
        CancellationToken cancellationToken);
}

internal readonly record struct SnsAmqpSendMessage(
    ReadOnlyMemory<byte> Body,
    AmqpProperties Properties,
    IReadOnlyDictionary<string, object?>? ApplicationProperties);

internal sealed record SnsBatchSendResult(IReadOnlyList<SnsBatchSendOutcome> Outcomes);

internal sealed record SnsBatchSendOutcome(bool Succeeded, string? ErrorCode, string? ErrorMessage, bool SenderFault);

internal enum SnsAmqpFailureKind
{
    Unknown = 0,
    Transient,
    Throttled,
    Auth,
    ClientFatal,
    ServerFatal,
    Redirect,
}

internal sealed class SnsAmqpException : Exception
{
    public SnsAmqpException(
        string message,
        Exception innerException,
        SnsAmqpFailureKind kind,
        string? condition = null,
        string? description = null)
        : base(message, innerException)
    {
        Kind = kind;
        Condition = condition;
        Description = description;
    }

    public SnsAmqpFailureKind Kind { get; }
    public string? Condition { get; }
    public string? Description { get; }
}

internal sealed class SnsAmqpSender : ISnsAmqpSender, IAsyncDisposable
{
    private readonly EntraIdTokenProvider _tokenProvider;
    private readonly AmqpConnectionSettings _connectionSettings;
    private readonly ConcurrentDictionary<ConnectionKey, ConnectionSlot> _connections = new();
    private int _disposed;

    public SnsAmqpSender(EntraIdTokenProvider tokenProvider, AmqpConnectionSettings connectionSettings)
    {
        ArgumentNullException.ThrowIfNull(tokenProvider);
        ArgumentNullException.ThrowIfNull(connectionSettings);
        _tokenProvider = tokenProvider;
        _connectionSettings = connectionSettings;
    }

    public async Task SendAsync(
        ServiceBusTopicsCredentials credentials,
        string namespaceFqdn,
        string topicName,
        SnsAmqpSendMessage message,
        CancellationToken cancellationToken)
    {
        var endpoint = ResolveEndpoint(credentials, namespaceFqdn);
        var key = new ConnectionKey(endpoint, BuildCredentialMarker(credentials));
        var slot = _connections.GetOrAdd(key, static _ => new ConnectionSlot());
        var connection = await slot.GetOrCreateConnectionAsync(
                _tokenProvider,
                _connectionSettings,
                credentials,
                endpoint,
                cancellationToken)
            .ConfigureAwait(false);

        try
        {
            await using var sender = await connection.OpenSenderAsync(topicName, BuildAudience(namespaceFqdn, topicName), cancellationToken).ConfigureAwait(false);
            await sender.SendAsync(ToAmqpMessage(message), settled: false, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (TryWrap(ex, out var wrapped))
        {
            await InvalidateConnectionAsync(key).ConfigureAwait(false);
            throw wrapped;
        }
    }

    public async Task<SnsBatchSendResult> SendBatchAsync(
        ServiceBusTopicsCredentials credentials,
        string namespaceFqdn,
        string topicName,
        IReadOnlyList<SnsAmqpSendMessage> messages,
        CancellationToken cancellationToken)
    {
        if (messages.Count == 0)
        {
            return new SnsBatchSendResult([]);
        }

        var endpoint = ResolveEndpoint(credentials, namespaceFqdn);
        var key = new ConnectionKey(endpoint, BuildCredentialMarker(credentials));
        var slot = _connections.GetOrAdd(key, static _ => new ConnectionSlot());
        var connection = await slot.GetOrCreateConnectionAsync(
                _tokenProvider,
                _connectionSettings,
                credentials,
                endpoint,
                cancellationToken)
            .ConfigureAwait(false);

        var outcomes = new SnsBatchSendOutcome[messages.Count];
        try
        {
            await using var sender = await connection.OpenSenderAsync(topicName, BuildAudience(namespaceFqdn, topicName), cancellationToken).ConfigureAwait(false);
            for (var i = 0; i < messages.Count; i++)
            {
                try
                {
                    await sender.SendAsync(ToAmqpMessage(messages[i]), settled: false, cancellationToken).ConfigureAwait(false);
                    outcomes[i] = new SnsBatchSendOutcome(true, null, null, false);
                }
                catch (ServiceBusSendException sendException)
                {
                    outcomes[i] = CreateBatchOutcome(new SnsAmqpException(
                        "Azure Service Bus Topics rejected the AMQP transfer.",
                        sendException,
                        MapOutcome(sendException.Outcome)));
                }
                catch (Exception ex) when (TryWrap(ex, out var wrapped))
                {
                    await InvalidateConnectionAsync(key).ConfigureAwait(false);
                    if (wrapped.Kind == SnsAmqpFailureKind.Auth)
                    {
                        throw wrapped;
                    }

                    var failure = CreateBatchOutcome(wrapped);
                    for (var remaining = i; remaining < outcomes.Length; remaining++)
                    {
                        outcomes[remaining] = failure;
                    }

                    return new SnsBatchSendResult(outcomes);
                }
            }

            return new SnsBatchSendResult(outcomes);
        }
        catch (Exception ex) when (TryWrap(ex, out var wrapped))
        {
            await InvalidateConnectionAsync(key).ConfigureAwait(false);
            if (wrapped.Kind == SnsAmqpFailureKind.Auth)
            {
                throw wrapped;
            }

            var failure = CreateBatchOutcome(wrapped);
            for (var i = 0; i < outcomes.Length; i++)
            {
                outcomes[i] = failure;
            }

            return new SnsBatchSendResult(outcomes);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var entry in _connections.ToArray())
        {
            if (_connections.TryRemove(entry))
            {
                await entry.Value.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static AmqpMessage ToAmqpMessage(SnsAmqpSendMessage message)
        => new()
        {
            Body = message.Body,
            Properties = message.Properties,
            ApplicationProperties = message.ApplicationProperties is null
                ? null
                : new Dictionary<string, object?>(message.ApplicationProperties, StringComparer.Ordinal),
        };

    private async Task InvalidateConnectionAsync(ConnectionKey key)
    {
        if (_connections.TryRemove(key, out var slot))
        {
            await slot.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static ServiceBusAmqpEndpoint ResolveEndpoint(ServiceBusTopicsCredentials credentials, string namespaceFqdn)
    {
        if (!string.IsNullOrWhiteSpace(credentials.Endpoint)
            && Uri.TryCreate(credentials.Endpoint, UriKind.Absolute, out var endpointUri))
        {
            var logicalNamespace = namespaceFqdn.Trim().ToLowerInvariant();
            if (string.Equals(endpointUri.Scheme, "amqp", StringComparison.OrdinalIgnoreCase)
                || string.Equals(endpointUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            {
                return ServiceBusAmqpEndpoint.Plain(endpointUri.Host, endpointUri.IsDefaultPort ? ServiceBusEndpoint.AmqpPort : endpointUri.Port, logicalNamespace);
            }

            return ServiceBusAmqpEndpoint.Tls(endpointUri.Host, endpointUri.IsDefaultPort ? ServiceBusEndpoint.AmqpsPort : endpointUri.Port, logicalNamespace);
        }

        return ServiceBusAmqpEndpoint.Tls(namespaceFqdn.Trim().ToLowerInvariant());
    }

    private static string BuildAudience(string namespaceFqdn, string entityPath)
        => "amqps://" + namespaceFqdn.Trim().TrimEnd('/') + "/" + entityPath.Trim().TrimStart('/');

    private static string BuildCredentialMarker(ServiceBusTopicsCredentials credentials)
        => !string.IsNullOrWhiteSpace(credentials.SasKeyName)
            ? "sas|" + credentials.SasKeyName.Trim()
            : "aad|" + credentials.TenantId + "|" + credentials.ClientId;

    internal static SnsBatchSendOutcome CreateBatchOutcome(SnsAmqpException exception)
        => exception.Kind == SnsAmqpFailureKind.Throttled
            ? new SnsBatchSendOutcome(
                false,
                "Throttled",
                "Azure Service Bus Topics throttled the publish request; retry with back-off.",
                SenderFault: true)
            : new SnsBatchSendOutcome(false, "InternalFailure", BuildFailureMessage(exception), false);

    private static string BuildFailureMessage(SnsAmqpException exception)
    {
        var message = string.Equals(exception.Condition, AmqpErrorCondition.Timeout, StringComparison.Ordinal)
            ? "Azure Service Bus Topics AMQP send timed out."
            : "Azure Service Bus Topics AMQP send failed.";
        if (!string.IsNullOrWhiteSpace(exception.Description))
        {
            message += " " + exception.Description;
        }

        return message;
    }

    internal static bool TryWrap(Exception exception, out SnsAmqpException wrapped)
    {
        switch (exception)
        {
            case SnsAmqpException alreadyWrapped:
                wrapped = alreadyWrapped;
                return true;
            case CbsAuthenticationException cbsAuthentication:
                wrapped = new SnsAmqpException(
                    "Service Bus Topics AMQP authorization failed.",
                    cbsAuthentication,
                    SnsAmqpFailureKind.Auth,
                    description: cbsAuthentication.StatusDescription);
                return true;
            case EntraIdTokenException tokenException:
                // A throttle / transient / auth failure from the Entra ID token
                // endpoint surfaces here when CBS authorization acquires a bearer
                // token during sender open. Classify it so the handler renders the
                // service-native retryable shape instead of a bare 500.
                wrapped = new SnsAmqpException(
                    "Service Bus Topics AMQP authorization failed.",
                    tokenException,
                    MapTokenStatus(tokenException.BackendStatus));
                return true;
            case ServiceBusSendException sendException:
                wrapped = new SnsAmqpException(
                    "Service Bus Topics rejected the AMQP transfer.",
                    sendException,
                    MapOutcome(sendException.Outcome));
                return true;
            case AmqpLinkException linkException:
                wrapped = new SnsAmqpException(
                    linkException.Message,
                    linkException,
                    MapKind(linkException.Kind),
                    linkException.PeerCondition,
                    linkException.PeerDescription);
                return true;
            case AmqpConnectionException connectionException:
                wrapped = new SnsAmqpException(
                    connectionException.Message,
                    connectionException,
                    MapKind(connectionException.Kind));
                return true;
            case TimeoutException timeoutException:
                wrapped = new SnsAmqpException(
                    "Service Bus Topics AMQP send timed out.",
                    timeoutException,
                    SnsAmqpFailureKind.Transient,
                    AmqpErrorCondition.Timeout,
                    timeoutException.Message);
                return true;
            default:
                wrapped = default!;
                return false;
        }
    }

    private static SnsAmqpFailureKind MapKind(AmqpErrorKind kind) => kind switch
    {
        AmqpErrorKind.Transient => SnsAmqpFailureKind.Transient,
        AmqpErrorKind.Throttled => SnsAmqpFailureKind.Throttled,
        AmqpErrorKind.Auth => SnsAmqpFailureKind.Auth,
        AmqpErrorKind.ClientFatal => SnsAmqpFailureKind.ClientFatal,
        AmqpErrorKind.ServerFatal => SnsAmqpFailureKind.ServerFatal,
        AmqpErrorKind.Redirect => SnsAmqpFailureKind.Redirect,
        _ => SnsAmqpFailureKind.Unknown,
    };

    private static SnsAmqpFailureKind MapTokenStatus(HttpStatusCode backendStatus) => backendStatus switch
    {
        HttpStatusCode.TooManyRequests => SnsAmqpFailureKind.Throttled,
        HttpStatusCode.ServiceUnavailable => SnsAmqpFailureKind.Transient,
        _ => SnsAmqpFailureKind.Auth,
    };

    private static SnsAmqpFailureKind MapOutcome(AmqpDispositionOutcome outcome) => outcome switch
    {
        AmqpDispositionOutcome.Released => SnsAmqpFailureKind.Transient,
        AmqpDispositionOutcome.Modified => SnsAmqpFailureKind.Throttled,
        _ => SnsAmqpFailureKind.Unknown,
    };

    private readonly record struct ConnectionKey(ServiceBusAmqpEndpoint Endpoint, string CredentialMarker);

    private sealed class ConnectionSlot : IAsyncDisposable
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private ServiceBusAmqpConnection? _connection;
        private int _disposed;

        public async Task<ServiceBusAmqpConnection> GetOrCreateConnectionAsync(
            EntraIdTokenProvider tokenProvider,
            AmqpConnectionSettings connectionSettings,
            ServiceBusTopicsCredentials credentials,
            ServiceBusAmqpEndpoint endpoint,
            CancellationToken cancellationToken)
        {
            var existing = Volatile.Read(ref _connection);
            if (existing is not null && !existing.IsClosed)
            {
                return existing;
            }

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                existing = _connection;
                if (existing is not null && !existing.IsClosed)
                {
                    return existing;
                }

                if (existing is not null)
                {
                    try { await existing.DisposeAsync().ConfigureAwait(false); } catch { }
                    Volatile.Write(ref _connection, null);
                }

                var transport = await ServiceBusAmqpConnector.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
                try
                {
                    var created = await ServiceBusAmqpConnection.OpenAsync(
                            transport,
                            CreateTokenProvider(tokenProvider, credentials),
                            connectionSettings with { Hostname = endpoint.LogicalNamespace },
                            cancellationToken)
                        .ConfigureAwait(false);
                    Volatile.Write(ref _connection, created);
                    return created;
                }
                catch
                {
                    await transport.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_connection is not null)
                {
                    await _connection.DisposeAsync().ConfigureAwait(false);
                    Volatile.Write(ref _connection, null);
                }
            }
            finally
            {
                _gate.Release();
                _gate.Dispose();
            }
        }

        private static IAmqpTokenProvider CreateTokenProvider(EntraIdTokenProvider tokenProvider, ServiceBusTopicsCredentials credentials)
        {
            if (!string.IsNullOrWhiteSpace(credentials.SasKeyName)
                && !string.IsNullOrWhiteSpace(credentials.SasKey))
            {
                return new ServiceBusSasTokenProvider(credentials.SasKeyName, credentials.SasKey);
            }

            return new ServiceBusTopicsBearerTokenProvider(tokenProvider, credentials);
        }

        private sealed class ServiceBusTopicsBearerTokenProvider : IAmqpTokenProvider
        {
            private readonly EntraIdTokenProvider _tokenProvider;
            private readonly ServiceBusTopicsCredentials _credentials;

            public ServiceBusTopicsBearerTokenProvider(EntraIdTokenProvider tokenProvider, ServiceBusTopicsCredentials credentials)
            {
                _tokenProvider = tokenProvider;
                _credentials = credentials;
            }

            public string TokenType => "jwt";

            public AmqpToken GetToken(string audience)
            {
                _ = audience;
                var token = _tokenProvider
                    .GetTokenAsync(
                        _credentials.TenantId!,
                        _credentials.ClientId!,
                        _credentials.ClientSecret!,
                        "https://servicebus.azure.net/.default")
                    .AsTask()
                    .GetAwaiter()
                    .GetResult();
                return new AmqpToken(token, TryParseJwtExpiry(token));
            }

            private static DateTimeOffset? TryParseJwtExpiry(string token)
            {
                if (string.IsNullOrWhiteSpace(token))
                {
                    return null;
                }

                var firstDot = token.IndexOf('.');
                if (firstDot < 0)
                {
                    return null;
                }

                var secondDot = token.IndexOf('.', firstDot + 1);
                if (secondDot < 0)
                {
                    return null;
                }

                var payload = token.AsSpan(firstDot + 1, secondDot - firstDot - 1);
                if (payload.IsEmpty)
                {
                    return null;
                }

                var requiredLength = GetBase64UrlDecodedLength(payload.Length);
                var rented = ArrayPool<byte>.Shared.Rent(requiredLength);
                try
                {
                    if (!TryDecodeBase64Url(payload, rented, out var written))
                    {
                        return null;
                    }

                    var reader = new Utf8JsonReader(rented.AsSpan(0, written), true, default);
                    while (reader.Read())
                    {
                        if (reader.TokenType == JsonTokenType.PropertyName
                            && reader.ValueTextEquals("exp")
                            && reader.Read()
                            && reader.TokenType == JsonTokenType.Number
                            && reader.TryGetInt64(out var seconds))
                        {
                            return DateTimeOffset.FromUnixTimeSeconds(seconds);
                        }
                    }

                    return null;
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(rented);
                }
            }

            private static int GetBase64UrlDecodedLength(int encodedLength)
            {
                var paddedLength = encodedLength + ((4 - (encodedLength % 4)) % 4);
                return (paddedLength / 4) * 3;
            }

            private static bool TryDecodeBase64Url(ReadOnlySpan<char> encoded, Span<byte> destination, out int written)
            {
                var paddedLength = encoded.Length + ((4 - (encoded.Length % 4)) % 4);
                var rented = ArrayPool<char>.Shared.Rent(paddedLength);
                try
                {
                    for (var i = 0; i < encoded.Length; i++)
                    {
                        rented[i] = encoded[i] switch
                        {
                            '-' => '+',
                            '_' => '/',
                            var value => value,
                        };
                    }

                    for (var i = encoded.Length; i < paddedLength; i++)
                    {
                        rented[i] = '=';
                    }

                    return Convert.TryFromBase64Chars(rented.AsSpan(0, paddedLength), destination, out written);
                }
                finally
                {
                    ArrayPool<char>.Shared.Return(rented);
                }
            }
        }
    }
}
