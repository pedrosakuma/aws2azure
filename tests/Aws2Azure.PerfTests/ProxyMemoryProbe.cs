using System.Globalization;

namespace Aws2Azure.PerfTests;

/// <summary>
/// One scrape of the proxy's self-reported runtime/memory gauges
/// (<c>aws2azure_process_working_set_bytes</c> and friends, emitted by
/// <c>Aws2Azure.Core.Observability.ProxyMetrics</c> on
/// <c>/_aws2azure/metrics</c>). All values are taken from inside the proxy
/// process itself — the perf harness drives the proxy out-of-process, so the
/// test process's own <see cref="Environment.WorkingSet"/> would measure the
/// wrong thing.
/// </summary>
public readonly record struct RuntimeMemorySnapshot(
    long WorkingSetBytes,
    long GcHeapBytes,
    long AllocatedBytesTotal,
    long Gen2Collections);

/// <summary>
/// Scrapes the proxy's Prometheus endpoint for the runtime memory gauges added
/// for issue #274. Used by <see cref="PerfRunner"/> to sample the proxy's
/// working set during the measure window and to diff cumulative allocated bytes
/// / gen2 collections across it. The endpoint is host-agnostic (a top-level
/// <c>MapGet</c>, not behind a module host matcher), so the loopback base URL is
/// used directly rather than the per-service nip.io alias.
/// </summary>
public sealed class ProxyMemoryProbe : IDisposable
{
    private const string WorkingSetMetric = "aws2azure_process_working_set_bytes";
    private const string HeapMetric = "aws2azure_dotnet_gc_heap_size_bytes";
    private const string AllocatedMetric = "aws2azure_dotnet_gc_allocated_bytes_total";
    private const string Gen2Metric = "aws2azure_dotnet_gc_gen2_collections_total";

    private readonly HttpClient _http;
    private readonly string _metricsUrl;

    public ProxyMemoryProbe(string proxyBaseUrl)
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        _metricsUrl = proxyBaseUrl.TrimEnd('/') + "/_aws2azure/metrics";
    }

    /// <summary>
    /// Scrapes and parses the four runtime gauges. Returns <c>null</c> if the
    /// endpoint is unreachable or any required gauge is missing — the caller
    /// treats that as "memory not measured" rather than failing the scenario.
    /// </summary>
    public async Task<RuntimeMemorySnapshot?> SampleAsync(CancellationToken cancellationToken = default)
    {
        string text;
        try
        {
            using var resp = await _http.GetAsync(_metricsUrl, cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            text = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }

        return Parse(text);
    }

    /// <summary>
    /// Parses the four runtime gauges out of Prometheus exposition text. Returns
    /// <c>null</c> if any required gauge is missing. Exposed for unit testing —
    /// our gauges carry no labels, so each line is <c>"&lt;name&gt; &lt;value&gt;"</c>.
    /// </summary>
    internal static RuntimeMemorySnapshot? Parse(string text)
    {
        long? ws = null, heap = null, alloc = null, gen2 = null;
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.AsSpan().Trim();
            if (trimmed.IsEmpty || trimmed[0] == '#') continue;

            var space = trimmed.IndexOf(' ');
            if (space <= 0) continue;
            var name = trimmed[..space];
            var valueSpan = trimmed[(space + 1)..].Trim();

            if (name.SequenceEqual(WorkingSetMetric)) ws = ParseLong(valueSpan);
            else if (name.SequenceEqual(HeapMetric)) heap = ParseLong(valueSpan);
            else if (name.SequenceEqual(AllocatedMetric)) alloc = ParseLong(valueSpan);
            else if (name.SequenceEqual(Gen2Metric)) gen2 = ParseLong(valueSpan);
        }

        if (ws is null || heap is null || alloc is null || gen2 is null) return null;
        return new RuntimeMemorySnapshot(ws.Value, heap.Value, alloc.Value, gen2.Value);
    }

    private static long? ParseLong(ReadOnlySpan<char> span)
        => long.TryParse(span, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    public void Dispose() => _http.Dispose();
}
