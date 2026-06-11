using Xunit;

namespace Aws2Azure.PerfTests;

public sealed class ProxyMemoryProbeTests
{
    private const string Sample = """
        # HELP aws2azure_active_requests Number of requests currently being processed
        # TYPE aws2azure_active_requests gauge
        aws2azure_active_requests 0
        # HELP aws2azure_process_working_set_bytes Resident working set of the proxy process in bytes
        # TYPE aws2azure_process_working_set_bytes gauge
        aws2azure_process_working_set_bytes 34185216
        # TYPE aws2azure_dotnet_gc_heap_size_bytes gauge
        aws2azure_dotnet_gc_heap_size_bytes 4030920
        # TYPE aws2azure_dotnet_gc_allocated_bytes_total gauge
        aws2azure_dotnet_gc_allocated_bytes_total 7873792
        # TYPE aws2azure_dotnet_gc_gen2_collections_total gauge
        aws2azure_dotnet_gc_gen2_collections_total 2
        """;

    [Fact]
    public void Parse_extracts_all_four_runtime_gauges()
    {
        var snap = ProxyMemoryProbe.Parse(Sample);
        Assert.NotNull(snap);
        Assert.Equal(34185216, snap!.Value.WorkingSetBytes);
        Assert.Equal(4030920, snap.Value.GcHeapBytes);
        Assert.Equal(7873792, snap.Value.AllocatedBytesTotal);
        Assert.Equal(2, snap.Value.Gen2Collections);
    }

    [Fact]
    public void Parse_returns_null_when_a_gauge_is_missing()
    {
        // Drop the working-set line — without it the snapshot is incomplete.
        var partial = string.Join('\n',
            Sample.Split('\n').Where(l => !l.Contains("process_working_set_bytes")));
        Assert.Null(ProxyMemoryProbe.Parse(partial));
    }

    [Fact]
    public void Parse_ignores_comment_and_blank_lines()
    {
        Assert.Null(ProxyMemoryProbe.Parse("# HELP something\n\n# TYPE x gauge\n"));
    }
}
