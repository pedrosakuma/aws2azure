using System;
using System.Buffers;
using System.Text;
using System.Text.Json;
using Aws2Azure.Modules.DynamoDb.Internal;
using Aws2Azure.Modules.DynamoDb.Persistence;
using BenchmarkDotNet.Attributes;

namespace Aws2Azure.Benchmarks.DynamoDb.Spike332;

/// <summary>
/// Step 2 of the #332 GO — the <b>production</b> DDB→Cosmos write encoders.
/// Both arms call the shipping entry points
/// (<see cref="InferredAttributeStorage.WriteCosmosDocument"/> text vs
/// <see cref="InferredAttributeStorage.WriteCosmosDocumentBinary"/> CosmosBinary)
/// over the same parsed DynamoDB item, so this measures the real format cost
/// the proxy pays per write — escaping + ASCII number formatting (text) versus
/// fixed-width binary markers (the #332 candidate), each driven through the
/// shared <see cref="ITokenWriter"/> token walk.
///
/// <para>The companion <see cref="CosmosEncodeBenchmarks"/> isolates the pure
/// formatter over a pre-materialized token tree (spike §2, ~2.4×); this one
/// keeps the production string-materialization tax both arms share, so its
/// delta is the honest end-to-end shipping number.</para>
///
/// <para>A <c>[GlobalSetup]</c> gate round-trips the binary output through the
/// production <see cref="CosmosBinaryDecoder"/> and asserts it matches the text
/// output (re-rendered with the decoder's default JSON encoder), so the numbers
/// describe a <i>correct</i> encoder.</para>
/// </summary>
[MemoryDiagnoser]
public class CosmosWriteEncodeBenchmarks
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

    private const string Id = "route-0001";
    private const string Pk = "p00";

    private JsonDocument _document = null!;
    private JsonElement _item;
    private byte[] _itemUtf8 = null!;
    private ArrayBufferWriter<byte> _buffer = null!;

    [GlobalSetup]
    public void Setup()
    {
        string json = SyntheticDdbItem.Build(Doc.StringAttrs, Doc.NumberAttrs, Doc.PayloadBytes);
        _document = JsonDocument.Parse(json);
        _item = _document.RootElement;
        _itemUtf8 = Encoding.UTF8.GetBytes(json);
        _buffer = new ArrayBufferWriter<byte>(Math.Max(1024, json.Length * 2));

        // Correctness gate: decode(binary) must equal the text output rendered
        // through the decoder's default JSON encoder.
        byte[] text = RenderTextDefaultEncoder();
        byte[] binary = RenderBinary();
        var decoded = new ArrayBufferWriter<byte>(text.Length + 16);
        CosmosBinaryDecoder.Decode(binary, decoded);
        if (!decoded.WrittenSpan.SequenceEqual(text))
        {
            throw new InvalidOperationException(
                $"binary encode round-trip diverged from text for '{Doc.Name}':\n" +
                $"  text:    {Encoding.UTF8.GetString(text)}\n" +
                $"  decoded: {Encoding.UTF8.GetString(decoded.WrittenSpan)}");
        }

        // Single-pass gate (#342): the wire (Utf8JsonReader) encoders must be
        // byte-identical to the JsonElement encoders they replace, both arms.
        AssertWireMatchesJsonElement(textArm: true);
        AssertWireMatchesJsonElement(textArm: false);
    }

    private void AssertWireMatchesJsonElement(bool textArm)
    {
        var fromElement = new ArrayBufferWriter<byte>(1024);
        var fromWire = new ArrayBufferWriter<byte>(1024);
        if (textArm)
        {
            InferredAttributeStorage.WriteCosmosDocument(fromElement, Id, Pk, _item);
            InferredAttributeStorage.WriteCosmosDocument(fromWire, Id, Pk, _itemUtf8);
        }
        else
        {
            InferredAttributeStorage.WriteCosmosDocumentBinary(fromElement, Id, Pk, _item);
            InferredAttributeStorage.WriteCosmosDocumentBinary(fromWire, Id, Pk, _itemUtf8);
        }

        if (!fromWire.WrittenSpan.SequenceEqual(fromElement.WrittenSpan))
        {
            throw new InvalidOperationException(
                $"wire encode diverged from JsonElement encode for '{Doc.Name}' ({(textArm ? "text" : "binary")}).");
        }
    }

    [GlobalCleanup]
    public void Cleanup() => _document.Dispose();

    [Benchmark(Baseline = true)]
    public int Text()
    {
        _buffer.Clear();
        InferredAttributeStorage.WriteCosmosDocument(_buffer, Id, Pk, _item);
        return _buffer.WrittenCount;
    }

    [Benchmark]
    public int Binary()
    {
        _buffer.Clear();
        InferredAttributeStorage.WriteCosmosDocumentBinary(_buffer, Id, Pk, _item);
        return _buffer.WrittenCount;
    }

    /// <summary>
    /// #342 single-pass: encode the JSON-text body straight from the raw item
    /// UTF-8 wire bytes via <see cref="System.Text.Json.Utf8JsonReader"/> — no
    /// <see cref="JsonElement"/> traversal, no per-attribute <c>GetString()</c>.
    /// Delta vs <see cref="Text"/> is the honest end-to-end single-pass gain.
    /// </summary>
    [Benchmark]
    public int TextWire()
    {
        _buffer.Clear();
        InferredAttributeStorage.WriteCosmosDocument(_buffer, Id, Pk, _itemUtf8);
        return _buffer.WrittenCount;
    }

    /// <summary>
    /// #342 single-pass: encode the CosmosBinary body straight from the raw item
    /// UTF-8 wire bytes. Delta vs <see cref="Binary"/> is the single-pass gain.
    /// </summary>
    [Benchmark]
    public int BinaryWire()
    {
        _buffer.Clear();
        InferredAttributeStorage.WriteCosmosDocumentBinary(_buffer, Id, Pk, _itemUtf8);
        return _buffer.WrittenCount;
    }

    private byte[] RenderBinary()
    {
        var abw = new ArrayBufferWriter<byte>(1024);
        InferredAttributeStorage.WriteCosmosDocumentBinary(abw, Id, Pk, _item);
        return abw.WrittenSpan.ToArray();
    }

    private byte[] RenderTextDefaultEncoder()
    {
        string relaxed = InferredAttributeStorage.BuildCosmosDocument(Id, Pk, _item);
        using var doc = JsonDocument.Parse(relaxed);
        var abw = new ArrayBufferWriter<byte>(1024);
        using (var writer = new Utf8JsonWriter(abw))
        {
            doc.RootElement.WriteTo(writer);
        }

        return abw.WrittenSpan.ToArray();
    }
}
