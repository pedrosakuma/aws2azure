using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using Aws2Azure.Modules.DynamoDb.Internal;
using Aws2Azure.Modules.DynamoDb.Operations;
using Aws2Azure.Modules.DynamoDb.Persistence;
using Xunit;

namespace Aws2Azure.UnitTests.DynamoDb.Persistence;

/// <summary>
/// Guards the #482 producer: the write path stamps an order-preserving,
/// digits-only <c>_a2a$ord$&lt;attr&gt;</c> field for every Number-typed GSI
/// sort attribute present in the item, so a Cosmos <c>ORDER BY</c> on that field
/// sorts high-precision (<c>{"_a2a:N":…}</c> envelope) values numerically. The
/// field must (a) only appear for N-typed GSI sort keys, (b) preserve numeric
/// order lexically across the bare/envelope storage boundary, (c) be produced by
/// both the JSON-text and CosmosBinary encoders, and (d) stay invisible to
/// callers on read (its <c>_a2a</c> prefix is auto-skipped).
/// </summary>
public class SecondaryIndexOrderKeyTests
{
    private const string OrderProp = "_a2a$ord$";

    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    /// <summary>Table with a composite GSI whose RANGE key <c>gsk</c> is Number.</summary>
    private static TableMetadata NumericGsiMeta(string sortAttr = "gsk") => new()
    {
        TableName = "t",
        AttributeDefinitions = new List<TableAttributeDefinition>
        {
            new() { Name = "pk", Type = "S" },
            new() { Name = "ghk", Type = "S" },
            new() { Name = sortAttr, Type = "N" },
        },
        KeySchema = new List<TableKeySchemaElement>
        {
            new() { Name = "pk", KeyType = "HASH" },
        },
        GlobalSecondaryIndexes = new List<TableIndexDefinition>
        {
            new()
            {
                IndexName = "gsi1",
                KeySchema = new List<TableKeySchemaElement>
                {
                    new() { Name = "ghk", KeyType = "HASH" },
                    new() { Name = sortAttr, KeyType = "RANGE" },
                },
            },
        },
    };

    /// <summary>Table with a composite GSI whose RANGE key is a String.</summary>
    private static TableMetadata StringGsiMeta() => new()
    {
        TableName = "t",
        AttributeDefinitions = new List<TableAttributeDefinition>
        {
            new() { Name = "pk", Type = "S" },
            new() { Name = "ghk", Type = "S" },
            new() { Name = "gsk", Type = "S" },
        },
        KeySchema = new List<TableKeySchemaElement>
        {
            new() { Name = "pk", KeyType = "HASH" },
        },
        GlobalSecondaryIndexes = new List<TableIndexDefinition>
        {
            new()
            {
                IndexName = "gsi1",
                KeySchema = new List<TableKeySchemaElement>
                {
                    new() { Name = "ghk", KeyType = "HASH" },
                    new() { Name = "gsk", KeyType = "RANGE" },
                },
            },
        },
    };

    /// <summary>Table with a composite LSI whose RANGE key <c>lsk</c> is Number.</summary>
    private static TableMetadata NumericLsiMeta(string sortAttr = "lsk") => new()
    {
        TableName = "t",
        AttributeDefinitions = new List<TableAttributeDefinition>
        {
            new() { Name = "pk", Type = "S" },
            new() { Name = "sk", Type = "S" },
            new() { Name = sortAttr, Type = "N" },
        },
        KeySchema = new List<TableKeySchemaElement>
        {
            new() { Name = "pk", KeyType = "HASH" },
            new() { Name = "sk", KeyType = "RANGE" },
        },
        LocalSecondaryIndexes = new List<TableIndexDefinition>
        {
            new()
            {
                IndexName = "lsi1",
                KeySchema = new List<TableKeySchemaElement>
                {
                    new() { Name = "pk", KeyType = "HASH" },
                    new() { Name = sortAttr, KeyType = "RANGE" },
                },
            },
        },
    };

    private static string WriteText(TableMetadata meta, string itemJson)
    {
        var item = Parse(itemJson);
        var orderKeys = SecondaryIndexOrderKeys.Compute(meta, item);
        var bw = new ArrayBufferWriter<byte>();
        InferredAttributeStorage.WriteCosmosDocument(bw, "id", "pk", item, null, orderKeys);
        return Encoding.UTF8.GetString(bw.WrittenSpan);
    }

