using Aws2Azure.Core.Buffers;
using Aws2Azure.Modules.DynamoDb.Internal;
using Aws2Azure.Modules.DynamoDb.Operations;
using Aws2Azure.Modules.DynamoDb.Persistence;
using BenchmarkDotNet.Attributes;
using System.Buffers;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace Aws2Azure.Benchmarks.DynamoDb;

/// <summary>
/// Measures the multi-item Query/Scan fused transform (the no-filter /
/// no-projection fast path) against the legacy materialized path, over an
/// already-decoded Cosmos query page (JSON text — both arms start from the same
/// decoded bytes, so this isolates the transform, not the CosmosBinary decode).
///
/// <list type="bullet">
///   <item><see cref="Materialized"/> — the legacy path: parse the page into a
///   <c>JsonDocument</c> DOM, <c>ExtractItem</c> each document into a
///   <c>Dictionary&lt;string, JsonElement&gt;</c> AttributeValue map, collect
///   into a <see cref="ScanResponse"/>, then re-serialize.</item>
///   <item><see cref="Fused"/> — the shipped fast path: stream the page's
///   <c>Documents</c> straight into the response envelope via
///   <c>InferredAttributeStorage.WriteTransformedDocuments</c>, with no DOM and
///   no per-item map.</item>
/// </list>
///
/// A <c>[GlobalSetup]</c> assertion pins both arms byte-identical, so the
/// numbers describe a correct transform, not a shortcut.
/// </summary>
[MemoryDiagnoser]
public class CosmosMultiItemTransformBenchmarks
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

    private byte[] _pageBytes = null!;

    [GlobalSetup]
    public void Setup()
    {
        string json = SyntheticCosmosPage.Build(Page.DocumentCount, Page.PayloadBytes, Page.ExtraAttributes);
        _pageBytes = Encoding.UTF8.GetBytes(json);

        byte[] materialized = RunMaterialized(_pageBytes);
        byte[] fused = RunFused(_pageBytes);
        AssertIdentical(materialized, fused);
    }

    [Benchmark(Baseline = true)]
    public int Materialized()
    {
        byte[] bytes = RunMaterialized(_pageBytes);
        return bytes.Length;
    }

    [Benchmark]
    public int Fused()
    {
        using var scratch = new PooledByteBufferWriter(4096);
        BuildFused(_pageBytes, scratch);
        return scratch.WrittenMemory.Length;
    }

    private static byte[] RunMaterialized(ReadOnlySpan<byte> pageBytes)
    {
        using var doc = JsonDocument.Parse(pageBytes.ToArray());
        var items = new List<Dictionary<string, JsonElement>>();
        if (doc.RootElement.TryGetProperty("Documents", out var docsEl)
            && docsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var docEl in docsEl.EnumerateArray())
            {
                var map = InferredAttributeStorage.ExtractItem(docEl);
                if (map is not null)
                {
                    items.Add(map);
                }
            }
        }

        var response = new ScanResponse
        {
            Items = items,
            Count = items.Count,
            ScannedCount = items.Count,
        };
        return JsonSerializer.SerializeToUtf8Bytes(response, ScanJsonContext.Default.ScanResponse);
    }

    private static byte[] RunFused(ReadOnlySpan<byte> pageBytes)
    {
        var scratch = new ArrayBufferWriter<byte>(4096);
        using var writer = new Utf8JsonWriter(scratch);
        WriteFusedEnvelope(pageBytes, writer);
        return scratch.WrittenSpan.ToArray();
    }

    private static void BuildFused(ReadOnlySpan<byte> pageBytes, PooledByteBufferWriter scratch)
    {
        using var writer = new Utf8JsonWriter(scratch);
        WriteFusedEnvelope(pageBytes, writer);
    }

    private static void WriteFusedEnvelope(ReadOnlySpan<byte> pageBytes, Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("Items"u8);
        writer.WriteStartArray();
        var reader = new Utf8JsonTokenReader(pageBytes);
        int count = InferredAttributeStorage.WriteTransformedDocuments(writer, ref reader);
        writer.WriteEndArray();
        writer.WriteNumber("Count"u8, count);
        writer.WriteNumber("ScannedCount"u8, count);
        writer.WriteEndObject();
        writer.Flush();
    }

    private void AssertIdentical(byte[] expected, byte[] actual)
    {
        if (!expected.AsSpan().SequenceEqual(actual))
        {
            throw new InvalidOperationException(
                $"correctness gate failed for '{Page.Name}':\n" +
                $"  materialized: {Encoding.UTF8.GetString(expected)}\n" +
                $"  fused: {Encoding.UTF8.GetString(actual)}");
        }
    }
}
