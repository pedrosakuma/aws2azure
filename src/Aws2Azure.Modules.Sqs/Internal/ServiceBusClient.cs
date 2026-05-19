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
