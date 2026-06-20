using Aws2Azure.Core.Buffers;
using System.Text;
using System.Text.Json;
using Aws2Azure.Modules.DynamoDb.Internal;
using Aws2Azure.Modules.DynamoDb.Operations;
using BenchmarkDotNet.Attributes;

namespace Aws2Azure.Benchmarks.DynamoDb;

/// <summary>
/// Quantifies the CPU + allocation cost of the two DynamoDB→Cosmos response
/// decode models (issue #317, follow-up to #268):
/// <list type="bullet">
///   <item><b>Text model</b> (<see cref="TextParse"/>, baseline) — Cosmos returns
///   text JSON; the proxy parses it for the response transform.</item>
///   <item><b>CosmosBinary model</b> (<see cref="BinaryDecodeThenParse"/>) — Cosmos
///   returns a <c>0x80</c> binary body; the proxy decodes it to JSON
///   (<see cref="CosmosBinaryDecoder"/>) and then parses it. This is the full
///   work the proxy does on the binary path.</item>
///   <item><see cref="BinaryDecodeOnly"/> isolates just the extra decode step so
///   the pure binary→JSON overhead is visible on its own.</item>
/// </list>
/// CosmosBinary trades this extra decode CPU for a smaller wire payload from
/// Cosmos; the wire-size delta is reported separately by the program entry point.
/// </summary>
[MemoryDiagnoser]
public class CosmosBinaryDecodeBenchmarks
{
    /// <summary>A named synthetic Cosmos query-page shape.</summary>
    public sealed record PageCase(string Name, int DocumentCount, int PayloadBytes, int ExtraAttributes = 0)
    {
        public override string ToString() => Name;
    }

    public static IEnumerable<PageCase> Pages =>
    [
        new("small_10x128", DocumentCount: 10, PayloadBytes: 128),
        new("medium_50x512", DocumentCount: 50, PayloadBytes: 512),
        new("large_200x1024", DocumentCount: 200, PayloadBytes: 1024),
        new("wide_50x24attrs", DocumentCount: 50, PayloadBytes: 0, ExtraAttributes: 24),
    ];

    [ParamsSource(nameof(Pages))]
    public PageCase Page { get; set; } = null!;

    private byte[] _textBody = null!;
    private byte[] _binaryBody = null!;
    private int _initialCapacity;

    [GlobalSetup]
    public void Setup()
    {
        string json = SyntheticCosmosPage.Build(Page.DocumentCount, Page.PayloadBytes, Page.ExtraAttributes);
        _textBody = Encoding.UTF8.GetBytes(json);
        _binaryBody = CosmosBinaryTestEncoder.Encode(json);

        // Mirror the production read path's output buffer sizing
        // (CosmosOpsShared.ReadCosmosJsonBodyAsync).
        _initialCapacity = Math.Max(4096, _textBody.Length);
    }

    [Benchmark(Baseline = true)]
    public int TextParse()
    {
        using var doc = JsonDocument.Parse(_textBody);
        return doc.RootElement.GetProperty("Documents").GetArrayLength();
    }

    [Benchmark]
    public int BinaryDecodeOnly()
    {
        using var decoded = new PooledByteBufferWriter(_initialCapacity);
        CosmosBinaryDecoder.Decode(_binaryBody, decoded);
        return decoded.WrittenMemory.Length;
    }

    [Benchmark]
    public int BinaryDecodeThenParse()
    {
        using var decoded = new PooledByteBufferWriter(_initialCapacity);
        CosmosBinaryDecoder.Decode(_binaryBody, decoded);
        using var doc = JsonDocument.Parse(decoded.WrittenMemory);
        return doc.RootElement.GetProperty("Documents").GetArrayLength();
    }
}
