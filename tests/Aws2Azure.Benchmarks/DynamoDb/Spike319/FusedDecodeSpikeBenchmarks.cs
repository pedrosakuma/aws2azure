using Aws2Azure.Modules.DynamoDb.Internal;
using Aws2Azure.Modules.DynamoDb.Operations;
using Aws2Azure.Modules.DynamoDb.Persistence;
using BenchmarkDotNet.Attributes;
using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Aws2Azure.Benchmarks.DynamoDb.Spike319;

/// <summary>
/// SPIKE (#319) — measures the reader-abstraction design: a single generic
/// envelope transform (<see cref="GenericEnvelopeTransform"/>) driven by either
/// a JSON-text reader (<see cref="Utf8JsonTokenReader"/>) or a native
/// <see cref="CosmosBinaryReader"/>, vs the shipped two-pass binary path.
///
/// <list type="bullet">
///   <item><see cref="Production_TwoPass"/> — the real shipped path:
///   <c>CosmosBinaryDecoder.Decode</c> (binary → JSON text) then
///   <c>InferredAttributeStorage.WriteGetItemEnvelope</c> (JSON text → envelope).</item>
///   <item><see cref="Generic_TextReader_TwoPass"/> — control: decode to JSON
///   text (same as production), then the GENERIC transform over the
///   <c>Utf8JsonReader</c> adapter. <c>Production_TwoPass</c> vs this isolates
///   the cost of the abstraction itself on identical input.</item>
///   <item><see cref="Generic_BinaryReader_Fused"/> — the proposed ship path:
///   the SAME generic transform driven straight off <c>CosmosBinaryReader</c>,
///   no intermediate JSON text.</item>
/// </list>
///
/// A <c>[GlobalSetup]</c> assertion pins all three byte-identical over the
/// corpus, so the numbers describe a correct transform, not a shortcut.
/// </summary>
[MemoryDiagnoser]
public class FusedDecodeSpikeBenchmarks
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

        byte[] production = RunProductionTwoPass(_binaryBody);
        byte[] genericText = RunGenericTextReader(_binaryBody);
        byte[] genericBinary = RunGenericBinaryReader(_binaryBody);

        AssertIdentical("Generic_TextReader_TwoPass", production, genericText);
        AssertIdentical("Generic_BinaryReader_Fused", production, genericBinary);
    }

    [Benchmark(Baseline = true)]
    public int Production_TwoPass()
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
    public int Generic_TextReader_TwoPass()
    {
        using var json = new PooledByteBufferWriter(Math.Max(4096, _binaryBody.Length));
        CosmosBinaryDecoder.Decode(_binaryBody, json);

        using var scratch = new PooledByteBufferWriter(1024);
        using (var writer = new Utf8JsonWriter(scratch))
        {
            var reader = new Utf8JsonTokenReader(json.WrittenMemory.Span);
            GenericEnvelopeTransform.WriteGetItemEnvelope(writer, ref reader);
            writer.Flush();
        }

        return scratch.WrittenMemory.Length;
    }

    [Benchmark]
    public int Generic_BinaryReader_Fused()
    {
        using var scratch = new PooledByteBufferWriter(1024);
        using (var writer = new Utf8JsonWriter(scratch))
        {
            var reader = new CosmosBinaryReader(_binaryBody);
            GenericEnvelopeTransform.WriteGetItemEnvelope(writer, ref reader);
            writer.Flush();
        }

        return scratch.WrittenMemory.Length;
    }

    private static byte[] RunProductionTwoPass(ReadOnlySpan<byte> binary)
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

    private static byte[] RunGenericTextReader(ReadOnlySpan<byte> binary)
    {
        using var json = new PooledByteBufferWriter(Math.Max(4096, binary.Length));
        CosmosBinaryDecoder.Decode(binary, json);

        var scratch = new ArrayBufferWriter<byte>(1024);
        using (var writer = new Utf8JsonWriter(scratch))
        {
            var reader = new Utf8JsonTokenReader(json.WrittenMemory.Span);
            GenericEnvelopeTransform.WriteGetItemEnvelope(writer, ref reader);
            writer.Flush();
        }

        return scratch.WrittenSpan.ToArray();
    }

    private static byte[] RunGenericBinaryReader(ReadOnlySpan<byte> binary)
    {
        var scratch = new ArrayBufferWriter<byte>(1024);
        using (var writer = new Utf8JsonWriter(scratch))
        {
            var reader = new CosmosBinaryReader(binary);
            GenericEnvelopeTransform.WriteGetItemEnvelope(writer, ref reader);
            writer.Flush();
        }

        return scratch.WrittenSpan.ToArray();
    }

    private void AssertIdentical(string arm, byte[] expected, byte[] actual)
    {
        if (!expected.AsSpan().SequenceEqual(actual))
        {
            throw new InvalidOperationException(
                $"spike correctness gate failed for '{Doc.Name}' / {arm}:\n" +
                $"  production: {Encoding.UTF8.GetString(expected)}\n" +
                $"  {arm}: {Encoding.UTF8.GetString(actual)}");
        }
    }
}
