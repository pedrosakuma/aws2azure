using Aws2Azure.Amqp.Connection;
using Aws2Azure.Amqp.Framing;
using Aws2Azure.Amqp.Management;

namespace Aws2Azure.Amqp.Security;

/// <summary>
/// Implements the Service Bus CBS (Claims-Based Security) handshake
/// over an AMQP <c>$cbs</c> management link:
/// <list type="bullet">
///   <item>Sends a <c>put-token</c> request whose body is the SAS token
///   string and whose application-properties carry <c>operation=put-token</c>,
///   <c>type=&lt;tokenType&gt;</c>, <c>name=&lt;audience&gt;</c>.</item>
///   <item>Awaits the response and validates <c>status-code=202</c>.</item>
/// </list>
/// Renews tokens as needed; tracks the last successful expiry per audience.
/// </summary>
internal sealed class CbsAuthenticator : IAsyncDisposable
{
    private const string CbsAddress = "$cbs";
    private const int StatusAccepted = 202;

    private readonly IAmqpTokenProvider _tokenProvider;
    private readonly AmqpSession _session;
    private readonly AmqpRequestResponseLink _link;
    private int _opened;
    private int _disposed;

    public CbsAuthenticator(AmqpSession session, IAmqpTokenProvider tokenProvider)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(tokenProvider);
        _session = session;
        _tokenProvider = tokenProvider;
        _link = new AmqpRequestResponseLink(session, new AmqpRequestResponseLinkSettings
        {
            Address = CbsAddress,
            InitialReceiverCredit = 16,
        });
    }

    /// <summary>Opens the CBS management link. Must be called once before <see cref="PutTokenAsync"/>.</summary>
    public async Task OpenAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _opened, 1) != 0)
            throw new InvalidOperationException("CbsAuthenticator is already opened.");
        await _link.OpenAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Authorises the given <paramref name="audience"/> by sending a
    /// CBS <c>put-token</c> with a fresh SAS token. Returns the token's
    /// expiry on success; throws <see cref="CbsAuthenticationException"/>
    /// on a non-2xx status-code from the broker.
    /// </summary>
    public async Task<DateTimeOffset?> PutTokenAsync(string audience, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(audience);
        if (Volatile.Read(ref _opened) == 0)
            throw new InvalidOperationException("OpenAsync must be called before PutTokenAsync.");

        var token = _tokenProvider.GetToken(audience);
        var request = new AmqpMessage
        {
            ApplicationProperties = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["operation"] = "put-token",
                ["type"] = _tokenProvider.TokenType,
                ["name"] = audience,
            },
            BodyValueString = token.Value,
        };

        var response = await _link.SendRequestAsync(request, cancellationToken).ConfigureAwait(false);
        var statusCode = ExtractStatusCode(response);
        var statusDescription = ExtractStatusDescription(response);

        if (statusCode != StatusAccepted)
            throw new CbsAuthenticationException(audience, statusCode, statusDescription);

        return token.ExpiresAtUtc;
    }

    private static int ExtractStatusCode(AmqpMessage response)
    {
        if (response.ApplicationProperties is { } ap
            && ap.TryGetValue("status-code", out var raw)
            && raw is int code)
            return code;
        return -1;
    }

    private static string? ExtractStatusDescription(AmqpMessage response)
    {
        if (response.ApplicationProperties is { } ap
            && ap.TryGetValue("status-description", out var raw)
            && raw is string desc)
            return desc;
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _link.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>Thrown when the CBS <c>put-token</c> handshake returns a non-2xx status.</summary>
internal sealed class CbsAuthenticationException : Exception
{
    public CbsAuthenticationException(string audience, int statusCode, string? statusDescription)
        : base($"CBS put-token failed for audience '{audience}': status={statusCode} description={statusDescription ?? "<none>"}")
    {
        Audience = audience;
        StatusCode = statusCode;
        StatusDescription = statusDescription;
    }

    public string Audience { get; }
    public int StatusCode { get; }
    public string? StatusDescription { get; }
}
