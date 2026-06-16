using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Aws2Azure.TestSupport.Http;

/// <summary>
/// Canonical test HTTP handler. It reconciles the previous one-off variants by
/// supporting sync or async responders, queued responses, call counts, and
/// eager request capture before <see cref="HttpClient"/> disposes messages.
/// </summary>
public sealed class ScriptedHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> _responses = new();
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? _fallback;
    private int _callCount;

    public ScriptedHandler()
    {
    }

    public ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : this((request, _) => Task.FromResult(responder(request)))
    {
    }

    public ScriptedHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> responder)
        : this((request, cancellationToken) => Task.FromResult(responder(request, cancellationToken)))
    {
    }

    public ScriptedHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
    {
        _fallback = responder;
    }

    public int CallCount => Volatile.Read(ref _callCount);

    public HttpRequestMessage? LastRequest { get; private set; }

    public List<CapturedHttpRequest> Requests { get; } = [];

    public bool ThrowWhenExhausted { get; set; } = true;

    public void Enqueue(HttpResponseMessage response)
        => Enqueue((_, _) => Task.FromResult(response));

    public void Enqueue(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => Enqueue((request, _) => Task.FromResult(responder(request)));

    public void Enqueue(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> responder)
        => Enqueue((request, cancellationToken) => Task.FromResult(responder(request, cancellationToken)));

    public void Enqueue(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        => _responses.Enqueue(responder);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _callCount);
        LastRequest = request;
        Requests.Add(await CapturedHttpRequest.FromAsync(request, cancellationToken));

        if (_responses.Count > 0)
        {
            return await _responses.Dequeue()(request, cancellationToken);
        }

        if (_fallback is not null)
        {
            return await _fallback(request, cancellationToken);
        }

        if (ThrowWhenExhausted)
        {
            throw new InvalidOperationException("No scripted HTTP response is available.");
        }

        return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
    }
}

/// <summary>Immutable snapshot of a request observed by <see cref="ScriptedHandler"/>.</summary>
public sealed record CapturedHttpRequest(
    HttpMethod Method,
    Uri? RequestUri,
    IReadOnlyDictionary<string, string[]> Headers,
    IReadOnlyDictionary<string, string[]> ContentHeaders,
    string? Body)
{
    public static async Task<CapturedHttpRequest> FromAsync(HttpRequestMessage request, CancellationToken cancellationToken = default)
    {
        string? body = null;
        Dictionary<string, string[]> contentHeaders = new(StringComparer.OrdinalIgnoreCase);
        if (request.Content is not null)
        {
            body = await request.Content.ReadAsStringAsync(cancellationToken);
            contentHeaders = request.Content.Headers.ToDictionary(
                header => header.Key,
                header => header.Value.ToArray(),
                StringComparer.OrdinalIgnoreCase);
        }

        var headers = request.Headers.ToDictionary(
            header => header.Key,
            header => header.Value.ToArray(),
            StringComparer.OrdinalIgnoreCase);

        return new CapturedHttpRequest(request.Method, request.RequestUri, headers, contentHeaders, body);
    }
}
