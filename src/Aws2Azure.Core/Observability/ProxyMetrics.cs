using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Aws2Azure.Core.Observability;

/// <summary>
/// Central metrics collector for aws2azure proxy. Uses System.Diagnostics.Metrics
/// for AOT-compatible instrumentation. Metrics are exposed via /metrics endpoint
/// in Prometheus format.
/// </summary>
public sealed class ProxyMetrics
{
    public static readonly string MeterName = "Aws2Azure.Proxy";
    
    private readonly Meter _meter;
    
    // Request counters
    private readonly Counter<long> _requestsTotal;
    private readonly Counter<long> _errorsTotal;
    
    // Duration histograms
    private readonly Histogram<double> _requestDuration;
    private readonly Histogram<double> _translationDuration;
    private readonly Histogram<double> _backendDuration;
    
    // Size histograms
    private readonly Histogram<long> _requestSize;
    private readonly Histogram<long> _responseSize;
    
    // Gauges (via ObservableGauge)
    private long _activeRequests;
    
    public ProxyMetrics()
    {
        _meter = new Meter(MeterName, "1.0.0");
        
        // Counters
        _requestsTotal = _meter.CreateCounter<long>(
            "aws2azure_requests_total",
            unit: "{request}",
            description: "Total number of requests processed");
        
        _errorsTotal = _meter.CreateCounter<long>(
            "aws2azure_errors_total",
            unit: "{error}",
            description: "Total number of errors");
        
        // Histograms with Prometheus-recommended buckets
        _requestDuration = _meter.CreateHistogram<double>(
            "aws2azure_request_duration_seconds",
            unit: "s",
            description: "Total request duration including backend call");
        
        _translationDuration = _meter.CreateHistogram<double>(
            "aws2azure_translation_duration_seconds",
            unit: "s",
            description: "Time spent in request/response translation (parsing, serialization)");
        
        _backendDuration = _meter.CreateHistogram<double>(
            "aws2azure_backend_duration_seconds",
            unit: "s",
            description: "Time spent waiting for Azure backend");
        
        _requestSize = _meter.CreateHistogram<long>(
            "aws2azure_request_size_bytes",
            unit: "By",
            description: "Request body size in bytes");
        
        _responseSize = _meter.CreateHistogram<long>(
            "aws2azure_response_size_bytes",
            unit: "By",
            description: "Response body size in bytes");
        
        // Gauges
        _meter.CreateObservableGauge(
            "aws2azure_active_requests",
            () => Interlocked.Read(ref _activeRequests),
            unit: "{request}",
            description: "Number of requests currently being processed");
    }
    
    /// <summary>
    /// Records the start of a request. Returns a context to pass to RecordRequestEnd.
    /// </summary>
    public RequestMetricsContext StartRequest(string service, string operation, long requestSizeBytes)
    {
        Interlocked.Increment(ref _activeRequests);
        
        if (requestSizeBytes > 0)
        {
            _requestSize.Record(requestSizeBytes, 
                new KeyValuePair<string, object?>("service", service),
                new KeyValuePair<string, object?>("operation", operation));
        }
        
        return new RequestMetricsContext
        {
            Service = service,
            Operation = operation,
            StartTimestamp = Stopwatch.GetTimestamp(),
        };
    }
    
    /// <summary>
    /// Records the end of a request with full timing breakdown.
    /// </summary>
    public void EndRequest(
        in RequestMetricsContext ctx,
        int statusCode,
        long responseSizeBytes,
        TimeSpan? translationTime = null,
        TimeSpan? backendTime = null)
    {
        Interlocked.Decrement(ref _activeRequests);
        
        var elapsed = Stopwatch.GetElapsedTime(ctx.StartTimestamp);
        var tags = new TagList
        {
            { "service", ctx.Service },
            { "operation", ctx.Operation },
            { "status", StatusCodeBucket(statusCode) },
        };
        
        _requestsTotal.Add(1, tags);
        _requestDuration.Record(elapsed.TotalSeconds, tags);
        
        if (statusCode >= 400)
        {
            var errorTags = new TagList
            {
                { "service", ctx.Service },
                { "operation", ctx.Operation },
                { "status_code", statusCode },
            };
            _errorsTotal.Add(1, errorTags);
        }
        
        if (responseSizeBytes > 0)
        {
            _responseSize.Record(responseSizeBytes,
                new KeyValuePair<string, object?>("service", ctx.Service),
                new KeyValuePair<string, object?>("operation", ctx.Operation));
        }
        
        // Record overhead breakdown if provided
        var overheadTags = new TagList
        {
            { "service", ctx.Service },
            { "operation", ctx.Operation },
        };
        
        if (translationTime.HasValue)
        {
            _translationDuration.Record(translationTime.Value.TotalSeconds, overheadTags);
        }
        
        if (backendTime.HasValue)
        {
            _backendDuration.Record(backendTime.Value.TotalSeconds, overheadTags);
        }
    }
    
    /// <summary>
    /// Records a backend call duration (for operations that make multiple Azure calls).
    /// </summary>
    public void RecordBackendCall(string service, string operation, TimeSpan duration)
    {
        _backendDuration.Record(duration.TotalSeconds,
            new KeyValuePair<string, object?>("service", service),
            new KeyValuePair<string, object?>("operation", operation));
    }
    
    private static string StatusCodeBucket(int statusCode) => statusCode switch
    {
        >= 200 and < 300 => "2xx",
        >= 300 and < 400 => "3xx",
        >= 400 and < 500 => "4xx",
        >= 500 => "5xx",
        _ => "other",
    };
}

/// <summary>
/// Context passed between StartRequest and EndRequest to track timing.
/// </summary>
public readonly struct RequestMetricsContext
{
    public required string Service { get; init; }
    public required string Operation { get; init; }
    public required long StartTimestamp { get; init; }
}
