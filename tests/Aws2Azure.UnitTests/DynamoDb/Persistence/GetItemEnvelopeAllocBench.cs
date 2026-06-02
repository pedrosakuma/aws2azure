using System;
using System.Buffers;
using System.Text;
using System.Text.Json;
using Aws2Azure.Modules.DynamoDb.Operations;
using Aws2Azure.Modules.DynamoDb.Persistence;
using Xunit;
using Xunit.Abstractions;

namespace Aws2Azure.UnitTests.DynamoDb.Persistence;

/// <summary>
/// Allocation micro-measurement isolating the GetItem response transform
/// (no network, no HttpContext) so the allocation-rate win is attributable
/// to the transform itself and not drowned by Cosmos/emulator noise — per
/// the design-review measurement methodology.
///
/// Compares the current materialized path
/// (<c>ExtractItem → GetItemResponse → JsonSerializer</c>) against the new
/// model-elimination path (<see cref="InferredAttributeStorage.WriteGetItemEnvelope"/>
/// into a reused buffer) using
/// <see cref="GC.GetAllocatedBytesForCurrentThread"/>. Gated by
/// <c>AWS2AZURE_PERF=1</c> so CI stays fast; the soft assertion only runs
/// when enabled.
/// </summary>
public class GetItemEnvelopeAllocBench
{
    private readonly ITestOutputHelper _output;

    public GetItemEnvelopeAllocBench(ITestOutputHelper output) => _output = output;

    private static bool PerfEnabled =>
        string.Equals(Environment.GetEnvironmentVariable("AWS2AZURE_PERF"), "1", StringComparison.Ordinal);

    [Theory]
    [InlineData(10)]
    [InlineData(20)]
    [InlineData(50)]
    public void New_path_allocates_less_per_op(int attrCount)
    {
        if (!PerfEnabled)
        {
            _output.WriteLine("Skipped (set AWS2AZURE_PERF=1 to run).");
            return;
        }

        var ddbItem = MakeItem(attrCount);
        using var itemDoc = JsonDocument.Parse(ddbItem);
        var cosmosJson = InferredAttributeStorage.BuildCosmosDocument("sortKey", "p", itemDoc.RootElement);
        using var cosmosDoc = JsonDocument.Parse(cosmosJson);
        var root = cosmosDoc.RootElement;
        var cosmosUtf8 = Encoding.UTF8.GetBytes(cosmosJson);

        const int iters = 50_000;

        // Warmup all paths.
        for (int i = 0; i < 500; i++) { _ = CurrentPath(root); NewPath(root); NewPathStreaming(cosmosUtf8); }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < iters; i++) _ = CurrentPath(root);
        var currentBytes = (GC.GetAllocatedBytesForCurrentThread() - before) / (double)iters;
        var currentNs = sw.Elapsed.TotalNanoseconds / iters;

        // Per-iteration pooled scratch buffer + writer — mirrors exactly what
        // CosmosOpsShared.WriteGetItemEnvelopeAsync does per request (the rented
        // array is pooled, so it is not a steady-state per-op allocation).
        sw.Restart();
        before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < iters; i++)
        {
            using var scratch = new PooledByteBufferWriter(1024);
            using var writer = new Utf8JsonWriter(scratch);
            InferredAttributeStorage.WriteGetItemEnvelope(writer, root);
            writer.Flush();
            _ = scratch.WrittenMemory;
        }
        var newBytes = (GC.GetAllocatedBytesForCurrentThread() - before) / (double)iters;
        var newNs = sw.Elapsed.TotalNanoseconds / iters;

        // Streaming Utf8JsonReader → Utf8JsonWriter pump (no DOM / no String).
        sw.Restart();
        before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < iters; i++)
        {
            using var scratch = new PooledByteBufferWriter(1024);
            using var writer = new Utf8JsonWriter(scratch);
            InferredAttributeStorage.WriteGetItemEnvelope(writer, cosmosUtf8.AsSpan());
            writer.Flush();
            _ = scratch.WrittenMemory;
        }
        var streamBytes = (GC.GetAllocatedBytesForCurrentThread() - before) / (double)iters;
        var streamNs = sw.Elapsed.TotalNanoseconds / iters;

        var reduction = currentBytes <= 0 ? 0 : (1 - newBytes / currentBytes) * 100;
        var streamReduction = currentBytes <= 0 ? 0 : (1 - streamBytes / currentBytes) * 100;
        var speedup = newNs <= 0 ? 0 : currentNs / newNs;
        var streamSpeedup = streamNs <= 0 ? 0 : currentNs / streamNs;
        _output.WriteLine(
            $"attrs={attrCount,3}  current={currentBytes,8:F0} B/op {currentNs,7:F0} ns/op   "
            + $"jsonelem={newBytes,8:F0} B/op {newNs,7:F0} ns/op ({reduction,5:F1}% {speedup,4:F2}x)   "
            + $"streaming={streamBytes,8:F0} B/op {streamNs,7:F0} ns/op ({streamReduction,5:F1}% {streamSpeedup,4:F2}x)");

        Assert.True(newBytes < currentBytes,
            $"new path allocated {newBytes:F0} B/op vs current {currentBytes:F0} B/op");
        Assert.True(streamBytes < newBytes,
            $"streaming path allocated {streamBytes:F0} B/op vs jsonelem {newBytes:F0} B/op");
    }

    private static void NewPathStreaming(byte[] cosmosUtf8)
    {
        using var scratch = new PooledByteBufferWriter(1024);
        using var writer = new Utf8JsonWriter(scratch);
        InferredAttributeStorage.WriteGetItemEnvelope(writer, cosmosUtf8.AsSpan());
        writer.Flush();
    }

    private static byte[] CurrentPath(JsonElement cosmosDocRoot)
    {
        var item = InferredAttributeStorage.ExtractItem(cosmosDocRoot);
        var response = new GetItemResponse { Item = item };
        return JsonSerializer.SerializeToUtf8Bytes(response, ItemJsonContext.Default.GetItemResponse);
    }

    private static void NewPath(JsonElement cosmosDocRoot)
    {
        using var scratch = new PooledByteBufferWriter(1024);
        using var writer = new Utf8JsonWriter(scratch);
        InferredAttributeStorage.WriteGetItemEnvelope(writer, cosmosDocRoot);
        writer.Flush();
    }

    private static string MakeItem(int attrs)
    {
        var sb = new StringBuilder();
        sb.Append("{\"pk\":{\"S\":\"p\"}");
        for (int i = 0; i < attrs; i++)
        {
            var mod = i % 20;
            if (mod < 12)
                sb.Append(",\"s").Append(i).Append("\":{\"S\":\"value_").Append(i).Append("\"}");
            else if (mod < 17)
                sb.Append(",\"n").Append(i).Append("\":{\"N\":\"").Append(i * 7).Append("\"}");
            else if (mod < 19)
                sb.Append(",\"b").Append(i).Append("\":{\"BOOL\":true}");
            else
                sb.Append(",\"m").Append(i).Append("\":{\"M\":{\"k\":{\"S\":\"v\"}}}");
        }
        sb.Append('}');
        return sb.ToString();
    }
}
