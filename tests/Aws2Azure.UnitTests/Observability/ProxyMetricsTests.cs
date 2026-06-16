using System.Diagnostics.Metrics;
using Aws2Azure.Core.Observability;

namespace Aws2Azure.UnitTests.Observability;

public class ProxyMetricsTests
{
    [Fact]
    public void StartRequest_increments_active_requests()
    {
        var metrics = new ProxyMetrics();
        var ctx = metrics.StartRequest("s3", "GetObject", 1024);
        
        Assert.Equal("s3", ctx.Service);
        Assert.Equal("GetObject", ctx.Operation);
        Assert.True(ctx.StartTimestamp > 0);
    }
    
    [Fact]
    public void EndRequest_records_duration_and_counters()
    {
        var metrics = new ProxyMetrics();
        var ctx = metrics.StartRequest("dynamodb", "PutItem", 512);

        metrics.EndRequest(ctx, 200, 256, TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(5));

        // No exception means success - actual values verified via PrometheusExporter
    }
    
    [Fact]
    public void RecordBackendCall_does_not_throw()
    {
        var metrics = new ProxyMetrics();
        metrics.RecordBackendCall("sqs", "SendMessage", TimeSpan.FromMilliseconds(100));
    }
}

public class PrometheusExporterTests
{
    [Fact]
    public void Export_returns_prometheus_format()
    {
        var metrics = new ProxyMetrics();
        using var exporter = new PrometheusExporter();
        
        // Record some metrics
        var ctx = metrics.StartRequest("s3", "GetObject", 1024);
        metrics.EndRequest(ctx, 200, 2048, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(10));
        
        var ctx2 = metrics.StartRequest("dynamodb", "Query", 256);
        metrics.EndRequest(ctx2, 200, 4096, TimeSpan.FromMilliseconds(2), TimeSpan.FromMilliseconds(20));
        
        var output = exporter.Export();
        
        // Verify Prometheus format
        Assert.Contains("# TYPE aws2azure_requests_total counter", output);
        Assert.Contains("aws2azure_requests_total{", output);
        Assert.Contains("service=\"s3\"", output);
        Assert.Contains("service=\"dynamodb\"", output);
        Assert.Contains("status=\"2xx\"", output);
    }
    
    [Fact]
    public void Export_includes_histogram_buckets()
    {
        var metrics = new ProxyMetrics();
        using var exporter = new PrometheusExporter();
        
        var ctx = metrics.StartRequest("s3", "PutObject", 1024);
        metrics.EndRequest(ctx, 200, 0, TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(50));
        
        var output = exporter.Export();
        
        // Histograms should have _bucket, _sum, _count
        Assert.Contains("_bucket{", output);
        Assert.Contains("le=\"", output);
        Assert.Contains("_sum", output);
        Assert.Contains("_count", output);
    }
    
    [Fact]
    public void Export_records_error_metrics()
    {
        var metrics = new ProxyMetrics();
        using var exporter = new PrometheusExporter();
        
        var ctx = metrics.StartRequest("dynamodb", "GetItem", 128);
        metrics.EndRequest(ctx, 500, 64, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(5));
        
        var output = exporter.Export();
        
        Assert.Contains("aws2azure_errors_total{", output);
        Assert.Contains("status_code=\"500\"", output);
    }

    [Fact]
    public void Export_renders_runtime_total_metrics_as_counters_and_memory_as_gauges()
    {
        var metrics = new ProxyMetrics();
        using var exporter = new PrometheusExporter();

        var output = exporter.Export();

        // Monotonic _total runtime metrics must advertise the counter type so
        Assert.Contains("# TYPE aws2azure_dotnet_gc_allocated_bytes_total counter", output);
        Assert.Contains("# TYPE aws2azure_dotnet_gc_gen2_collections_total counter", output);

        // Point-in-time runtime metrics stay gauges.
        Assert.Contains("# TYPE aws2azure_process_working_set_bytes gauge", output);
        Assert.Contains("# TYPE aws2azure_dotnet_gc_heap_size_bytes gauge", output);
    }

    [Fact]
    public void Export_observable_counter_uses_set_semantics_not_accumulation()
    {
        // An ObservableCounter reports its cumulative total on each scrape, so the
        // exporter must SET it — not accumulate like Counter.Add would (issue #276).
        using var meter = new Meter(ProxyMetrics.MeterName);
        long total = 100;
        meter.CreateObservableCounter("aws2azure_test_obs_total", () => total, description: "test");

        using var exporter = new PrometheusExporter();

        var first = exporter.Export();
        Assert.Contains("# TYPE aws2azure_test_obs_total counter", first);
        Assert.Contains("aws2azure_test_obs_total 100", first);

        // Re-scraping the same cumulative value must not double it to 200.
        var second = exporter.Export();
        Assert.Contains("aws2azure_test_obs_total 100", second);
        Assert.DoesNotContain("aws2azure_test_obs_total 200", second);

        // When the cumulative total advances, the new value is reflected exactly.
        total = 175;
        var third = exporter.Export();
        Assert.Contains("aws2azure_test_obs_total 175", third);
    }

    [Fact]
    public void Export_renders_fractional_observable_gauge_with_invariant_decimal()
    {
        // Double-valued metrics must render culture-invariantly with a '.' decimal
        // separator and no precision loss (issue #278).
        using var meter = new Meter(ProxyMetrics.MeterName);
        meter.CreateObservableGauge("aws2azure_test_ratio", () => 1.75, description: "test");

        using var exporter = new PrometheusExporter();

        var output = exporter.Export();
        Assert.Contains("# TYPE aws2azure_test_ratio gauge", output);
        Assert.Contains("aws2azure_test_ratio 1.75", output);
    }

    [Fact]
    public void Export_renders_fractional_observable_counter_with_invariant_decimal()
    {
        using var meter = new Meter(ProxyMetrics.MeterName);
        meter.CreateObservableCounter("aws2azure_test_seconds_total", () => 2.5, description: "test");

        using var exporter = new PrometheusExporter();

        var output = exporter.Export();
        Assert.Contains("# TYPE aws2azure_test_seconds_total counter", output);
        Assert.Contains("aws2azure_test_seconds_total 2.5", output);
    }

    [Fact]
    public void Export_renders_whole_valued_double_as_integer_no_fractional_suffix()
    {
        // A whole-number double must render as a plain integer ("42", not "42.0"
        // or "4.2E+01") so integer consumers like the perf harness keep parsing it.
        using var meter = new Meter(ProxyMetrics.MeterName);
        meter.CreateObservableGauge("aws2azure_test_whole", () => 42.0, description: "test");

        using var exporter = new PrometheusExporter();

        var output = exporter.Export();
        Assert.Contains("aws2azure_test_whole 42\n", output.Replace("\r\n", "\n"));
        Assert.DoesNotContain("aws2azure_test_whole 42.0", output);
    }
}
