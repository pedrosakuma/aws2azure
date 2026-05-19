using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Aws2Azure.Core.Azure;
using Aws2Azure.Core.Configuration;

namespace Aws2Azure.Modules.Sqs.Internal;

/// <summary>
/// Thin Azure Service Bus REST client. Owns SAS-token generation and the
/// canonical endpoint resolution, so the per-op handlers (Slice 1+) can
/// stay focused on translation logic.
///
/// <para>
/// Service Bus exposes two surface variants under the same namespace
/// hostname:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <b>Management API</b> (Atom/XML, e.g. <c>PUT /{queue}?api-version=…</c>)
///     for queue creation, ListQueues, GetQueueAttributes.
///   </description></item>
///   <item><description>
///     <b>Runtime API</b> (JSON / brokered messages, e.g.
///     <c>POST /{queue}/messages</c>, <c>POST /{queue}/messages/head</c>)
///     for SendMessage, ReceiveMessage, lock renewal and deletion.
///   </description></item>
/// </list>
///
/// <para>This class is intentionally minimal at Slice 0: it can build a
/// request URI, attach SAS auth, and issue the call. Slice 1+ will add
/// per-op helpers (CreateQueueAsync, GetQueuePropertiesAsync, …) layered on
/// top.</para>
/// </summary>
internal sealed class ServiceBusClient
{
    /// <summary>
    /// Service Bus REST API version pinned across the module. Picked to
    /// match the version that the official emulator
    /// (<c>mcr.microsoft.com/azure-messaging/servicebus-emulator</c>)
    /// implements end-to-end.
    /// </summary>
    public const string ApiVersion = "2021-05";

    private readonly AzureHttpClient _http;
    private readonly ServiceBusSasAuthenticator _auth;
    private readonly Uri _baseEndpoint;
    private readonly string _namespace;

    public ServiceBusClient(AzureHttpClient http, ServiceBusCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(credentials);
        if (string.IsNullOrWhiteSpace(credentials.Namespace))
        {
            throw new ArgumentException("ServiceBusCredentials.Namespace must be set.", nameof(credentials));
        }
        if (string.IsNullOrWhiteSpace(credentials.SasKeyName) || string.IsNullOrWhiteSpace(credentials.SasKey))
        {
            throw new ArgumentException("ServiceBusCredentials.SasKeyName and SasKey must be set.", nameof(credentials));
        }

        _http = http;
        _namespace = credentials.Namespace;
        _baseEndpoint = ResolveEndpoint(credentials.Namespace);
        _auth = new ServiceBusSasAuthenticator(credentials.SasKeyName, credentials.SasKey);
    }

    public Uri BaseEndpoint => _baseEndpoint;
    public string Namespace => _namespace;

