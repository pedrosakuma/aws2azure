using System;
using System.Diagnostics;
using Aws2Azure.Modules.DynamoDb.Internal;
using Xunit;
using Xunit.Abstractions;

namespace Aws2Azure.UnitTests.DynamoDb;

/// <summary>
/// Allocation micro-measurement isolating the Cosmos master-key
/// <c>Authorization</c> header construction (no network) so the win is
/// attributable to the byte pipe and not drowned by emulator noise — same
/// methodology as the SigV4 byte-pipe benches.
///
/// Compares the <c>Build(string)</c> String oracle (per-call
/// <c>FromBase64String</c> + 3× <c>ToLowerInvariant</c> + <c>string.Concat</c>
/// + 2× <c>UTF8.GetBytes</c> + <c>ToBase64String</c> + <c>EscapeDataString</c>)
/// against <see cref="CosmosMasterKeyAuth.BuildAuthHeader"/> (cached key bytes,
/// pooled/stack string-to-sign, hand-rolled percent-encode) using
/// <see cref="GC.GetAllocatedBytesForCurrentThread"/>. Gated by
/// <c>AWS2AZURE_PERF=1</c> so CI stays fast.
/// </summary>
public class CosmosAuthAllocBench
{
    private readonly ITestOutputHelper _output;

    public CosmosAuthAllocBench(ITestOutputHelper output) => _output = output;

    private static bool PerfEnabled =>
        string.Equals(Environment.GetEnvironmentVariable("AWS2AZURE_PERF"), "1", StringComparison.Ordinal);

    [Fact]
    public void Byte_pipe_allocates_less_per_op()
    {
        if (!PerfEnabled)
        {
            _output.WriteLine("Skipped (set AWS2AZURE_PERF=1 to run).");
            return;
        }

        const string verb = "GET";
        const string resourceType = "docs";
        const string resourceLink = "dbs/main/colls/orders/docs/pk-12345";
        const string date = "thu, 27 apr 2017 00:51:12 gmt";
        const string key = "MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=";
        var keyBytes = Convert.FromBase64String(key);

        const int iters = 100_000;

        for (int i = 0; i < 1000; i++)
        {
            _ = CosmosMasterKeyAuth.Build(verb, resourceType, resourceLink, date, key);
            _ = CosmosMasterKeyAuth.BuildAuthHeader(verb, resourceType, resourceLink, date, keyBytes);
        }

        var sw = Stopwatch.StartNew();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < iters; i++)
            _ = CosmosMasterKeyAuth.Build(verb, resourceType, resourceLink, date, key);
        var oldBytes = (GC.GetAllocatedBytesForCurrentThread() - before) / (double)iters;
        var oldNs = sw.Elapsed.TotalNanoseconds / iters;

        sw.Restart();
        before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < iters; i++)
            _ = CosmosMasterKeyAuth.BuildAuthHeader(verb, resourceType, resourceLink, date, keyBytes);
        var newBytes = (GC.GetAllocatedBytesForCurrentThread() - before) / (double)iters;
        var newNs = sw.Elapsed.TotalNanoseconds / iters;

        var reduction = oldBytes <= 0 ? 0 : (1 - newBytes / oldBytes) * 100;
        var speedup = newNs <= 0 ? 0 : oldNs / newNs;
        _output.WriteLine(
            $"old(string)={oldBytes,8:F0} B/op {oldNs,7:F0} ns/op   "
            + $"new(bytepipe)={newBytes,8:F0} B/op {newNs,7:F0} ns/op ({reduction,5:F1}% {speedup,4:F2}x)");

        Assert.True(newBytes < oldBytes,
            $"byte pipe allocated {newBytes:F0} B/op vs string path {oldBytes:F0} B/op");
    }
}
