using Aws2Azure.Core.Buffers;
using Aws2Azure.Benchmarks.DynamoDb.Spike319;
using Aws2Azure.Modules.DynamoDb.Internal;
using Aws2Azure.Modules.DynamoDb.Operations;
using Aws2Azure.Modules.DynamoDb.Persistence;
using BenchmarkDotNet.Attributes;
using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Aws2Azure.Benchmarks.DynamoDb;

/// <summary>
/// Measures the shipped GetItem-on-CosmosBinary fast path (#321) against the
/// previous decode-to-text path, using the PRODUCTION reader + transform:
///
/// <list type="bullet">
///   <item><see cref="TwoPass"/> — the previous shipped path:
///   <c>CosmosBinaryDecoder.Decode</c> (binary → JSON text) then
///   <c>InferredAttributeStorage.WriteGetItemEnvelope(writer, span)</c>
///   (JSON text → envelope, internally a <c>Utf8JsonTokenReader</c>).</item>
///   <item><see cref="Fused"/> — the current shipped path: the SAME generic
///   transform driven straight off the production
///   <c>CosmosBinaryReader</c>, skipping the intermediate JSON-text
///   materialization entirely.</item>
/// </list>
///
/// A <c>[GlobalSetup]</c> assertion pins both arms byte-identical over each
/// document shape, so the numbers describe a correct transform, not a shortcut.
/// </summary>
[MemoryDiagnoser]
public class CosmosFusedEnvelopeBenchmarks
{
    public sealed record DocCase(string Name, int StringAttrs, int NumberAttrs, int PayloadBytes)
    {
        public override string ToString() => Name;
    }

    public static IEnumerable<DocCase> Docs =>
    [
        new("lean", StringAttrs: 0, NumberAttrs: 0, PayloadBytes: 0),
        new("payload_512", StringAttrs: 0, NumberAttrs: 0, PayloadBytes: 512),
        new("wide_20s_20n", StringAttrs: 20, NumberAttrs: 20, PayloadBytes: 0),
    ];

    [ParamsSource(nameof(Docs))]
    public DocCase Doc { get; set; } = null!;

    private byte[] _binaryBody = null!;

    [GlobalSetup]
    public void Setup()
    {
        string json = SyntheticCosmosDoc.Build(Doc.StringAttrs, Doc.NumberAttrs, Doc.PayloadBytes);
        _binaryBody = CosmosBinaryTestEncoder.Encode(json);

        byte[] twoPass = RunTwoPass(_binaryBody);
        byte[] fused = RunFused(_binaryBody);
        AssertIdentical(twoPass, fused);
    }

    [Benchmark(Baseline = true)]
    public int TwoPass()
    {
        using var json = new PooledByteBufferWriter(Math.Max(4096, _binaryBody.Length));
        CosmosBinaryDecoder.Decode(_binaryBody, json);

        using var scratch = new PooledByteBufferWriter(1024);
        using (var writer = new Utf8JsonWriter(scratch))
        {
            InferredAttributeStorage.WriteGetItemEnvelope(writer, json.WrittenMemory.Span);
            writer.Flush();
        }

        return scratch.WrittenMemory.Length;
    }

    [Benchmark]
    public int Fused()
    {
        using var scratch = new PooledByteBufferWriter(1024);
        using (var writer = new Utf8JsonWriter(scratch))
        {
            var reader = new CosmosBinaryReader(_binaryBody);
            try
            {
                InferredAttributeStorage.WriteGetItemEnvelope(writer, ref reader);
            }
            finally
            {
                reader.Dispose();
            }
            writer.Flush();
        }

        return scratch.WrittenMemory.Length;
    }

    private static byte[] RunTwoPass(ReadOnlySpan<byte> binary)
    {
        using var json = new PooledByteBufferWriter(Math.Max(4096, binary.Length));
        CosmosBinaryDecoder.Decode(binary, json);

        var scratch = new ArrayBufferWriter<byte>(1024);
        using (var writer = new Utf8JsonWriter(scratch))
        {
            InferredAttributeStorage.WriteGetItemEnvelope(writer, json.WrittenMemory.Span);
            writer.Flush();
        }

        return scratch.WrittenSpan.ToArray();
    }

    private static byte[] RunFused(ReadOnlySpan<byte> binary)
    {
        var scratch = new ArrayBufferWriter<byte>(1024);
        using (var writer = new Utf8JsonWriter(scratch))
        {
            var reader = new CosmosBinaryReader(binary);
            try
            {
                InferredAttributeStorage.WriteGetItemEnvelope(writer, ref reader);
            }
            finally
            {
                reader.Dispose();
            }
            writer.Flush();
        }

        return scratch.WrittenSpan.ToArray();
    }

    private void AssertIdentical(byte[] expected, byte[] actual)
    {
        if (!expected.AsSpan().SequenceEqual(actual))
        {
            throw new InvalidOperationException(
                $"correctness gate failed for '{Doc.Name}':\n" +
                $"  two-pass: {Encoding.UTF8.GetString(expected)}\n" +
                $"  fused: {Encoding.UTF8.GetString(actual)}");
        }
    }
}
