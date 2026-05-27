using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text;

namespace Aws2Azure.Core.Observability;

/// <summary>
/// Exports metrics in Prometheus text format. AOT-compatible implementation
/// that reads from System.Diagnostics.Metrics via MeterListener.
/// Uses struct-based keys and per-instrument bucket sets.
/// </summary>
public sealed class PrometheusExporter : IDisposable
{
    private readonly MeterListener _listener;
    private readonly object _lock = new();
    
    // Accumulated metric state - keyed by MetricKey for bounded key allocation
    private readonly Dictionary<MetricKey, CounterState> _counters = new();
    private readonly Dictionary<MetricKey, HistogramState> _histograms = new();
    private readonly Dictionary<MetricKey, GaugeState> _gauges = new();
    
    // Duration histogram buckets (seconds) - Prometheus defaults
    private static readonly double[] DurationBuckets = 
        [0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10];
    
    // Size histogram buckets (bytes) - powers of 2 from 256B to 64MB
    private static readonly double[] SizeBuckets = 
        [256, 1024, 4096, 16384, 65536, 262144, 1048576, 4194304, 16777216, 67108864];
    
    public PrometheusExporter()
    {
        _listener = new MeterListener();
        _listener.InstrumentPublished = OnInstrumentPublished;
        _listener.SetMeasurementEventCallback<long>(OnMeasurement);
        _listener.SetMeasurementEventCallback<double>(OnMeasurement);
        _listener.Start();
    }
    
    private void OnInstrumentPublished(Instrument instrument, MeterListener listener)
    {
        if (instrument.Meter.Name == ProxyMetrics.MeterName)
        {
            listener.EnableMeasurementEvents(instrument);
        }
    }
    
