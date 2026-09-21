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
    private readonly HttpClient _http = new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false });
    private readonly object _gate = new();
    private readonly List<double> _latencies = new();
    private readonly HashSet<string> _identities = new(StringComparer.Ordinal);
    private WebApplication? _app;
    private bool _recording;
    private int _attempts, _repeated, _throttles, _errors, _missingRu;
    private long _requestBytes, _responseBytes;
    private double _ru;
    public string Endpoint { get; private set; } = "";

    public async Task StartAsync(string backend, CancellationToken ct)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        _app = builder.Build();
        _app.Run(async ctx =>
        {
            var path = ctx.Request.Path.Value ?? "/";
            using var request = new HttpRequestMessage(new HttpMethod(ctx.Request.Method), new Uri(new Uri(backend), path + ctx.Request.QueryString));
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
            var isWrite = path.Contains("/docs", StringComparison.Ordinal)
                && ctx.Request.Method is "POST" or "DELETE";
            var identity = path;
            if (isWrite && ctx.Request.Method == "POST")
            {
                using var document = JsonDocument.Parse(body);
                identity += "/" + document.RootElement.GetProperty("id").GetString();
            }
            identity += "|" + ctx.Request.Headers["x-ms-documentdb-partitionkey"];
            var start = Stopwatch.GetTimestamp();
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted);
            var bytes = await response.Content.ReadAsByteArrayAsync(ctx.RequestAborted);
            var milliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            if (isWrite)
            {
                lock (_gate)
                {
                    if (_recording)
                    {
                        if (_latencies.Count >= 65536) throw new InvalidOperationException("REST observation budget exhausted.");
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
            ctx.Response.StatusCode = (int)response.StatusCode;
            foreach (var header in response.Headers.Concat(response.Content.Headers))
            {
                if (header.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
                    || header.Key.Equals("Connection", StringComparison.OrdinalIgnoreCase)
                    || header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
                ctx.Response.Headers[header.Key] = header.Value.ToArray();
            }
            ctx.Response.ContentLength = bytes.Length;
            await ctx.Response.Body.WriteAsync(bytes, ctx.RequestAborted);
        });
        await _app.StartAsync(ct);
        Endpoint = _app.Urls.Single() + "/";
    }

    public void Begin()
    {
        lock (_gate)
        {
            if (_recording || _attempts != 0) throw new InvalidOperationException("Use a fresh observer per measurement.");
            _recording = true;
        }
    }

    public object End()
    {
        lock (_gate)
        {
            _recording = false;
            return new
            {
                writeRestAttempts = _attempts, repeatedItemAttempts = _repeated, responses429 = _throttles,
                nonSuccessResponses = _errors, requestBodyBytes = _requestBytes, responseBodyBytes = _responseBytes,
                observedRequestUnits = _ru, responsesWithoutRu = _missingRu,
                restAttemptLatencyMs = BatchWriteExperimentAccounting.Distribution(_latencies),
                scope = "Write attempts at relay, through complete backend response body; excludes proxy queueing and request preparation.",
            };
        }
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
