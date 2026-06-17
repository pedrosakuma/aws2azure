using System;
using System.Text;
using System.Text.Json;
using Aws2Azure.Modules.DynamoDb.Internal;
using Aws2Azure.Modules.DynamoDb.Operations;
using Aws2Azure.Modules.DynamoDb.Persistence;
using Aws2Azure.UnitTests.DynamoDb;
using Xunit;
using Xunit.Abstractions;

namespace Aws2Azure.UnitTests.DynamoDb.Persistence;

/// <summary>
/// Tier 0 mechanism guard (issue #420) for the GetItem-on-CosmosBinary fused
/// path (#321). The dev-only BenchmarkDotNet
/// <c>CosmosFusedEnvelopeBenchmarks</c> proves the CPU/alloc win but never runs
/// in CI; this is a cheap, deterministic per-PR assertion that the fused path
/// keeps allocating strictly less than the previous decode-to-text two-pass
/// path, so a refactor that silently re-materializes the intermediate JSON text
/// (collapsing the win) fails the build.
///
/// <para>Both arms drive the SAME production transform
/// (<see cref="InferredAttributeStorage.WriteGetItemEnvelope{TReader}"/>):</para>
/// <list type="bullet">
///   <item><b>two-pass</b>: <c>CosmosBinaryDecoder.Decode</c> (binary → JSON
///   text, which spins up its own <c>Utf8JsonWriter</c>) then the transform over
///   the text.</item>
///   <item><b>fused</b>: the transform driven straight off the
///   <c>CosmosBinaryReader</c>, skipping the intermediate text materialization
///   and its writer.</item>
/// </list>
///
/// <para>Per-op managed allocation is measured with
/// <see cref="GC.GetAllocatedBytesForCurrentThread"/> (exact, not sampled) and
/// the minimum across rounds is taken so sporadic test-infra allocations on the
/// thread cannot inflate the figure. The saving is a fixed ~168 B/op (one
/// intermediate <c>Utf8JsonWriter</c>'s managed state) independent of document
/// shape; the floor below leaves headroom for minor runtime drift while still
/// catching a collapse of the mechanism. Deterministic and reproducible
/// byte-for-byte across runs — not a flaky timing benchmark.</para>
/// </summary>
public class CosmosFusedEnvelopeAllocTests
{
    // Observed saving is a stable 168 B/op across all shapes (one Utf8JsonWriter
    // of intermediate state). Floor at 96 catches a regression that re-adds the
    // intermediate text pass while tolerating runtime-version drift in the
    // common writer overhead. Bump deliberately if a runtime change is expected
    // to move it — never by accident.
    private const double MinSavingBytesPerOp = 96.0;

    private const int Iterations = 20_000;
    private const int Rounds = 3;
    private const int Warmup = 500;

    private readonly ITestOutputHelper _output;

    public CosmosFusedEnvelopeAllocTests(ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData(0, 0, 0)]      // lean
    [InlineData(0, 0, 512)]    // payload-heavy
    [InlineData(20, 20, 0)]    // wide
    public void Fused_path_allocates_less_than_two_pass(int stringAttrs, int numberAttrs, int payloadBytes)
    {
        byte[] binary = BuildBinaryDocument(stringAttrs, numberAttrs, payloadBytes);

        // Correctness gate: the two arms must be byte-identical, so the alloc
        // numbers describe a correct transform and not a shortcut.
        Assert.True(
            RunTwoPass(binary).AsSpan().SequenceEqual(RunFused(binary)),
            "fused and two-pass envelopes diverge — alloc comparison would be meaningless.");

        for (int i = 0; i < Warmup; i++)
        {
            _ = RunTwoPass(binary);
            _ = RunFused(binary);
        }

        double twoPass = MeasureMinBytesPerOp(() => RunTwoPass(binary));
        double fused = MeasureMinBytesPerOp(() => RunFused(binary));
        double saving = twoPass - fused;

        _output.WriteLine(
            $"s={stringAttrs} n={numberAttrs} pay={payloadBytes}  " +
            $"twoPass={twoPass:F0} B/op  fused={fused:F0} B/op  saving={saving:F0} B/op");

        Assert.True(fused < twoPass,
            $"fused allocated {fused:F0} B/op, not less than two-pass {twoPass:F0} B/op — " +
            "the CosmosBinary fused path lost its allocation advantage.");
        Assert.True(saving >= MinSavingBytesPerOp,
            $"fused saved only {saving:F0} B/op (floor {MinSavingBytesPerOp:F0}) vs two-pass — " +
            "the intermediate decode-to-text pass appears to have crept back in.");
    }

    private static double MeasureMinBytesPerOp(Func<byte[]> arm)
    {
        double best = double.MaxValue;
        for (int round = 0; round < Rounds; round++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < Iterations; i++)
            {
                _ = arm();
            }
            double perOp = (GC.GetAllocatedBytesForCurrentThread() - before) / (double)Iterations;
            if (perOp < best)
            {
                best = perOp;
            }
        }

        return best;
    }

    private static byte[] BuildBinaryDocument(int stringAttrs, int numberAttrs, int payloadBytes)
    {
        string ddbItem = MakeItem(stringAttrs, numberAttrs, payloadBytes);
        using var itemDoc = JsonDocument.Parse(ddbItem);
        string cosmosJson = InferredAttributeStorage.BuildCosmosDocument("sortKey", "p", itemDoc.RootElement);
        return CosmosBinaryTestEncoder.Encode(cosmosJson);
    }

    private static byte[] RunTwoPass(ReadOnlySpan<byte> binary)
    {
        // Mirrors the production decode-to-text fallback in
        // CosmosOpsShared.WriteGetItemEnvelopeAsync (decode into a pooled buffer,
        // then transform into a second pooled scratch) so the only allocation
        // delta vs the fused helper is the intermediate decode's Utf8JsonWriter.
        using var json = new PooledByteBufferWriter(Math.Max(4096, binary.Length));
        CosmosBinaryDecoder.Decode(binary, json);

        using var scratch = new PooledByteBufferWriter(1024);
        using (var writer = new Utf8JsonWriter(scratch))
        {
            InferredAttributeStorage.WriteGetItemEnvelope(writer, json.WrittenMemory.Span);
            writer.Flush();
        }

        return scratch.WrittenMemory.Span.ToArray();
    }

    private static byte[] RunFused(ReadOnlySpan<byte> binary)
    {
        // Drives the SHIPPED fused helper so a regression that re-introduces the
        // decode-to-text pass inside production code (not just the low-level
        // primitive) trips this guard.
        if (!CosmosOpsShared.TryWriteFusedBinaryEnvelope(binary, out PooledByteBufferWriter? envelope))
        {
            throw new InvalidOperationException("fused helper declined the synthetic binary body.");
        }

        using (envelope)
        {
            return envelope!.WrittenMemory.Span.ToArray();
        }
    }

    private static string MakeItem(int stringAttrs, int numberAttrs, int payloadBytes)
    {
        var sb = new StringBuilder();
        sb.Append("{\"pk\":{\"S\":\"p\"}");
        for (int i = 0; i < stringAttrs; i++)
        {
            sb.Append(",\"s").Append(i).Append("\":{\"S\":\"value_").Append(i).Append("\"}");
        }

        for (int i = 0; i < numberAttrs; i++)
        {
            sb.Append(",\"n").Append(i).Append("\":{\"N\":\"").Append(i * 7).Append("\"}");
        }

        if (payloadBytes > 0)
        {
            sb.Append(",\"payload\":{\"S\":\"").Append('x', payloadBytes).Append("\"}");
        }

        sb.Append('}');
        return sb.ToString();
    }
}