    private void OnMeasurement(Instrument instrument, long value, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
    {
        var (key, tagDict) = BuildKey(instrument.Name, tags);
        
        lock (_lock)
        {
            if (instrument is Counter<long>)
            {
                if (!_counters.TryGetValue(key, out var counter))
                {
                    counter = new CounterState { Name = instrument.Name, Tags = tagDict, Description = instrument.Description };
                    _counters[key] = counter;
                }
                counter.Value += value;
            }
            else if (instrument is Histogram<long>)
            {
                var buckets = GetBucketsForInstrument(instrument.Name);
                if (!_histograms.TryGetValue(key, out var histogram))
                {
                    histogram = new HistogramState(instrument.Name, tagDict, instrument.Description, buckets);
                    _histograms[key] = histogram;
                }
                histogram.Record(value);
            }
            else if (instrument is ObservableGauge<long>)
            {
                if (!_gauges.TryGetValue(key, out var gauge))
                {
                    gauge = new GaugeState { Name = instrument.Name, Tags = tagDict, Description = instrument.Description };
                    _gauges[key] = gauge;
                }
                gauge.Value = value;
            }
        }
    }
    
    private void OnMeasurement(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
    {
        var (key, tagDict) = BuildKey(instrument.Name, tags);
        
        lock (_lock)
        {
            if (instrument is Counter<double>)
            {
                if (!_counters.TryGetValue(key, out var counter))
                {
                    counter = new CounterState { Name = instrument.Name, Tags = tagDict, Description = instrument.Description };
                    _counters[key] = counter;
                }
                counter.Value += (long)value;
            }
            else if (instrument is Histogram<double>)
            {
                var buckets = GetBucketsForInstrument(instrument.Name);
                if (!_histograms.TryGetValue(key, out var histogram))
                {
                    histogram = new HistogramState(instrument.Name, tagDict, instrument.Description, buckets);
                    _histograms[key] = histogram;
                }
                histogram.Record(value);
            }
            else if (instrument is ObservableGauge<double>)
            {
                if (!_gauges.TryGetValue(key, out var gauge))
                {
                    gauge = new GaugeState { Name = instrument.Name, Tags = tagDict, Description = instrument.Description };
                    _gauges[key] = gauge;
                }
                gauge.Value = (long)value;
            }
        }
    }
    
    /// <summary>
    /// Selects appropriate histogram buckets based on instrument name/unit.
    /// </summary>
    private static double[] GetBucketsForInstrument(string name) =>
        name.EndsWith("_bytes", StringComparison.Ordinal) ? SizeBuckets : DurationBuckets;
    
    /// <summary>
    /// Exports all metrics in Prometheus text format.
    /// </summary>
    public string Export()
    {
        // Trigger observable instruments to update
        _listener.RecordObservableInstruments();
        
        var sb = new StringBuilder(4096);
        var writtenHelp = new HashSet<string>();
        
        lock (_lock)
        {
            // Counters
            foreach (var (key, counter) in _counters.OrderBy(kv => kv.Key.Name).ThenBy(kv => kv.Key.TagsKey))
            {
                WriteHelp(sb, counter.Name, "counter", counter.Description, writtenHelp);
                sb.Append(counter.Name);
                WriteTags(sb, counter.Tags);
                sb.Append(' ');
                sb.Append(counter.Value);
                sb.AppendLine();
            }
            
            // Histograms
            var histogramsByName = _histograms.Values.GroupBy(h => h.Name);
            foreach (var group in histogramsByName.OrderBy(g => g.Key))
            {
                var first = group.First();
                WriteHelp(sb, first.Name, "histogram", first.Description, writtenHelp);
                
                foreach (var histogram in group)
                {
                    var buckets = histogram.BucketBoundaries;
                    
                    // Bucket lines
                    long cumulative = 0;
                    for (int i = 0; i < buckets.Length; i++)
                    {
                        cumulative += histogram.BucketCounts[i];
                        sb.Append(histogram.Name).Append("_bucket");
                        var bucketTags = new Dictionary<string, string>(histogram.Tags)
                        {
                            ["le"] = buckets[i].ToString(CultureInfo.InvariantCulture)
                        };
                        WriteTags(sb, bucketTags);
                        sb.Append(' ').Append(cumulative).AppendLine();
                    }
                    // +Inf bucket
                    cumulative += histogram.BucketCounts[^1];
                    sb.Append(histogram.Name).Append("_bucket");
                    var infTags = new Dictionary<string, string>(histogram.Tags) { ["le"] = "+Inf" };
                    WriteTags(sb, infTags);
                    sb.Append(' ').Append(cumulative).AppendLine();
                    
                    // Sum and count
                    sb.Append(histogram.Name).Append("_sum");
                    WriteTags(sb, histogram.Tags);
                    sb.Append(' ').Append(histogram.Sum.ToString("G17", CultureInfo.InvariantCulture)).AppendLine();
                    
                    sb.Append(histogram.Name).Append("_count");
                    WriteTags(sb, histogram.Tags);
                    sb.Append(' ').Append(histogram.Count).AppendLine();
                }
            }
            
            // Gauges
            foreach (var (key, gauge) in _gauges.OrderBy(kv => kv.Key.Name).ThenBy(kv => kv.Key.TagsKey))
            {
                WriteHelp(sb, gauge.Name, "gauge", gauge.Description, writtenHelp);
                sb.Append(gauge.Name);
                WriteTags(sb, gauge.Tags);
                sb.Append(' ');
                sb.Append(gauge.Value);
                sb.AppendLine();
            }
        }
        
        return sb.ToString();
    }
    
    private static void WriteHelp(StringBuilder sb, string name, string type, string? description, HashSet<string> written)
    {
        if (written.Add(name))
        {
            if (!string.IsNullOrEmpty(description))
            {
                sb.Append("# HELP ").Append(name).Append(' ').AppendLine(description);
            }
            sb.Append("# TYPE ").Append(name).Append(' ').AppendLine(type);
        }
    }
    
    private static void WriteTags(StringBuilder sb, Dictionary<string, string> tags)
    {
        if (tags.Count == 0) return;
        
        sb.Append('{');
        bool first = true;
        foreach (var (k, v) in tags.OrderBy(kv => kv.Key))
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append(k).Append("=\"").Append(EscapeLabelValue(v)).Append('"');
        }
        sb.Append('}');
    }
    
    private static string EscapeLabelValue(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
    
    /// <summary>
    /// Builds a MetricKey from tags. Keys are bounded by the operation allowlist
    /// in ServiceModuleRegistry.
    /// </summary>
    private static (MetricKey Key, Dictionary<string, string> Tags) BuildKey(
        string name, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var dict = new Dictionary<string, string>(tags.Length);
        var sb = new StringBuilder(64);
        sb.Append(name);
        
        // Copy and sort tags for consistent key
        var sortedTags = new List<(string Key, string Value)>(tags.Length);
        
        foreach (var tag in tags)
        {
            var key = tag.Key;
            var value = tag.Value?.ToString() ?? "";
            sortedTags.Add((key, value));
            dict[key] = value;
        }
        
        sortedTags.Sort((a, b) => string.Compare(a.Key, b.Key, StringComparison.Ordinal));
        
        sb.Append('{');
        for (int i = 0; i < sortedTags.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(sortedTags[i].Key).Append('=').Append(sortedTags[i].Value);
        }
        sb.Append('}');
        
        return (new MetricKey(name, sb.ToString()), dict);
    }
    
    public void Dispose() => _listener.Dispose();
    
    /// <summary>
    /// Struct key for metrics dictionary to reduce allocations on lookup.
    /// </summary>
    private readonly struct MetricKey : IEquatable<MetricKey>
    {
        public readonly string Name;
        public readonly string TagsKey;
        
        public MetricKey(string name, string tagsKey)
        {
            Name = name;
            TagsKey = tagsKey;
        }
        
        public bool Equals(MetricKey other) => 
            Name == other.Name && TagsKey == other.TagsKey;
        
        public override bool Equals(object? obj) => obj is MetricKey other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(Name, TagsKey);
    }
    
    private sealed class CounterState
    {
        public required string Name { get; init; }
        public required Dictionary<string, string> Tags { get; init; }
        public string? Description { get; init; }
        public long Value;
    }
    
    private sealed class HistogramState
    {
        public string Name { get; }
        public Dictionary<string, string> Tags { get; }
        public string? Description { get; }
        public double[] BucketBoundaries { get; }
        public long[] BucketCounts { get; }
        public long Count;
        public double Sum;
        
        public HistogramState(string name, Dictionary<string, string> tags, string? description, double[] buckets)
        {
            Name = name;
            Tags = tags;
            Description = description;
            BucketBoundaries = buckets;
            BucketCounts = new long[buckets.Length + 1]; // +1 for overflow
        }
        
        public void Record(double value)
        {
            Count++;
            Sum += value;
            
            for (int i = 0; i < BucketBoundaries.Length; i++)
            {
                if (value <= BucketBoundaries[i])
                {
                    BucketCounts[i]++;
                    return;
                }
            }
            BucketCounts[^1]++; // Overflow bucket
        }
    }
    
    private sealed class GaugeState
    {
        public required string Name { get; init; }
        public required Dictionary<string, string> Tags { get; init; }
        public string? Description { get; init; }
        public long Value;
    }
}
