using System.Buffers;
using System.Text;
using System.Text.Json;
using Aws2Azure.Benchmarks.DynamoDb.Spike319;
using Aws2Azure.Modules.DynamoDb.Internal;
using BenchmarkDotNet.Attributes;

namespace Aws2Azure.Benchmarks.DynamoDb.Spike332;

/// <summary>
/// Spike #332 — does encoding the Cosmos write body as <b>CosmosBinary</b> beat
/// the current JSON-<b>text</b> formatter? Unlike the decode direction there is
/// no redundant pass to remove (the production write path already builds the
/// Cosmos document in one <c>Utf8JsonWriter</c> pass), so this isolates the pure
/// <i>formatter</i> cost.
///
/// Both encoders walk the <b>same pre-materialized <see cref="Node"/> tree</b>
/// (names + string values decoded to UTF-8 once in setup), so the hot path is
/// zero-allocation and the only thing measured is the format difference —
/// escaping + number formatting + structural framing. This deliberately removes
/// the <c>GetString()</c> materialization tax that, in the first cut of this
/// benchmark, dominated both arms and masked the real text-vs-binary delta.
///
/// <list type="bullet">
///   <item><see cref="Text_TokenWalk"/> — <c>Utf8JsonWriter</c> from the token
///   tree (optimal zero-materialization text).</item>
///   <item><see cref="Binary_TokenWalk"/> — the backpatching
///   <see cref="BinaryTokenEncoder"/> from the same tree (the #332
///   candidate).</item>
///   <item><see cref="Text_TokenWalk_ToString"/> — text walk plus the
///   <c>Encoding.UTF8.GetString</c> the production <c>BuildCosmosDocument</c>
///   tail pays before <c>StringContent</c> (the bytes→string→bytes round-trip;
///   encode spike §5).</item>
///   <item><see cref="Text_WriteTo"/> — <c>JsonElement.WriteTo</c> reference (a
///   different, JsonElement-driven path; context only).</item>
/// </list>
///
/// A <c>[GlobalSetup]</c> gate round-trips the binary output through the
/// production <c>CosmosBinaryDecoder</c> and asserts it equals the text output,
/// so the numbers describe a <i>correct</i> encoder.
/// </summary>
[MemoryDiagnoser]
public class CosmosEncodeBenchmarks
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

    private Node _tree = null!;
    private JsonDocument _doc = null!;
    private JsonElement _root;

    private ArrayBufferWriter<byte> _textBuffer = null!;
    private Utf8JsonWriter _textWriter = null!;
    private BinaryTokenEncoder _binary = null!;

    [GlobalSetup]
    public void Setup()
    {
        string json = SyntheticCosmosDoc.Build(Doc.StringAttrs, Doc.NumberAttrs, Doc.PayloadBytes);
        _tree = Node.FromJson(json);
        _doc = JsonDocument.Parse(json);
        _root = _doc.RootElement;

        _textBuffer = new ArrayBufferWriter<byte>(Math.Max(1024, json.Length * 2));
        _textWriter = new Utf8JsonWriter(_textBuffer);
        _binary = new BinaryTokenEncoder(Math.Max(1024, json.Length * 2));

        // Correctness gate: binary output must decode back to the text output.
        byte[] text = RunText();
        _binary.Encode(_tree);
        var decoded = new ArrayBufferWriter<byte>(text.Length + 16);
        CosmosBinaryDecoder.Decode(_binary.Written, decoded);
        if (!decoded.WrittenSpan.SequenceEqual(text))
        {
            throw new InvalidOperationException(
                $"binary encode round-trip diverged from text for '{Doc.Name}':\n" +
                $"  text:    {Encoding.UTF8.GetString(text)}\n" +
                $"  decoded: {Encoding.UTF8.GetString(decoded.WrittenSpan)}");
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _textWriter.Dispose();
        _doc.Dispose();
    }

    [Benchmark(Baseline = true)]
    public int Text_TokenWalk()
    {
        _textBuffer.Clear();
        _textWriter.Reset();
        TextTokenEncoder.Write(_textWriter, _tree);
        _textWriter.Flush();
        return _textBuffer.WrittenCount;
    }

    [Benchmark]
    public int Binary_TokenWalk()
    {
        _binary.Encode(_tree);
        return _binary.Written.Length;
    }

    [Benchmark]
    public int Text_TokenWalk_ToString()
    {
        _textBuffer.Clear();
        _textWriter.Reset();
        TextTokenEncoder.Write(_textWriter, _tree);
        _textWriter.Flush();
        return Encoding.UTF8.GetString(_textBuffer.WrittenSpan).Length;
    }

    [Benchmark]
    public int Text_WriteTo()
    {
        _textBuffer.Clear();
        _textWriter.Reset();
        _root.WriteTo(_textWriter);
        _textWriter.Flush();
        return _textBuffer.WrittenCount;
    }

    private byte[] RunText()
    {
        var abw = new ArrayBufferWriter<byte>(1024);
        using var w = new Utf8JsonWriter(abw);
        TextTokenEncoder.Write(w, _tree);
        w.Flush();
        return abw.WrittenSpan.ToArray();
    }
}
