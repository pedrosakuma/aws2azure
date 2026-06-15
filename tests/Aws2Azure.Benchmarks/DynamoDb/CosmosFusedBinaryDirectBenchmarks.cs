using System.Buffers;
using System.Text;
using System.Text.Json;
using Aws2Azure.Modules.DynamoDb.Internal;
using Aws2Azure.Modules.DynamoDb.Operations;
using Aws2Azure.Modules.DynamoDb.Persistence;
using BenchmarkDotNet.Attributes;

namespace Aws2Azure.Benchmarks.DynamoDb;

/// <summary>
/// Records issue #327's hard case: the no-filter / no-projection fused multi-item
/// Query/Scan path streams a CosmosBinary page straight off a
/// <c>CosmosBinaryReader</c> instead of decoding it to text first.
///
/// Both arms start from the SAME CosmosBinary (<c>0x80</c>) page — the realistic
/// input once <c>DynamoDb.CosmosBinaryResponses</c> is negotiated — and produce
/// the byte-identical DynamoDB <c>Items</c> envelope. A <c>[GlobalSetup]</c> gate
/// pins both arms identical, so the numbers describe a correct transform.
///
/// <list type="bullet">
///   <item><see cref="DecodeToTextFused"/> (baseline) — the pre-#327 fused path:
///   decode the whole page binary→UTF-8 JSON, then stream it through
///   <c>WriteTransformedDocuments</c> with a text <c>Utf8JsonTokenReader</c>.</item>
///   <item><see cref="BinaryDirectStreaming"/> — the shipped path: pump the binary
///   page straight through <c>WriteTransformedDocuments</c> with a
///   <c>CosmosBinaryReader</c> into the response writer, no decode-to-text and no
///   fallback. A reader decline propagates as an error (exactly like a malformed
///   text page), which is why the fused path needs no per-page rollback.</item>
/// </list>
///
/// Earlier revisions also measured per-page scratch designs (splice / validate /
/// raw-append) that preserved a decode-to-text fallback by buffering each page
/// before splicing it into the shared cross-page writer. They matched the
/// streaming ceiling only by paying a re-parse/copy tax, so the fused path drops
/// the fallback entirely and streams directly — see PR description for the data.
///
/// Numbers are host-bound (AMD EPYC 7763, .NET 10) and only describe the binary
/// fast path, which engages with opt-in <c>DynamoDb.CosmosBinaryResponses=true</c>.
/// </summary>
[MemoryDiagnoser]
public class CosmosFusedBinaryDirectBenchmarks
{
    public sealed record PageCase(string Name, int DocumentCount, int PayloadBytes, int ExtraAttributes)
    {
        public override string ToString() => Name;
    }

    public static IEnumerable<PageCase> Pages =>
    [
        new("lean_100", DocumentCount: 100, PayloadBytes: 0, ExtraAttributes: 0),
        new("wide_100_20attrs", DocumentCount: 100, PayloadBytes: 0, ExtraAttributes: 20),
        new("payload_100_512b", DocumentCount: 100, PayloadBytes: 512, ExtraAttributes: 0),
        new("lean_1000", DocumentCount: 1000, PayloadBytes: 0, ExtraAttributes: 0),
    ];

    [ParamsSource(nameof(Pages))]
    public PageCase Page { get; set; } = null!;

    private byte[] _binaryBody = null!;
    private int _initialCapacity;

    [GlobalSetup]
    public void Setup()
    {
        string json = SyntheticCosmosPage.Build(Page.DocumentCount, Page.PayloadBytes, Page.ExtraAttributes);
        byte[] textBody = Encoding.UTF8.GetBytes(json);
        _binaryBody = CosmosBinaryTestEncoder.Encode(json);
        // Mirror CosmosOpsShared.ReadCosmosRawBodyAsync output buffer sizing.
        _initialCapacity = Math.Max(4096, textBody.Length);

        byte[] baseline = RunDecodeToTextFused(_binaryBody, _initialCapacity);
        byte[] streaming = RunBinaryDirectStreaming(_binaryBody);
        AssertIdentical(baseline, streaming, "BinaryDirectStreaming");
    }

    [Benchmark(Baseline = true)]
    public int DecodeToTextFused()
    {
        using var decoded = new PooledByteBufferWriter(_initialCapacity);
        CosmosBinaryDecoder.Decode(_binaryBody, decoded);
        using var outBuf = new PooledByteBufferWriter(4096);
        using var writer = new Utf8JsonWriter(outBuf);
        OpenEnvelope(writer);
        var reader = new Utf8JsonTokenReader(decoded.WrittenMemory.Span);
        int count = InferredAttributeStorage.WriteTransformedDocuments(writer, ref reader);
        CloseEnvelope(writer, count);
        return outBuf.WrittenMemory.Length;
    }

    [Benchmark]
    public int BinaryDirectStreaming()
    {
        using var outBuf = new PooledByteBufferWriter(4096);
        using var writer = new Utf8JsonWriter(outBuf);
        OpenEnvelope(writer);
        var reader = new CosmosBinaryReader(_binaryBody);
        int count;
        try { count = InferredAttributeStorage.WriteTransformedDocuments(writer, ref reader); }
        finally { reader.Dispose(); }
        CloseEnvelope(writer, count);
        return outBuf.WrittenMemory.Length;
    }

    // ---- shared helpers --------------------------------------------------

    private static byte[] RunDecodeToTextFused(ReadOnlySpan<byte> binary, int capacity)
    {
        using var decoded = new PooledByteBufferWriter(capacity);
        CosmosBinaryDecoder.Decode(binary, decoded);
        var scratch = new ArrayBufferWriter<byte>(4096);
        using var writer = new Utf8JsonWriter(scratch);
        OpenEnvelope(writer);
        var reader = new Utf8JsonTokenReader(decoded.WrittenMemory.Span);
        int count = InferredAttributeStorage.WriteTransformedDocuments(writer, ref reader);
        CloseEnvelope(writer, count);
        return scratch.WrittenSpan.ToArray();
    }

    private static byte[] RunBinaryDirectStreaming(ReadOnlySpan<byte> binary)
    {
        var scratch = new ArrayBufferWriter<byte>(4096);
        using var writer = new Utf8JsonWriter(scratch);
        OpenEnvelope(writer);
        var reader = new CosmosBinaryReader(binary);
        int count;
        try { count = InferredAttributeStorage.WriteTransformedDocuments(writer, ref reader); }
        finally { reader.Dispose(); }
        CloseEnvelope(writer, count);
        return scratch.WrittenSpan.ToArray();
    }

    private static void OpenEnvelope(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("Items"u8);
        writer.WriteStartArray();
    }

    private static void CloseEnvelope(Utf8JsonWriter writer, int count)
    {
        writer.WriteEndArray();
        writer.WriteNumber("Count"u8, count);
        writer.WriteNumber("ScannedCount"u8, count);
        writer.WriteEndObject();
        writer.Flush();
    }

    private void AssertIdentical(byte[] expected, byte[] actual, string arm)
    {
        if (!expected.AsSpan().SequenceEqual(actual))
        {
            throw new InvalidOperationException(
                $"correctness gate failed for '{Page.Name}' arm '{arm}':\n" +
                $"  baseline: {Encoding.UTF8.GetString(expected)}\n" +
                $"  {arm}: {Encoding.UTF8.GetString(actual)}");
        }
    }
}
