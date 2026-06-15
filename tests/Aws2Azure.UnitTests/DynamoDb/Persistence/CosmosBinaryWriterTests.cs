using System;
using System.Buffers;
using System.Text;
using System.Text.Json;
using Aws2Azure.Modules.DynamoDb.Internal;
using Aws2Azure.Modules.DynamoDb.Persistence;
using Xunit;

namespace Aws2Azure.UnitTests.DynamoDb.Persistence;

/// <summary>
/// Guards the #335 CosmosBinary write encoder. Two complementary gates:
///
/// <list type="bullet">
///   <item><b>Golden corpus</b> — a hand-derived byte sequence pins the exact
///   <c>0x80</c> wire format so an accidental marker/framing change is caught.</item>
///   <item><b>Decode round-trip</b> — for a broad attribute-shape corpus,
///   <c>decode(WriteCosmosDocumentBinary(x))</c> must equal the canonical text
///   rendering of the same document, run through the <b>production</b>
///   <see cref="CosmosBinaryDecoder"/> (the spike's <c>[GlobalSetup]</c> gate,
///   productionised). The text reference is re-rendered with the decoder's
///   default JSON encoder so escaping is apples-to-apples.</item>
/// </list>
/// </summary>
public class CosmosBinaryWriterTests
{
    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static byte[] Binary(string id, string pk, JsonElement item)
    {
        var bw = new ArrayBufferWriter<byte>();
        InferredAttributeStorage.WriteCosmosDocumentBinary(bw, id, pk, item);
        return bw.WrittenSpan.ToArray();
    }

    private static byte[] Decode(byte[] binary)
    {
        var bw = new ArrayBufferWriter<byte>();
        CosmosBinaryDecoder.Decode(binary, bw);
        return bw.WrittenSpan.ToArray();
    }

    // The text reference must use the SAME JSON encoder the decoder writes
    // through (default escaping), otherwise relaxed-vs-default escaping of
    // characters like '<' '&' would create a spurious mismatch. Re-parsing the
    // production (relaxed) text and re-emitting with a default-encoder writer
    // gives the canonical form the decoder produces.
    private static byte[] DecoderEquivalentText(string id, string pk, JsonElement item)
    {
        string relaxed = InferredAttributeStorage.BuildCosmosDocument(id, pk, item);
        using var doc = JsonDocument.Parse(relaxed);
        var bw = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(bw))
        {
            doc.RootElement.WriteTo(writer);
        }

