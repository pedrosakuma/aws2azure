using System;
using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace Aws2Azure.UnitTests.DynamoDb;

/// <summary>
/// Allocation micro-measurement isolating the Cosmos request-URI build. The
/// account base URI is constant per <c>CosmosClient</c>, so re-parsing it per
/// request (<c>new Uri(endpoint.TrimEnd('/') + "/")</c>) was pure waste. This
/// bench compares the old per-call base parse against the hoisted-base path
/// (only the relative resolve remains) using
/// <see cref="GC.GetAllocatedBytesForCurrentThread"/>. Gated by
/// <c>AWS2AZURE_PERF=1</c> so CI stays fast.
/// </summary>
public class CosmosUriAllocBench
{
    private readonly ITestOutputHelper _output;

    public CosmosUriAllocBench(ITestOutputHelper output) => _output = output;

    private static bool PerfEnabled =>
        string.Equals(Environment.GetEnvironmentVariable("AWS2AZURE_PERF"), "1", StringComparison.Ordinal);

    [Fact]
    public void Hoisted_base_allocates_less_per_op()
    {
        if (!PerfEnabled)
        {
            _output.WriteLine("Skipped (set AWS2AZURE_PERF=1 to run).");
            return;
        }

        const string endpoint = "https://example.documents.azure.com:443/";
        const string requestUri = "/dbs/main/colls/orders/docs/pk-12345";
        var hoistedBase = new Uri(endpoint.TrimEnd('/') + "/", UriKind.Absolute);

        const int iters = 100_000;

        for (int i = 0; i < 1000; i++) { _ = OldPath(); _ = NewPath(); }

        var sw = Stopwatch.StartNew();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < iters; i++) _ = OldPath();
        var oldBytes = (GC.GetAllocatedBytesForCurrentThread() - before) / (double)iters;
        var oldNs = sw.Elapsed.TotalNanoseconds / iters;

        sw.Restart();
        before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < iters; i++) _ = NewPath();
        var newBytes = (GC.GetAllocatedBytesForCurrentThread() - before) / (double)iters;
        var newNs = sw.Elapsed.TotalNanoseconds / iters;

        var reduction = oldBytes <= 0 ? 0 : (1 - newBytes / oldBytes) * 100;
        var speedup = newNs <= 0 ? 0 : oldNs / newNs;
        _output.WriteLine(
            $"old(parse-base)={oldBytes,8:F0} B/op {oldNs,7:F0} ns/op   "
            + $"new(hoisted)={newBytes,8:F0} B/op {newNs,7:F0} ns/op ({reduction,5:F1}% {speedup,4:F2}x)");

        Assert.True(newBytes < oldBytes,
            $"hoisted base allocated {newBytes:F0} B/op vs per-call parse {oldBytes:F0} B/op");

        Uri OldPath() => new(
            new Uri(endpoint.TrimEnd('/') + "/", UriKind.Absolute), requestUri.TrimStart('/'));

        Uri NewPath() => new(hoistedBase, requestUri.TrimStart('/'));
    }
}
