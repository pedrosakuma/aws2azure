using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aws2Azure.Benchmarks.DynamoDb.Spike332;
using Aws2Azure.Modules.DynamoDb.Operations;
using BenchmarkDotNet.Attributes;

namespace Aws2Azure.Benchmarks.DynamoDb;

/// <summary>
/// Quantifies the ceiling for the "drop the <see cref="JsonElement"/> DOM on the
/// PutItem write path" idea (custom-converter range capture). Three arms over the
/// same PutItem request body:
///
/// <list type="bullet">
///   <item><c>Dom_PlusLocate</c> — the SHIPPING pre-encode cost: deserialize the
///     request (materializing the <see cref="JsonElement"/> <c>Item</c> DOM) then
///     re-scan the body with <see cref="ItemHandlers.TryLocateItemBytes"/> to
///     recover the item's raw byte range for the single-pass encoder.</item>
///   <item><c>Dom_Only</c> — just the deserialize (isolates the DOM cost from the
///     locate scan).</item>
///   <item><c>RangeConverter</c> — the PROPOSED path: deserialize with the
///     <c>Item</c> bound by a custom <see cref="JsonConverter{T}"/> that records
///     <c>(start,length)</c> via <c>TokenStartIndex</c>/<c>BytesConsumed</c> — no
///     DOM, and the range falls out of the single deserialize pass (no separate
///     locate scan).</item>
/// </list>
///
/// Delta <c>Dom_PlusLocate − RangeConverter</c> is the honest CPU+alloc ceiling
/// the refactor could save on the write path's parse step. A <c>[GlobalSetup]</c>
/// gate asserts the converter's range is byte-identical to
/// <see cref="ItemHandlers.TryLocateItemBytes"/>, so the numbers describe a
/// correct capture.
/// </summary>
[MemoryDiagnoser]
public class PutItemParseBenchmarks
{
    public sealed record Case(string Name, int StringAttrs, int NumberAttrs, int PayloadBytes)
    {
        public override string ToString() => Name;
    }

    public static IEnumerable<Case> Cases =>
    [
        new("lean", StringAttrs: 0, NumberAttrs: 0, PayloadBytes: 0),
        new("payload_512", StringAttrs: 0, NumberAttrs: 0, PayloadBytes: 512),
        new("wide_20s_20n", StringAttrs: 20, NumberAttrs: 20, PayloadBytes: 0),
    ];

    [ParamsSource(nameof(Cases))]
    public Case Item { get; set; } = null!;

    private byte[] _body = null!;

    [GlobalSetup]
    public void Setup()
    {
        string itemJson = SyntheticDdbItem.Build(Item.StringAttrs, Item.NumberAttrs, Item.PayloadBytes);
        string body = "{\"TableName\":\"bench-table\",\"Item\":" + itemJson + ",\"ReturnValues\":\"NONE\"}";
        _body = Encoding.UTF8.GetBytes(body);

        // Correctness gate: the converter range must equal the shipping locate
        // scan, and must slice the exact item bytes.
        if (!ItemHandlers.TryLocateItemBytes(_body, out int locStart, out int locLength))
        {
            throw new InvalidOperationException($"TryLocateItemBytes failed for '{Item.Name}'.");
        }

        var rng = JsonSerializer.Deserialize(_body, RangeJsonContext.Default.PutItemRangeRequest)!;
        if (rng.Item.Start != locStart || rng.Item.Length != locLength)
        {
            throw new InvalidOperationException(
                $"converter range ({rng.Item.Start},{rng.Item.Length}) != locate ({locStart},{locLength}) for '{Item.Name}'.");
        }

        ReadOnlySpan<byte> sliced = _body.AsSpan(rng.Item.Start, rng.Item.Length);
        if (!sliced.SequenceEqual(Encoding.UTF8.GetBytes(itemJson)))
        {
            throw new InvalidOperationException($"converter range did not slice the item bytes for '{Item.Name}'.");
        }

        var dom = JsonSerializer.Deserialize(_body, DomJsonContext.Default.PutItemDomRequest)!;
        if (dom.Item.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException($"DOM Item not an object for '{Item.Name}'.");
        }
    }

