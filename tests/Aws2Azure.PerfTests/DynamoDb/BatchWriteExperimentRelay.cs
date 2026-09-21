using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Aws2Azure.PerfTests.DynamoDb;

/// <summary>
/// Test-only observer. Both paths traverse this relay; its buffering overhead
/// is part of the experiment and must not be presented as production latency.
/// </summary>
internal sealed class BatchWriteExperimentRelay : IAsyncDisposable
{
    internal const int MaxObservedAttempts = 65536;
    private readonly HttpClient _http;
    private readonly int _observationLimit;
    private readonly object _gate = new();
    private readonly List<double> _latencies = new();
    private readonly HashSet<string> _identities = new(StringComparer.Ordinal);
    private readonly HashSet<string> _dispatchIdentities = new(StringComparer.Ordinal);
    private readonly AttemptCounts _writes = new(), _other = new();
    private WebApplication? _app;
    private bool _recording, _begun;
    private int _dropped;
    private int _attempts, _repeated, _throttles, _errors, _missingRu;
    private long _requestBytes, _responseBytes;
    private double _ru;
    public string Endpoint { get; private set; } = "";

    public BatchWriteExperimentRelay(HttpMessageHandler? handler = null, TimeSpan? timeout = null,
        int observationLimit = MaxObservedAttempts)
    {
        if (observationLimit < 1 || observationLimit > MaxObservedAttempts)
            throw new ArgumentOutOfRangeException(nameof(observationLimit));
        _observationLimit = observationLimit;
        _http = new(handler ?? new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false });
        if (timeout.HasValue) _http.Timeout = timeout.Value;
    }

    public async Task StartAsync(string backend, CancellationToken ct)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        _app = builder.Build();
        _app.Run(ctx => ForwardAsync(ctx, new Uri(backend)));
        await _app.StartAsync(ct);
        Endpoint = _app.Urls.Single() + "/";
    }

    internal async Task ForwardAsync(HttpContext ctx, Uri backend)
    {
        var path = ctx.Request.Path.Value ?? "/";
        var isWrite = path.Contains("/docs", StringComparison.Ordinal)
            && ctx.Request.Method is "POST" or "DELETE";
        AttemptCounts? counts = null;
        lock (_gate)
        {
            if (_recording)
            {
                if (_writes.Arrived + _other.Arrived >= _observationLimit)
                    _dropped = Math.Min(MaxObservedAttempts, _dropped + 1);
                else
                {
                    counts = isWrite ? _writes : _other;
                    counts.Arrived++;
                    counts.Active++;
                }
            }
        }
        var phase = "request-read";
        try
        {
            using var request = new HttpRequestMessage(new HttpMethod(ctx.Request.Method), new Uri(backend, path + ctx.Request.QueryString));
            using var requestBuffer = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(requestBuffer, ctx.RequestAborted);
            var body = requestBuffer.ToArray();
            if (body.Length != 0) request.Content = new ByteArrayContent(body);
            foreach (var header in ctx.Request.Headers)
            {
                if (header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase)
                    || header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                    || header.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
                    || header.Key.Equals("Connection", StringComparison.OrdinalIgnoreCase)) continue;
                if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
                    request.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }
            var identity = path;
            if (isWrite && ctx.Request.Method == "POST")
            {
                using var document = JsonDocument.Parse(body);
                identity += "/" + document.RootElement.GetProperty("id").GetString();
            }
            identity += "|" + ctx.Request.Headers["x-ms-documentdb-partitionkey"];
            var start = Stopwatch.GetTimestamp();
            phase = "dispatch";
            lock (_gate)
            {
                if (counts is not null)
                {
                    counts.DispatchStarted++;
                    if (isWrite && !_dispatchIdentities.Add(identity)) counts.RepeatedDispatches++;
                }
            }
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted);
            lock (_gate)
            {
                if (counts is not null)
                {
                    counts.HeadersReceived++;
                    var status = AllowedStatus((int)response.StatusCode);
                    counts.Statuses[status] = counts.Statuses.GetValueOrDefault(status) + 1;
                    if (!response.IsSuccessStatusCode) counts.NonSuccessHeaders++;
                }
            }
            phase = "response-body";
            var bytes = await response.Content.ReadAsByteArrayAsync(ctx.RequestAborted);
            var milliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            lock (_gate)
            {
                if (counts is not null)
                {
                    counts.BodyCompleted++;
                    if (isWrite)
                    {
                        _attempts++;
                        if (!_identities.Add(identity)) _repeated++;
                        if (response.StatusCode == HttpStatusCode.TooManyRequests) _throttles++;
                        if (!response.IsSuccessStatusCode) _errors++;
                        _latencies.Add(milliseconds);
                        _requestBytes += body.Length;
                        _responseBytes += bytes.Length;
                        if (response.Headers.TryGetValues("x-ms-request-charge", out var charges)
                            && double.TryParse(charges.Single(), NumberStyles.Float, CultureInfo.InvariantCulture, out var ru))
                            _ru += ru;
                        else _missingRu++;
                    }
                }
            }
            phase = "response-write";
            ctx.Response.StatusCode = (int)response.StatusCode;
            foreach (var header in response.Headers.Concat(response.Content.Headers))
            {
                if (header.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
                    || header.Key.Equals("Connection", StringComparison.OrdinalIgnoreCase)
                    || header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
                ctx.Response.Headers[header.Key] = header.Value.ToArray();
            }
            if (response.StatusCode == HttpStatusCode.NoContent)
            {
                // Even an empty Body.WriteAsync is invalid for Kestrel's 204 response.
                await ctx.Response.CompleteAsync();
            }
            else
            {
                ctx.Response.ContentLength = bytes.Length;
                await ctx.Response.Body.WriteAsync(bytes, ctx.RequestAborted);
            }
            lock (_gate)
                if (counts is not null) counts.ResponseWriteCompleted++;
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                if (counts is not null)
                {
                    var key = phase + ":" + SafeExceptionType(ex);
                    counts.Faults[key] = counts.Faults.GetValueOrDefault(key) + 1;
                    // RequestAborted also identifies disconnect IO failures that are
                    // surfaced as IOException rather than OperationCanceledException.
                    if (ctx.RequestAborted.IsCancellationRequested)
                    {
                        counts.Cancelled++;
                        counts.ClientDisconnects++;
                    }
                    else if (ex is OperationCanceledException)
                    {
                        counts.Cancelled++;
                        if (ex.InnerException is TimeoutException) counts.Timeouts++;
                        else counts.OtherCancellations++;
                    }
                    else counts.Failed++;
                }
            }
            throw;
        }
        finally
        {
            lock (_gate)
                if (counts is not null) counts.Active--;
        }
    }

    public void Begin()
    {
        lock (_gate)
        {
            if (_begun) throw new InvalidOperationException("Use a fresh observer per measurement.");
            _begun = true;
            _recording = true;
        }
    }

    public object End() => End(out _);

    public object End(out bool incompleteOrFaulted)
    {
        lock (_gate)
        {
            _recording = false;
            incompleteOrFaulted = _dropped != 0 || _writes.Active + _other.Active != 0
                || _writes.Failed + _other.Failed + _writes.Cancelled + _other.Cancelled != 0;
            return new
            {
                writeRestAttempts = _attempts, repeatedItemAttempts = _repeated, responses429 = _throttles,
                nonSuccessResponses = _errors, requestBodyBytes = _requestBytes, responseBodyBytes = _responseBytes,
                observedRequestUnits = _ru, responsesWithoutRu = _missingRu,
                restAttemptLatencyMs = BatchWriteExperimentAccounting.Distribution(_latencies),
                boundaries = new { writes = _writes.Snapshot(), otherRequests = _other.Snapshot(),
                    maxObservedAttempts = _observationLimit, droppedArrivals = _dropped },
                scope = "Legacy fields count completed backend write response bodies only. Boundaries count relay arrivals through response write, including incomplete attempts. ResponseWriteCompleted is not proof of client acknowledgement. Nonzero activeAtSnapshot/droppedArrivals means incomplete observation.",
            };
        }
    }

    public async Task<bool> WaitForIdleAsync()
    {
        var start = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(start) < TimeSpan.FromSeconds(2))
        {
            lock (_gate) if (_writes.Active + _other.Active == 0) return true;
            await Task.Delay(10);
        }
        return false;
    }

    internal static string SafeExceptionType(Exception exception) => exception switch
    {
        OperationCanceledException => "OperationCanceledException",
        HttpRequestException => "HttpRequestException",
        IOException => "IOException",
        JsonException => "JsonException",
        InvalidOperationException => "InvalidOperationException",
        _ => "Other",
    };

    private static string AllowedStatus(int status) => status switch
    {
        200 or 201 or 202 or 204 or 400 or 401 or 403 or 404 or 408 or 409 or 412 or 413 or 429
            or 500 or 502 or 503 or 504 => status.ToString(CultureInfo.InvariantCulture),
        _ => "other",
    };

    private sealed class AttemptCounts
    {
        public int Arrived, Active, DispatchStarted, HeadersReceived, BodyCompleted, ResponseWriteCompleted;
        public int Failed, Cancelled, ClientDisconnects, Timeouts, OtherCancellations, NonSuccessHeaders, RepeatedDispatches;
        public readonly Dictionary<string, int> Statuses = new(StringComparer.Ordinal);
        public readonly Dictionary<string, int> Faults = new(StringComparer.Ordinal);
        public object Snapshot() => new
        {
            arrived = Arrived, dispatchStarted = DispatchStarted, responseHeadersReceived = HeadersReceived,
            bodyCompleted = BodyCompleted, responseWriteCompleted = ResponseWriteCompleted,
            failed = Failed, cancelled = Cancelled, clientDisconnects = ClientDisconnects,
            timeouts = Timeouts, otherCancellations = OtherCancellations,
            nonSuccessResponseHeaders = NonSuccessHeaders, repeatedDispatches = RepeatedDispatches,
            activeAtSnapshot = Active,
            statusCounts = new Dictionary<string, int>(Statuses),
            faultsByPhaseAndType = new Dictionary<string, int>(Faults),
        };
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await _app.StopAsync(deadline.Token);
            await _app.DisposeAsync();
        }
        _http.Dispose();
    }
}
