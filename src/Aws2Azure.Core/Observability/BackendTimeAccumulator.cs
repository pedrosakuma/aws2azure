using System.Diagnostics;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Core.Observability;

/// <summary>
/// Thread-safe accumulator for backend call durations within a single request.
/// Modules add their Azure REST/AMQP call times, and the registry computes
/// translation time as (total request time - accumulated backend time).
/// </summary>
public sealed class BackendTimeAccumulator
{
    private long _totalTicks;

    /// <summary>
    /// Adds a backend call duration to the accumulator.
    /// </summary>
    public void Add(TimeSpan duration)
    {
        Interlocked.Add(ref _totalTicks, duration.Ticks);
    }

    /// <summary>
    /// Gets the total accumulated backend time.
    /// </summary>
    public TimeSpan GetTotal() => TimeSpan.FromTicks(Interlocked.Read(ref _totalTicks));
}

/// <summary>
/// Ambient context for backend timing that flows with async operations.
/// Set by ServiceModuleRegistry before calling module.HandleAsync().
/// </summary>
public static class BackendTimingContext
{
    private static readonly AsyncLocal<BackendTimeAccumulator?> _current = new();
    private static readonly AsyncLocal<ProxyMetrics?> _metrics = new();
    private static readonly AsyncLocal<string?> _service = new();
    private static readonly AsyncLocal<string?> _operation = new();

    /// <summary>
    /// Sets the current backend timing context. Called by ServiceModuleRegistry.
    /// </summary>
    public static void SetCurrent(BackendTimeAccumulator accumulator, ProxyMetrics? metrics, string service, string operation)
    {
        _current.Value = accumulator;
        _metrics.Value = metrics;
        _service.Value = service;
        _operation.Value = operation;
    }

    /// <summary>
    /// Clears the current context. Called after module.HandleAsync completes.
    /// </summary>
    public static void Clear()
    {
        _current.Value = null;
        _metrics.Value = null;
        _service.Value = null;
        _operation.Value = null;
    }

    /// <summary>
    /// Records a backend call duration to the current context (if set).
    /// Safe to call even when no context is set.
    /// </summary>
    public static void RecordBackendCall(TimeSpan duration)
    {
        _current.Value?.Add(duration);
        
        if (_metrics.Value is { } metrics)
        {
            metrics.RecordBackendCall(_service.Value ?? "unknown", _operation.Value ?? "unknown", duration);
        }
    }

    /// <summary>
    /// Executes an async backend call and records its duration to the ambient context.
    /// Usage: var response = await BackendTimingContext.TimeAsync(() => client.SendAsync(request, ct));
    /// </summary>
    public static async Task<T> TimeAsync<T>(Func<Task<T>> backendCall)
    {
        var start = Stopwatch.GetTimestamp();
        try
        {
            return await backendCall().ConfigureAwait(false);
        }
        finally
        {
            var duration = Stopwatch.GetElapsedTime(start);
            RecordBackendCall(duration);
        }
    }

    /// <summary>
    /// Executes an async backend call (no return value) and records its duration.
    /// </summary>
    public static async Task TimeAsync(Func<Task> backendCall)
    {
        var start = Stopwatch.GetTimestamp();
        try
        {
            await backendCall().ConfigureAwait(false);
        }
        finally
        {
            var duration = Stopwatch.GetElapsedTime(start);
            RecordBackendCall(duration);
        }
    }
}

/// <summary>
/// Extension methods for recording backend call metrics from modules via HttpContext.
/// </summary>
public static class BackendMetricsExtensions
{
    private const string AccumulatorKey = "aws2azure.backendTimeAccumulator";
    private const string MetricsKey = "aws2azure.metrics";
    private const string ServiceKey = "aws2azure.service";
    private const string OperationKey = "aws2azure.operation";

    /// <summary>
    /// Records a backend call duration. Call this after each Azure REST/AMQP call.
    /// The duration is accumulated and used to compute translation time at request end.
    /// </summary>
    public static void RecordBackendCall(this HttpContext context, TimeSpan duration)
    {
        if (context.Items.TryGetValue(AccumulatorKey, out var obj) && obj is BackendTimeAccumulator acc)
        {
            acc.Add(duration);
        }

        // Also record individual backend call to ProxyMetrics for detailed histogram
        if (context.Items.TryGetValue(MetricsKey, out var metricsObj) && metricsObj is ProxyMetrics metrics)
        {
            var service = context.Items.TryGetValue(ServiceKey, out var svc) ? svc as string ?? "unknown" : "unknown";
            var operation = context.Items.TryGetValue(OperationKey, out var op) ? op as string ?? "unknown" : "unknown";
            metrics.RecordBackendCall(service, operation, duration);
        }
    }

    /// <summary>
    /// Starts timing a backend call. Returns a timestamp to pass to StopBackendCall.
    /// </summary>
    public static long StartBackendCall(this HttpContext _) => Stopwatch.GetTimestamp();

    /// <summary>
    /// Stops timing a backend call and records the duration.
    /// </summary>
    public static void StopBackendCall(this HttpContext context, long startTimestamp)
    {
        var duration = Stopwatch.GetElapsedTime(startTimestamp);
        context.RecordBackendCall(duration);
    }

    /// <summary>
    /// Executes an async backend call and records its duration.
    /// Usage: var response = await context.TimeBackendCallAsync(() => client.SendAsync(request, ct));
    /// </summary>
    public static async Task<T> TimeBackendCallAsync<T>(this HttpContext context, Func<Task<T>> backendCall)
    {
        var start = Stopwatch.GetTimestamp();
        try
        {
            return await backendCall().ConfigureAwait(false);
        }
        finally
        {
            var duration = Stopwatch.GetElapsedTime(start);
            context.RecordBackendCall(duration);
        }
    }

    /// <summary>
    /// Executes an async backend call (no return value) and records its duration.
    /// </summary>
    public static async Task TimeBackendCallAsync(this HttpContext context, Func<Task> backendCall)
    {
        var start = Stopwatch.GetTimestamp();
        try
        {
            await backendCall().ConfigureAwait(false);
        }
        finally
        {
            var duration = Stopwatch.GetElapsedTime(start);
            context.RecordBackendCall(duration);
        }
    }
}