    [Fact]
    public void Emits_order_key_for_numeric_lsi_sort_attribute()
    {
        // #504: the encoded field is emitted unconditionally for LSI numeric
        // sort keys too (same producer as GSI), so ordered LSI queries can sort
        // high-precision values once the opt-in flag is enabled.
        var json = WriteText(NumericLsiMeta(), "{\"sk\":{\"S\":\"r\"},\"lsk\":{\"N\":\"42\"}}");
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty(OrderProp + "lsk", out var ord));
        Assert.Equal(JsonValueKind.String, ord.ValueKind);
        Assert.NotEmpty(ord.GetString()!);
    }

    [Fact]
    public void Does_not_emit_when_lsi_sort_attribute_absent_from_item()
    {
        var json = WriteText(NumericLsiMeta(), "{\"sk\":{\"S\":\"r\"},\"other\":{\"S\":\"x\"}}");
        Assert.DoesNotContain(OrderProp, json, StringComparison.Ordinal);
    }

    [Fact]
    public void Emits_order_key_for_numeric_gsi_sort_attribute()
    {
        var json = WriteText(NumericGsiMeta(), "{\"ghk\":{\"S\":\"h\"},\"gsk\":{\"N\":\"42\"}}");
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty(OrderProp + "gsk", out var ord));
        Assert.Equal(JsonValueKind.String, ord.ValueKind);
        Assert.NotEmpty(ord.GetString()!);
    }

    [Fact]
    public void Does_not_emit_when_gsi_sort_is_string()
    {
        var json = WriteText(StringGsiMeta(), "{\"ghk\":{\"S\":\"h\"},\"gsk\":{\"S\":\"abc\"}}");
        Assert.DoesNotContain(OrderProp, json, StringComparison.Ordinal);
    }

    [Fact]
    public void Does_not_emit_when_no_gsi_declared()
    {
        var meta = new TableMetadata
        {
            TableName = "t",
            AttributeDefinitions = new List<TableAttributeDefinition> { new() { Name = "pk", Type = "S" } },
            KeySchema = new List<TableKeySchemaElement> { new() { Name = "pk", KeyType = "HASH" } },
        };
        var json = WriteText(meta, "{\"n\":{\"N\":\"42\"}}");
        Assert.DoesNotContain(OrderProp, json, StringComparison.Ordinal);
    }

    [Fact]
    public void Does_not_emit_when_sort_attribute_absent_from_item()
    {
        // Sparse-index item: the N-typed GSI sort attribute is not present.
        var json = WriteText(NumericGsiMeta(), "{\"ghk\":{\"S\":\"h\"},\"other\":{\"S\":\"x\"}}");
        Assert.DoesNotContain(OrderProp, json, StringComparison.Ordinal);
    }

    [Fact]
    public void Compute_returns_null_when_no_numeric_gsi_sort_key()
    {
        Assert.Null(SecondaryIndexOrderKeys.Compute(StringGsiMeta(), Parse("{\"gsk\":{\"S\":\"a\"}}")));
    }

    [Theory]
    // Pairs where the SECOND value is numerically greater; the encoded order key
    // of the second must sort lexically AFTER the first. Includes the
    // high-precision cases that break bare Cosmos ORDER BY (envelope storage).
    [InlineData("1", "2")]
    [InlineData("-5", "-1")]
    [InlineData("-1", "1")]
    [InlineData("0", "0.0000000000000001")]
    [InlineData("1.0000000000000001", "1.0000000000000002")]
    [InlineData("99999999999999999999", "100000000000000000000")]
    [InlineData("-100000000000000000000", "-99999999999999999999")]
    [InlineData("123456789012345678901234567890.0001", "123456789012345678901234567890.0002")]
    public void Order_key_preserves_numeric_order_lexically(string lo, string hi)
    {
        var meta = NumericGsiMeta();
        var loKey = ExtractOrderKey(WriteText(meta, $"{{\"ghk\":{{\"S\":\"h\"}},\"gsk\":{{\"N\":\"{lo}\"}}}}"));
        var hiKey = ExtractOrderKey(WriteText(meta, $"{{\"ghk\":{{\"S\":\"h\"}},\"gsk\":{{\"N\":\"{hi}\"}}}}"));
        Assert.True(string.CompareOrdinal(loKey, hiKey) < 0,
            $"expected order key for {lo} ('{loKey}') to sort before {hi} ('{hiKey}')");
    }

    private static string ExtractOrderKey(string docJson)
    {
        using var doc = JsonDocument.Parse(docJson);
        return doc.RootElement.GetProperty(OrderProp + "gsk").GetString()!;
    }

    [Fact]
    public void Order_key_is_invisible_on_read()
    {
        // A doc carrying the reserved order-key field must read back without it:
        // the _a2a prefix is auto-skipped by the read transform.
        var item = Parse("{\"ghk\":{\"S\":\"h\"},\"gsk\":{\"N\":\"42\"}}");
        var orderKeys = SecondaryIndexOrderKeys.Compute(NumericGsiMeta(), item);
        Assert.NotNull(orderKeys);
        var bw = new ArrayBufferWriter<byte>();
        InferredAttributeStorage.WriteCosmosDocument(bw, "id", "pk", item, null, orderKeys);

        using var stored = JsonDocument.Parse(Encoding.UTF8.GetString(bw.WrittenSpan));
        // Sanity: the reserved field really is present in the stored doc.
        Assert.True(stored.RootElement.TryGetProperty(OrderProp + "gsk", out _));

        var readBack = InferredAttributeStorage.ExtractItem(stored.RootElement);
        Assert.NotNull(readBack);
        Assert.False(readBack!.ContainsKey(OrderProp + "gsk"));
        Assert.True(readBack.TryGetValue("gsk", out var gsk));
        Assert.Equal("42", gsk.GetProperty("N").GetString());
    }

    [Fact]
    public void Binary_encoder_stores_the_order_key()
    {
        var item = Parse("{\"ghk\":{\"S\":\"h\"},\"gsk\":{\"N\":\"42\"}}");
        var orderKeys = SecondaryIndexOrderKeys.Compute(NumericGsiMeta(), item);
        Assert.NotNull(orderKeys);
        using var w = InferredAttributeStorage.WriteCosmosDocumentBinary("id", "pk", item, null, orderKeys);
        // The binary format stores property names as UTF-8 verbatim, so the
        // reserved name appears as a contiguous ASCII run in the raw bytes.
        var raw = Encoding.Latin1.GetString(w.WrittenMemory.ToArray());
        Assert.Contains(OrderProp + "gsk", raw, StringComparison.Ordinal);
    }
}