        return bw.WrittenSpan.ToArray();
    }

    [Theory]
    // primitives + envelope-tag value
    [InlineData("i", "p", "{\"name\":{\"S\":\"Alice\"}}")]
    [InlineData("user-42", "user-42", "{\"id\":{\"S\":\"user-42\"},\"name\":{\"S\":\"Alice\"}}")]
    [InlineData("k", "pk", "{\"n\":{\"N\":\"12345\"},\"b\":{\"BOOL\":true},\"nul\":{\"NULL\":true}}")]
    // integers across int32 / int64 / negative + fractional doubles
    [InlineData("k", "pk", "{\"i32\":{\"N\":\"2147483647\"},\"neg\":{\"N\":\"-2147483648\"}}")]
    [InlineData("k", "pk", "{\"i64\":{\"N\":\"9007199254740991\"},\"negi64\":{\"N\":\"-9007199254740991\"}}")]
    [InlineData("k", "pk", "{\"a\":{\"N\":\"1.5\"},\"b\":{\"N\":\"0.001\"},\"c\":{\"N\":\"-2.5\"},\"d\":{\"N\":\"3.14159\"}}")]
    [InlineData("k", "pk", "{\"zero\":{\"N\":\"0\"},\"big\":{\"N\":\"100\"}}")]
    // high-precision number → string envelope
    [InlineData("k", "pk", "{\"big\":{\"N\":\"123456789012345678901234567890.0001\"}}")]
    // nested map/list with an escaped quote
    [InlineData("k", "pk", "{\"m\":{\"M\":{\"inner\":{\"S\":\"x\\\"quote\\\"\"}}},\"l\":{\"L\":[{\"S\":\"a\"},{\"N\":\"1\"}]}}")]
    // binary + sets
    [InlineData("k", "pk", "{\"bin\":{\"B\":\"AQID\"},\"ss\":{\"SS\":[\"a\",\"b\"]},\"ns\":{\"NS\":[\"1\",\"2\"]},\"bs\":{\"BS\":[\"BAU=\"]}}")]
    // unicode + multibyte UTF-8
    [InlineData("k", "pk", "{\"unicode\":{\"S\":\"héllo → 世界 😀\"}}")]
    // characters that escape differently under relaxed vs default encoder
    [InlineData("k", "pk", "{\"html\":{\"S\":\"<a href='x'>&amp; +\"}}")]
    // empty containers + empty string
    [InlineData("k", "pk", "{\"emptyM\":{\"M\":{}},\"emptyL\":{\"L\":[]},\"emptyS\":{\"S\":\"\"}}")]
    // deeply nested
    [InlineData("k", "pk", "{\"d\":{\"L\":[{\"L\":[{\"M\":{\"x\":{\"L\":[{\"N\":\"7\"}]}}}]}]}}")]
    // id attribute shadow-encoding
    [InlineData("user-42", "user-42", "{\"id\":{\"S\":\"user-42\"},\"role\":{\"S\":\"admin\"}}")]
    public void Decode_of_binary_matches_text_through_production_decoder(string id, string pk, string itemJson)
    {
        var item = Parse(itemJson);

        byte[] expected = DecoderEquivalentText(id, pk, item);
        byte[] decoded = Decode(Binary(id, pk, item));

        Assert.Equal(Encoding.UTF8.GetString(expected), Encoding.UTF8.GetString(decoded));
    }

    [Fact]
    public void Binary_body_starts_with_format_marker_and_object_root()
    {
        byte[] binary = Binary("i", "p", Parse("{\"x\":{\"S\":\"y\"}}"));

        Assert.True(CosmosBinaryDecoder.IsBinary(binary));
        Assert.Equal(0x80, binary[0]); // CosmosBinary format marker
        Assert.Equal(0xEF, binary[1]); // LC4 object root
    }

    [Fact]
    public void Golden_empty_item_pins_wire_format()
    {
        // Hand-derived: 0x80 marker, 0xEF LC4 object, 8-byte prefix
        // (payloadLength=25, count=3), then the three metadata properties.
        byte[] expected =
        [
            0x80, 0xEF,
            0x19, 0x00, 0x00, 0x00, // payload length = 25
            0x03, 0x00, 0x00, 0x00, // element count  = 3
            0x82, 0x69, 0x64,                               // "id"
            0x81, 0x69,                                     // "i"
            0x87, 0x5F, 0x61, 0x32, 0x61, 0x5F, 0x70, 0x6B, // "_a2a_pk"
            0x81, 0x70,                                     // "p"
            0x84, 0x5F, 0x61, 0x32, 0x61,                   // "_a2a"
            0x84, 0x69, 0x74, 0x65, 0x6D,                   // "item"
        ];

        byte[] actual = Binary("i", "p", Parse("{}"));

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Golden_int32_number_pins_marker()
    {
        // id="k", pk="p", item={"n":{"N":"5"}} → adds an int32 (0xCA) value.
        byte[] expected =
        [
            0x80, 0xEF,
            0x20, 0x00, 0x00, 0x00, // payload length = 32
            0x04, 0x00, 0x00, 0x00, // element count  = 4
            0x82, 0x69, 0x64,                               // "id"
            0x81, 0x6B,                                     // "k"
            0x87, 0x5F, 0x61, 0x32, 0x61, 0x5F, 0x70, 0x6B, // "_a2a_pk"
            0x81, 0x70,                                     // "p"
            0x84, 0x5F, 0x61, 0x32, 0x61,                   // "_a2a"
            0x84, 0x69, 0x74, 0x65, 0x6D,                   // "item"
            0x81, 0x6E,                                     // "n"
            0xCA, 0x05, 0x00, 0x00, 0x00,                   // int32 5
        ];

        byte[] actual = Binary("k", "p", Parse("{\"n\":{\"N\":\"5\"}}"));

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Binary_rejects_reserved_a2a_namespace_attribute()
    {
        var item = Parse("{\"_a2a:custom\":{\"S\":\"oops\"}}");
        var bw = new ArrayBufferWriter<byte>();
        Assert.Throws<ArgumentException>(
            () => InferredAttributeStorage.WriteCosmosDocumentBinary(bw, "id1", "pk1", item));
    }

    [Fact]
    public void Binary_rejects_non_object_item()
    {
        var item = Parse("[1,2,3]");
        var bw = new ArrayBufferWriter<byte>();
        Assert.Throws<ArgumentException>(
            () => InferredAttributeStorage.WriteCosmosDocumentBinary(bw, "id1", "pk1", item));
    }

    [Fact]
    public void Large_string_uses_extended_length_marker_and_round_trips()
    {
        // A value longer than 65535 UTF-8 bytes forces the 0xC2 (uint32) length
        // marker; assert it both encodes and decodes losslessly.
        string big = new('a', 70000);
        var item = Parse($"{{\"big\":{{\"S\":\"{big}\"}}}}");

        byte[] expected = DecoderEquivalentText("k", "p", item);
        byte[] decoded = Decode(Binary("k", "p", item));

        Assert.Equal(Encoding.UTF8.GetString(expected), Encoding.UTF8.GetString(decoded));
    }
}
