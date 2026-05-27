using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text;

namespace Aws2Azure.Core.Observability;

/// <summary>
/// Exports metrics in Prometheus text format. AOT-compatible implementation
/// that reads from System.Diagnostics.Metrics via MeterListener.
/// </summary>
public sealed class PrometheusExporter : IDisposable
{
    private readonly MeterListener _listener;
    private readonly object _lock = new();
    
    // Accumulated metric state
    private readonly Dictionary<string, CounterState> _counters = new();
    private readonly Dictionary<string, HistogramState> _histograms = new();
    private readonly Dictionary<string, GaugeState> _gauges = new();
    
    // Standard histogram buckets (Prometheus defaults)
    private static readonly double[] HistogramBuckets = 
        [0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10];
    
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
        var tagDict = TagsToDict(tags);
        var key = BuildKey(instrument.Name, tagDict);
        
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
                if (!_histograms.TryGetValue(key, out var histogram))
                {
                    histogram = new HistogramState { Name = instrument.Name, Tags = tagDict, Description = instrument.Description };
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
        var tagDict = TagsToDict(tags);
        var key = BuildKey(instrument.Name, tagDict);
        
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
                if (!_histograms.TryGetValue(key, out var histogram))
                {
                    histogram = new HistogramState { Name = instrument.Name, Tags = tagDict, Description = instrument.Description };
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
            foreach (var (_, counter) in _counters.OrderBy(kv => kv.Key))
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
                    // Bucket lines
                    long cumulative = 0;
                    for (int i = 0; i < HistogramBuckets.Length; i++)
                    {
                        cumulative += histogram.Buckets[i];
                        sb.Append(histogram.Name).Append("_bucket");
                        var bucketTags = new Dictionary<string, string>(histogram.Tags)
                        {
                            ["le"] = HistogramBuckets[i].ToString(CultureInfo.InvariantCulture)
                        };
                        WriteTags(sb, bucketTags);
                        sb.Append(' ').Append(cumulative).AppendLine();
                    }
                    // +Inf bucket
                    cumulative += histogram.Buckets[^1];
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
            foreach (var (_, gauge) in _gauges.OrderBy(kv => kv.Key))
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
    
    private static Dictionary<string, string> TagsToDict(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var dict = new Dictionary<string, string>(tags.Length);
        foreach (var tag in tags)
        {
            dict[tag.Key] = tag.Value?.ToString() ?? "";
        }
        return dict;
    }
    
    private static string BuildKey(string name, Dictionary<string, string> tags)
    {
        if (tags.Count == 0) return name;
        var sorted = string.Join(",", tags.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"));
        return $"{name}{{{sorted}}}";
    }
    
    public void Dispose() => _listener.Dispose();
    
    private sealed class CounterState
    {
        public required string Name { get; init; }
        public required Dictionary<string, string> Tags { get; init; }
        public string? Description { get; init; }
        public long Value;
    }
    
    private sealed class HistogramState
    {
        public required string Name { get; init; }
        public required Dictionary<string, string> Tags { get; init; }
        public string? Description { get; init; }
        public long Count;
        public double Sum;
        public long[] Buckets = new long[HistogramBuckets.Length + 1]; // +1 for overflow
        
        public void Record(double value)
        {
            Count++;
            Sum += value;
            
            for (int i = 0; i < HistogramBuckets.Length; i++)
            {
                if (value <= HistogramBuckets[i])
                {
                    Buckets[i]++;
                    return;
                }
            }
            Buckets[^1]++; // Overflow bucket
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