    /// <summary>Shipping pre-encode cost: DOM deserialize + locate re-scan.</summary>
    [Benchmark(Baseline = true)]
    public int Dom_PlusLocate()
    {
        var req = JsonSerializer.Deserialize(_body, DomJsonContext.Default.PutItemDomRequest)!;
        ItemHandlers.TryLocateItemBytes(_body, out int start, out int length);
        return (int)req.Item.ValueKind + start + length;
    }

    /// <summary>Deserialize only — isolates the DOM materialization cost.</summary>
    [Benchmark]
    public int Dom_Only()
    {
        var req = JsonSerializer.Deserialize(_body, DomJsonContext.Default.PutItemDomRequest)!;
        return (int)req.Item.ValueKind;
    }

    /// <summary>Proposed: range capture via custom converter, no DOM, no re-scan.</summary>
    [Benchmark]
    public int RangeConverter()
    {
        var req = JsonSerializer.Deserialize(_body, RangeJsonContext.Default.PutItemRangeRequest)!;
        return req.Item.Start + req.Item.Length;
    }

    /// <summary>
    /// Low-risk variant: range capture + a TRANSIENT pooled <see cref="JsonDocument"/>
    /// over the sliced item, disposed immediately. Unlike the deserializer's retained
    /// JsonElement, <see cref="JsonDocument.Parse(ReadOnlyMemory{byte})"/> rents its
    /// metadata DB from the array pool and returns it on Dispose — so the existing
    /// JsonElement validators can run unchanged with (hopefully) near-zero net alloc.
    /// </summary>
    [Benchmark]
    public int RangePooledParse()
    {
        var req = JsonSerializer.Deserialize(_body, RangeJsonContext.Default.PutItemRangeRequest)!;
        using var doc = JsonDocument.Parse(_body.AsMemory(req.Item.Start, req.Item.Length));
        return (int)doc.RootElement.ValueKind;
    }
}

/// <summary>Byte range of a property value within the deserialized buffer.</summary>
internal readonly record struct JsonRange(int Start, int Length);

/// <summary>
/// Records the byte range of a JSON value instead of materializing it. Valid only
/// when deserializing from a contiguous in-memory buffer (the
/// <c>ReadOnlySpan&lt;byte&gt;</c>/<c>byte[]</c> overload), where
/// <see cref="Utf8JsonReader.TokenStartIndex"/> /
/// <see cref="Utf8JsonReader.BytesConsumed"/> are absolute offsets into the input.
/// </summary>
internal sealed class JsonRangeConverter : JsonConverter<JsonRange>
{
    public override JsonRange Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        int start = checked((int)reader.TokenStartIndex);
        reader.Skip();
        int length = checked((int)reader.BytesConsumed) - start;
        return new JsonRange(start, length);
    }

    public override void Write(Utf8JsonWriter writer, JsonRange value, JsonSerializerOptions options)
        => throw new NotSupportedException();
}

internal sealed class PutItemRangeRequest
{
    [JsonPropertyName("TableName")] public string? TableName { get; set; }

    [JsonPropertyName("Item")]
    [JsonConverter(typeof(JsonRangeConverter))]
    public JsonRange Item { get; set; }

    [JsonPropertyName("ReturnValues")] public string? ReturnValues { get; set; }
}

[JsonSerializable(typeof(PutItemRangeRequest))]
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    AllowTrailingCommas = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class RangeJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Benchmark-local mirror of the OLD shipping shape (<c>Item</c> as a retained
/// <see cref="JsonElement"/> DOM), kept here so the <c>Dom_*</c> arms still measure
/// the pre-change cost after production switched <c>PutItemRequest.Item</c> to a
/// <c>JsonRange</c>. Mirrors <c>ItemJsonContext</c>'s options.
/// </summary>
internal sealed class PutItemDomRequest
{
    [JsonPropertyName("TableName")] public string? TableName { get; set; }

    [JsonPropertyName("Item")] public JsonElement Item { get; set; }

    [JsonPropertyName("ReturnValues")] public string? ReturnValues { get; set; }
}

[JsonSerializable(typeof(PutItemDomRequest))]
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    AllowTrailingCommas = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class DomJsonContext : JsonSerializerContext
{
}