    /// <summary>
    /// Builds a URI under the namespace endpoint with the given relative path
    /// (e.g. <c>"myqueue"</c>, <c>"myqueue/messages"</c>) and optional query
    /// string (no leading <c>?</c>).
    /// </summary>
    public Uri BuildUri(string relativePath, string? query = null)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        var trimmed = relativePath.TrimStart('/');
        var sb = new StringBuilder(_baseEndpoint.AbsoluteUri);
        if (!sb.ToString().EndsWith('/'))
        {
            sb.Append('/');
        }
        sb.Append(trimmed);
        if (!string.IsNullOrEmpty(query))
        {
            sb.Append('?').Append(query);
        }
        return new Uri(sb.ToString(), UriKind.Absolute);
    }

    /// <summary>
    /// Signs and dispatches an arbitrary <see cref="HttpRequestMessage"/>.
    /// Callers are responsible for setting method, URI, body, and any
    /// content-type / broker-property headers; this method only attaches
    /// the SAS <c>Authorization</c> header and runs the request through the
    /// shared <see cref="AzureHttpClient"/> (which owns retry policy).
    /// </summary>
    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _auth.AuthenticateAsync(request, ct).ConfigureAwait(false);
        return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
    }

    // --- Management API helpers (Slice 1 queue lifecycle) ---

    /// <summary>
    /// PUTs the supplied Atom <c>QueueDescription</c> entry, creating the
    /// queue if it doesn't exist. Service Bus returns 201 on create, 200 on
    /// "create-or-update with matching definition", and 409 on conflict.
    /// </summary>
    public Task<HttpResponseMessage> CreateQueueAsync(string queueName, string atomEntryXml, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Put, BuildUri(queueName, $"api-version={ApiVersion}"));
        req.Content = new StringContent(atomEntryXml, Encoding.UTF8, "application/atom+xml;type=entry");
        req.Headers.TryAddWithoutValidation("If-None-Match", "*");
        return SendAsync(req, ct);
    }

    /// <summary>
    /// GETs the Atom <c>QueueDescription</c> of an existing queue. Returns
    /// 404 when the queue does not exist.
    /// </summary>
    public Task<HttpResponseMessage> GetQueueAsync(string queueName, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, BuildUri(queueName, $"api-version={ApiVersion}"));
        return SendAsync(req, ct);
    }

    /// <summary>
    /// DELETEs the queue. Returns 200 on success, 404 if it doesn't exist.
    /// </summary>
    public Task<HttpResponseMessage> DeleteQueueAsync(string queueName, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Delete, BuildUri(queueName, $"api-version={ApiVersion}"));
        return SendAsync(req, ct);
    }

    /// <summary>
    /// Lists queues under the namespace. Service Bus paginates via
    /// <c>$skip</c>/<c>$top</c>; the per-page max is 100 — callers loop.
    /// </summary>
    public Task<HttpResponseMessage> ListQueuesAsync(int skip, int top, CancellationToken ct)
    {
        var query = $"api-version={ApiVersion}&$skip={skip}&$top={top}";
        var req = new HttpRequestMessage(HttpMethod.Get, BuildUri("$Resources/queues", query));
        return SendAsync(req, ct);
    }

    // --- Runtime API helpers (Slice 2+ send / receive) ---

    /// <summary>
    /// Posts a single message to a queue's runtime endpoint. Caller is
    /// responsible for setting the <c>BrokerProperties</c> header (with
    /// MessageId, SessionId, ScheduledEnqueueTimeUtc, etc. encoded as JSON)
    /// and any custom application-property headers. The body must already
    /// be the raw message bytes.
    /// </summary>
    public Task<HttpResponseMessage> SendMessageAsync(
        string queueName,
        HttpContent body,
        string? brokerPropertiesJson,
        IReadOnlyDictionary<string, string>? applicationProperties,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(queueName);
        ArgumentNullException.ThrowIfNull(body);

        var req = new HttpRequestMessage(HttpMethod.Post,
            BuildUri(queueName + "/messages", $"api-version={ApiVersion}"))
        {
            Content = body,
        };

        if (!string.IsNullOrEmpty(brokerPropertiesJson))
        {
            req.Headers.TryAddWithoutValidation("BrokerProperties", brokerPropertiesJson);
        }
        if (applicationProperties is not null)
        {
            foreach (var kv in applicationProperties)
            {
                // SB application properties are sent as plain HTTP headers
                // alongside the message; the receiver gets them back via
                // the same header surface on /messages/head.
                req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
            }
        }
        return SendAsync(req, ct);
    }

    /// <summary>
    /// Posts a batch of messages to a queue. Service Bus accepts JSON
    /// arrays at <c>POST /{queue}/messages</c> when the content-type is
    /// <c>application/vnd.microsoft.servicebus.json</c>. Each array element
    /// is <c>{ "Body": ..., "BrokerProperties": {...}, "UserProperties": {...} }</c>.
    /// </summary>
    public Task<HttpResponseMessage> SendMessageBatchAsync(
        string queueName, string batchJson, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(queueName);
        ArgumentException.ThrowIfNullOrEmpty(batchJson);
        var req = new HttpRequestMessage(HttpMethod.Post,
            BuildUri(queueName + "/messages", $"api-version={ApiVersion}"))
        {
            Content = new StringContent(batchJson, Encoding.UTF8,
                "application/vnd.microsoft.servicebus.json"),
        };
        return SendAsync(req, ct);
    }

    /// <summary>
    /// Peek-locks a single message at the head of the queue. Returns the
    /// raw SB <see cref="HttpResponseMessage"/> so the caller can read both
    /// the body (the message payload) and the response headers
    /// (<c>BrokerProperties</c>, application-property headers, <c>Location</c>
    /// which encodes the lock token). The caller is responsible for either
    /// completing (DELETE) or letting the lock expire.
    ///
    /// <para>Service Bus REST does <em>not</em> support batched receives —
    /// AMQP does, but over HTTP the proxy must loop. Callers requesting
    /// <c>MaxNumberOfMessages &gt; 1</c> issue this method repeatedly within
    /// their receive budget.</para>
    /// </summary>
    public Task<HttpResponseMessage> PeekLockMessageAsync(
        string queueName, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(queueName);
        // SB's `timeout` query parameter is a server-side wait in seconds —
        // it's how long the broker should wait before returning empty when
        // no message is available. We expose it so Slice 4 long-polling can
        // crank it up; Slice 3 short-poll callers pass TimeSpan.Zero.
        var timeoutSeconds = Math.Max(0, (int)timeout.TotalSeconds);
        var req = new HttpRequestMessage(HttpMethod.Post,
            BuildUri(queueName + "/messages/head", $"timeout={timeoutSeconds}&api-version={ApiVersion}"));
        return SendAsync(req, ct);
    }

    /// <summary>
    /// Completes an in-flight peek-locked message via
    /// <c>DELETE /{queue}/messages/{messageId}/{lockToken}</c>. SB returns
    /// 200 on success, 404 if the lock has expired or the message no
    /// longer exists.
    /// </summary>
    public Task<HttpResponseMessage> DeleteLockedMessageAsync(
        string queueName, string messageId, string lockToken, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(queueName);
        ArgumentException.ThrowIfNullOrEmpty(messageId);
        ArgumentException.ThrowIfNullOrEmpty(lockToken);
        var req = new HttpRequestMessage(HttpMethod.Delete,
            BuildUri($"{queueName}/messages/{Uri.EscapeDataString(messageId)}/{Uri.EscapeDataString(lockToken)}",
                $"api-version={ApiVersion}"));
        return SendAsync(req, ct);
    }

    /// <summary>
    /// Renews the lock on an in-flight peek-locked message via
    /// <c>POST /{queue}/messages/{messageId}/{lockToken}</c>. SB's behavior
    /// is to extend the lock by the queue's configured <c>LockDuration</c>;
    /// it does <em>not</em> accept an arbitrary new timeout (unlike SQS's
    /// <c>VisibilityTimeout</c>). The proxy documents this divergence and
    /// clamps caller-supplied values.
    /// </summary>
    public Task<HttpResponseMessage> RenewLockAsync(
        string queueName, string messageId, string lockToken, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(queueName);
        ArgumentException.ThrowIfNullOrEmpty(messageId);
        ArgumentException.ThrowIfNullOrEmpty(lockToken);
        var req = new HttpRequestMessage(HttpMethod.Post,
            BuildUri($"{queueName}/messages/{Uri.EscapeDataString(messageId)}/{Uri.EscapeDataString(lockToken)}",
                $"api-version={ApiVersion}"));
        // The renew endpoint is a POST with no body, but HttpClient demands
        // a non-null Content for verbs that allow a body. Empty bytes keep
        // SB happy without an explicit Content-Length=0 fight.
        req.Content = new ByteArrayContent(Array.Empty<byte>());
        return SendAsync(req, ct);
    }

    /// <summary>
    /// Updates an existing queue's QueueDescription via
    /// <c>PUT /{queue}?api-version=…</c> with <c>If-Match: *</c>. Service
    /// Bus only honours updates to a small subset of properties
    /// (LockDuration, DefaultMessageTimeToLive, etc.); other fields must
    /// match the existing values verbatim. Callers are responsible for
    /// merging the desired changes onto the current QueueDescription
    /// before serialising the Atom entry.
    /// </summary>
    public Task<HttpResponseMessage> UpdateQueueAsync(string queueName, string atomEntryXml, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(queueName);
        ArgumentException.ThrowIfNullOrEmpty(atomEntryXml);
        var req = new HttpRequestMessage(HttpMethod.Put, BuildUri(queueName, $"api-version={ApiVersion}"));
        req.Content = new StringContent(atomEntryXml, Encoding.UTF8, "application/atom+xml;type=entry");
        req.Headers.TryAddWithoutValidation("If-Match", "*");
        return SendAsync(req, ct);
    }

    /// <summary>
    /// Resolves the namespace endpoint. Accepts either a plain namespace
    /// name (<c>"my-ns"</c> → <c>https://my-ns.servicebus.windows.net/</c>)
    /// or an absolute http/https URL (used by the emulator and sovereign
    /// clouds). Plain namespace input is validated as a single DNS label
    /// so a malformed value cannot point SAS-signed traffic at an
    /// attacker-controlled host (e.g. <c>"evil.example/path"</c> being
    /// interpolated into the hostname template).
    /// </summary>
    internal static Uri ResolveEndpoint(string nsOrUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nsOrUrl);

        if (Uri.TryCreate(nsOrUrl, UriKind.Absolute, out var absolute) &&
            (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
        {
            var raw = absolute.AbsoluteUri.TrimEnd('/') + "/";
            return new Uri(raw, UriKind.Absolute);
        }

        if (!IsValidDnsLabel(nsOrUrl))
        {
            throw new ArgumentException(
                $"Service Bus namespace '{nsOrUrl}' is not a valid DNS label. " +
                "Pass either a plain namespace name (1-50 chars, letters/digits/hyphens, " +
                "must start with a letter and end with a letter or digit) " +
                "or an absolute http(s) URL.",
                nameof(nsOrUrl));
        }
        return new Uri($"https://{nsOrUrl}.servicebus.windows.net/", UriKind.Absolute);
    }

    // Azure Service Bus namespace naming rules (matches portal validation):
    //   * 1-50 characters
    //   * starts with a letter, ends with a letter or digit
    //   * letters, digits, hyphens only
    private static bool IsValidDnsLabel(string value)
    {
        if (value.Length is 0 or > 50) return false;
        if (!char.IsLetter(value[0])) return false;
        var last = value[^1];
        if (!char.IsLetterOrDigit(last)) return false;
        foreach (var c in value)
        {
            if (!(char.IsLetterOrDigit(c) || c == '-')) return false;
        }
        return true;
    }
}
