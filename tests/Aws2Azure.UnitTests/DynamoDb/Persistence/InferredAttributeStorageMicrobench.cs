using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using Aws2Azure.Modules.DynamoDb.Persistence;
using Xunit.Abstractions;

namespace Aws2Azure.UnitTests.DynamoDb.Persistence;

/// <summary>
/// Microbench for <see cref="InferredAttributeStorage"/> encode/decode
/// throughput at typical DDB item sizes. Skipped unless
/// <c>AWS2AZURE_PERF=1</c> is set in the environment so CI stays fast
/// and the numbers are reproducible only on the same kind of run as
/// the proxy/baseline perf suite.
/// </summary>
public class InferredAttributeStorageMicrobench
{
    private readonly ITestOutputHelper _output;

    public InferredAttributeStorageMicrobench(ITestOutputHelper output)
    {
        _output = output;
    }

    private static bool PerfEnabled =>
        string.Equals(Environment.GetEnvironmentVariable("AWS2AZURE_PERF"), "1", StringComparison.Ordinal);

    [Theory]
    [InlineData(10)]
    [InlineData(20)]
    [InlineData(100)]
    [InlineData(1000)]
    public void Encode_decode_throughput(int attrCount)
    {
        if (!PerfEnabled)
        {
            _output.WriteLine("Skipped (set AWS2AZURE_PERF=1 to run).");
            return;
        }

        var itemJson = MakeRealisticItem(attrCount);
        using var itemDoc = JsonDocument.Parse(itemJson);
        var item = itemDoc.RootElement;

        // Warmup.
        for (int i = 0; i < 200; i++)
            InferredAttributeStorage.BuildCosmosDocument("id", "pk", item);

        int iters = attrCount switch
        {
            <= 20 => 200_000,
            <= 100 => 30_000,
            _ => 3_000,
        };

        var sw = Stopwatch.StartNew();
        string lastDoc = string.Empty;
        for (int i = 0; i < iters; i++)
            lastDoc = InferredAttributeStorage.BuildCosmosDocument("id", "pk", item);
        sw.Stop();
        var encodeNsPerOp = sw.Elapsed.TotalMilliseconds * 1_000_000d / iters;

        // Decode warmup.
        for (int i = 0; i < 200; i++)
        {
            using var ms = new MemoryStream(Encoding.UTF8.GetBytes(lastDoc));
            InferredAttributeStorage.ExtractItem(ms);
        }

        sw.Restart();
        for (int i = 0; i < iters; i++)
        {
            using var ms = new MemoryStream(Encoding.UTF8.GetBytes(lastDoc));
            InferredAttributeStorage.ExtractItem(ms);
        }
        sw.Stop();
        var decodeNsPerOp = sw.Elapsed.TotalMilliseconds * 1_000_000d / iters;

        var docBytes = Encoding.UTF8.GetByteCount(lastDoc);
        _output.WriteLine(
            $"attrs={attrCount,5}  encode={encodeNsPerOp,8:F0} ns/op   decode={decodeNsPerOp,8:F0} ns/op   docBytes={docBytes}");

        // Soft gate: at 20 attrs (typical small DDB item) both encode and
        // decode must stay under 25 microseconds on the perf box — well
        // below the Cosmos network p50 (~100ms) so the per-op CPU
        // overhead is < 0.05% of the budget.
        if (attrCount == 20)
        {
            Assert.True(encodeNsPerOp < 25_000,
                $"encode at 20 attrs took {encodeNsPerOp:F0} ns/op (gate: <25000)");
            Assert.True(decodeNsPerOp < 25_000,
                $"decode at 20 attrs took {decodeNsPerOp:F0} ns/op (gate: <25000)");
        }
    }

    /// <summary>
    /// Builds a synthetic DDB item map that mirrors a typical OLTP row:
    /// 60% String, 25% Number, 10% Bool, 5% Map. Numbers stay within
    /// decimal precision so the bare-Number fast path dominates.
    /// </summary>
    private static string MakeRealisticItem(int attrs)
    {
        var sb = new StringBuilder();
        sb.Append('{');
        for (int i = 0; i < attrs; i++)
        {
            if (i > 0) sb.Append(',');
            var mod = i % 20;
            if (mod < 12)
                sb.Append("\"s").Append(i).Append("\":{\"S\":\"value_").Append(i).Append("\"}");
            else if (mod < 17)
                sb.Append("\"n").Append(i).Append("\":{\"N\":\"").Append(i * 7).Append("\"}");
            else if (mod < 19)
                sb.Append("\"b").Append(i).Append("\":{\"BOOL\":true}");
            else
                sb.Append("\"m").Append(i).Append("\":{\"M\":{\"k\":{\"S\":\"v\"}}}");
        }
        sb.Append('}');
        return sb.ToString();
    }
}
