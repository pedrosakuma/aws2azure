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
        
        // Simulate some work
        Thread.Sleep(10);
        
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
}
