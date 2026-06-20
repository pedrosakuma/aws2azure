using Aws2Azure.Core.Buffers;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;
using Aws2Azure.Benchmarks.DynamoDb.Spike332;
using Aws2Azure.Modules.DynamoDb.Internal;
using Aws2Azure.Modules.DynamoDb.Operations;
using Aws2Azure.Modules.DynamoDb.Persistence;
using BenchmarkDotNet.Attributes;
using System.Text.Json;

namespace Aws2Azure.Benchmarks.DynamoDb;

/// <summary>
/// #345 — eliminate the <c>byte[] → string → byte[]</c> round-trips on the
/// Cosmos stored-procedure write paths (conditional PutItem / UpdateItem and
/// TransactWriteItems). Sproc parameters are inherently text JSON (CosmosBinary
/// does not apply to sproc input); this benchmark isolates the pure
/// materialization waste removed, not a format change.
///
/// <para><b>SingleWriteLegacy</b> reproduces the pre-#345 conditional-PutItem
/// body: <see cref="InferredAttributeStorage.BuildCosmosDocument"/> →
/// <c>string</c>, embed into <see cref="SprocManager.BuildParamsJson"/>, then
/// <c>Encoding.UTF8.GetBytes</c> (the <see cref="System.Net.Http.StringContent"/>
/// re-encode). <b>SingleWriteSinglePass</b> is the shipping path: the document
/// is encoded once into a pooled UTF-8 buffer and spliced into the parameter
/// list written straight into another pooled buffer, sent zero-copy.</para>
///
/// <para><b>TransactLegacy</b> models <c>"[" + BuildOperationsJson + "]"</c> then
/// the StringContent re-encode; <b>TransactSinglePass</b> assembles
/// <c>[[…ops…]]</c> in one <see cref="Utf8JsonWriter"/> pass over a pooled
/// buffer.</para>
///
/// <para>A <c>[GlobalSetup]</c> gate asserts each single-pass arm is
/// byte-identical to its legacy counterpart, so the deltas describe a
/// <i>correct</i> encoder. Emulator-independent CPU micro-benchmark (no Azure
/// round-trip).</para>
/// </summary>
[MemoryDiagnoser]
public class SprocParamsEncodeBenchmarks
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
    private const string ConditionAst = "{\"type\":\"ATTR_NOT_EXISTS\",\"attr\":\"id\"}";

    private JsonDocument _document = null!;
    private JsonElement _item;
    private byte[] _itemUtf8 = null!;
    private TransactWriteItemsHandler.PreparedOp[] _ops = null!;

    [GlobalSetup]
    public void Setup()
    {
        string json = SyntheticDdbItem.Build(Doc.StringAttrs, Doc.NumberAttrs, Doc.PayloadBytes);
        _document = JsonDocument.Parse(json);
        _item = _document.RootElement;
        _itemUtf8 = Encoding.UTF8.GetBytes(json);

        // Two Put ops + one conditional Delete — a representative transaction.
        _ops =
        [
            new TransactWriteItemsHandler.PreparedOp(
                TransactWriteItemsHandler.OpKind.Put, Id,
                ItemHandlers.BuildItemDocumentBytes(Id, Pk, _item), null),
            new TransactWriteItemsHandler.PreparedOp(
                TransactWriteItemsHandler.OpKind.Put, "route-0002",
                ItemHandlers.BuildItemDocumentBytes("route-0002", Pk, _item), ConditionAst),
            new TransactWriteItemsHandler.PreparedOp(
                TransactWriteItemsHandler.OpKind.Delete, "route-0003", null, ConditionAst),
        ];

        // Byte-identity gates: single-pass must equal legacy on both paths.
        byte[] swLegacy = SingleWriteLegacyBytes();
        byte[] swPooled = SingleWritePooledBytes();
        if (!swLegacy.AsSpan().SequenceEqual(swPooled))
        {
            throw new InvalidOperationException(
                "SprocParamsEncodeBenchmarks: single-write single-pass diverged from legacy.");
        }

        byte[] txLegacy = TransactLegacyBytes();
        byte[] txPooled = TransactPooledBytes();
        if (!txLegacy.AsSpan().SequenceEqual(txPooled))
        {
            throw new InvalidOperationException(
                "SprocParamsEncodeBenchmarks: transact single-pass diverged from legacy.");
        }
    }

    [GlobalCleanup]
    public void Cleanup() => _document.Dispose();

    private byte[] SingleWriteLegacyBytes()
    {
        string doc = InferredAttributeStorage.BuildCosmosDocument(Id, Pk, _item);
        string p = SprocManager.BuildParamsJson(SprocOperation.Put, Id, doc, ConditionAst, null);
        return Encoding.UTF8.GetBytes(p);
    }

    private byte[] SingleWritePooledBytes()
    {
        using var docBuf = ItemHandlers.ItemDocumentBody.CreateText(Id, Pk, _itemUtf8, _item);
        using var buf = new PooledByteBufferWriter(256);
        SprocManager.WriteSingleWriteParams(buf, SprocOperation.Put, Id, docBuf.Memory, ConditionAst, null);
        return buf.WrittenMemory.ToArray();
    }

    private byte[] TransactLegacyBytes()
    {
        string ops = TransactWriteItemsHandler.BuildOperationsJson(_ops);
        return Encoding.UTF8.GetBytes("[" + ops + "]");
    }

    private byte[] TransactPooledBytes()
    {
        using var buf = TransactWriteItemsHandler.BuildTransactParamsBody(_ops);
        return buf.WrittenMemory.ToArray();
    }

    [Benchmark(Baseline = true)]
    public int SingleWriteLegacy()
    {
        string doc = InferredAttributeStorage.BuildCosmosDocument(Id, Pk, _item);
        string p = SprocManager.BuildParamsJson(SprocOperation.Put, Id, doc, ConditionAst, null);
        return Encoding.UTF8.GetBytes(p).Length;
    }

    [Benchmark]
    public int SingleWriteSinglePass()
    {
        using var docBuf = ItemHandlers.ItemDocumentBody.CreateText(Id, Pk, _itemUtf8, _item);
        using var buf = new PooledByteBufferWriter(256);
        SprocManager.WriteSingleWriteParams(buf, SprocOperation.Put, Id, docBuf.Memory, ConditionAst, null);
        return buf.WrittenMemory.Length;
    }

    [Benchmark]
    public int TransactLegacy()
    {
        string ops = TransactWriteItemsHandler.BuildOperationsJson(_ops);
        return Encoding.UTF8.GetBytes("[" + ops + "]").Length;
    }

    [Benchmark]
    public int TransactSinglePass()
    {
        using var buf = TransactWriteItemsHandler.BuildTransactParamsBody(_ops);
        return buf.WrittenMemory.Length;
    }
}
