using Aws2Azure.Modules.DynamoDb.Internal;
using Aws2Azure.Modules.DynamoDb.Operations;
using Aws2Azure.Modules.DynamoDb.Persistence;
using BenchmarkDotNet.Attributes;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace Aws2Azure.Benchmarks.DynamoDb;

/// <summary>
/// Measures the <c>BatchGetItem</c> grouped-query (no-<c>ProjectionExpression</c>)
/// response assembly against an already-decoded Cosmos query page, isolating the
/// per-document materialization the response path pays — not the Cosmos decode.
///
/// <list type="bullet">
///   <item><see cref="Materialized"/> — the legacy path (issue #443 baseline):
///   <c>ExtractItemsFusedWithId</c> re-parses each transformed document back into
///   a <c>Dictionary&lt;string, JsonElement&gt;</c> map (a <c>JsonDocument.Parse</c>
///   plus per-attribute <c>JsonElement.Clone</c>), wraps each in a
///   <see cref="BatchGetResponseItem"/>, then serializes the
///   <see cref="BatchGetItemResponse"/>.</item>
///   <item><see cref="Bytes"/> — the shipped path:
///   <c>ExtractItemsFusedWithIdBytes</c> keeps the transformed item bytes and
///   splices them verbatim into <c>Responses</c> (no DOM, no map, no clones),
///   then serializes the same response.</item>
/// </list>
///
/// A <c>[GlobalSetup]</c> assertion pins both arms byte-identical, so the numbers
/// describe a correct transform, not a shortcut. This is a <b>GC / footprint</b>
/// measurement (allocation per response) — the BatchGetItem read path is IO-bound,
/// so the win is reduced managed allocation, not throughput.
/// </summary>
[MemoryDiagnoser]
public class BatchGetItemResponseBenchmarks
{
    private const string TableName = "orders";

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
        byte[] bytes = RunBytes(_pageBytes);
        AssertIdentical(materialized, bytes);
    }

    [Benchmark(Baseline = true)]
    public int Materialized()
    {
        byte[] bytes = RunMaterialized(_pageBytes);
        return bytes.Length;
    }

    [Benchmark]
    public int Bytes()
    {
        byte[] bytes = RunBytes(_pageBytes);
        return bytes.Length;
    }

    private static byte[] RunMaterialized(ReadOnlySpan<byte> pageBytes)
    {
        var items = new List<BatchGetResponseItem>();
        var reader = new Utf8JsonTokenReader(pageBytes);
        InferredAttributeStorage.ExtractItemsFusedWithId(ref reader, new MapSink(items));
        return Serialize(items);
    }

    private static byte[] RunBytes(ReadOnlySpan<byte> pageBytes)
    {
        var items = new List<BatchGetResponseItem>();
        var reader = new Utf8JsonTokenReader(pageBytes);
        InferredAttributeStorage.ExtractItemsFusedWithIdBytes(ref reader, new BytesSink(items));
        return Serialize(items);
    }

    private static byte[] Serialize(List<BatchGetResponseItem> items)
    {
        var response = new BatchGetItemResponse
        {
            Responses = new Dictionary<string, List<BatchGetResponseItem>> { [TableName] = items },
        };
        return JsonSerializer.SerializeToUtf8Bytes(response, BatchGetItemJsonContext.Default.BatchGetItemResponse);
    }

    private readonly struct MapSink(List<BatchGetResponseItem> items) : IFusedItemWithIdSink
    {
        public void Accept(string? id, Dictionary<string, JsonElement> map)
            => items.Add(new BatchGetResponseItem(map));
    }

    private readonly struct BytesSink(List<BatchGetResponseItem> items) : IFusedItemBytesWithIdSink
    {
        public void Accept(string? id, ReadOnlySpan<byte> itemBytes)
            => items.Add(new BatchGetResponseItem(itemBytes.ToArray()));
    }

    private void AssertIdentical(byte[] expected, byte[] actual)
    {
        if (!expected.AsSpan().SequenceEqual(actual))
        {
            throw new InvalidOperationException(
                $"correctness gate failed for '{Page.Name}':\n" +
                $"  materialized: {Encoding.UTF8.GetString(expected)}\n" +
                $"  bytes: {Encoding.UTF8.GetString(actual)}");
        }
    }
}
