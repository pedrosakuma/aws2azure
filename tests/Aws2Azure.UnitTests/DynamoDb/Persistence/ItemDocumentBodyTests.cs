using System;
using System.Buffers;
using System.Text.Json;
using Aws2Azure.Modules.DynamoDb.Internal;
using Aws2Azure.Modules.DynamoDb.Operations;
using Aws2Azure.Modules.DynamoDb.Persistence;
using Xunit;

namespace Aws2Azure.UnitTests.DynamoDb.Persistence;

/// <summary>
/// Guards #336 write-body selection: the standalone-document write paths emit
/// CosmosBinary (<c>0x80</c>) when the flag is on and JSON text otherwise. The
/// text path must stay byte-identical to today; the binary path must decode
/// (through the production <see cref="CosmosBinaryDecoder"/>) to the same
/// document as the text path.
/// </summary>
public class ItemDocumentBodyTests
{
    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static byte[] TextDocument(string id, string pk, JsonElement item)
    {
        var bw = new ArrayBufferWriter<byte>();
        InferredAttributeStorage.WriteCosmosDocument(bw, id, pk, item);
        return bw.WrittenSpan.ToArray();
    }

    // Re-render through the decoder's default JSON encoder so binary-vs-text
    // escaping is apples-to-apples (the production text path uses relaxed
    // escaping; the decoder emits with the default encoder).
    private static byte[] DecoderEquivalentText(string id, string pk, JsonElement item)
    {
        var bw = new ArrayBufferWriter<byte>();
        using var doc = JsonDocument.Parse(TextDocument(id, pk, item));
        using (var writer = new Utf8JsonWriter(bw))
        {
            doc.RootElement.WriteTo(writer);
        }

        return bw.WrittenSpan.ToArray();
    }

    private static byte[] Decode(ReadOnlyMemory<byte> binary)
    {
        var bw = new ArrayBufferWriter<byte>();
        CosmosBinaryDecoder.Decode(binary.Span, bw);
        return bw.WrittenSpan.ToArray();
    }

    [Theory]
    [InlineData("k", "p", "{\"name\":{\"S\":\"Alice\"}}")]
    [InlineData("k", "p", "{\"n\":{\"N\":\"12345\"},\"b\":{\"BOOL\":true}}")]
    [InlineData("k", "p", "{\"m\":{\"M\":{\"a\":{\"S\":\"x\"},\"b\":{\"N\":\"1.5\"}}},\"l\":{\"L\":[{\"S\":\"y\"},{\"N\":\"2\"}]}}")]
    public void Text_selection_is_byte_identical_to_direct_text_write(string id, string pk, string itemJson)
    {
        var item = Parse(itemJson);

        using var body = ItemHandlers.ItemDocumentBody.Create(id, pk, item, binary: false);

        Assert.Equal((byte)'{', body.Memory.Span[0]);
        Assert.Equal(TextDocument(id, pk, item), body.Memory.ToArray());
    }

    [Theory]
    [InlineData("k", "p", "{\"name\":{\"S\":\"Alice\"}}")]
    [InlineData("k", "p", "{\"n\":{\"N\":\"12345\"},\"b\":{\"BOOL\":true}}")]
    [InlineData("k", "p", "{\"m\":{\"M\":{\"a\":{\"S\":\"x\"},\"b\":{\"N\":\"1.5\"}}},\"l\":{\"L\":[{\"S\":\"y\"},{\"N\":\"2\"}]}}")]
    public void Binary_selection_emits_marker_and_decodes_to_text(string id, string pk, string itemJson)
    {
        var item = Parse(itemJson);

        using var body = ItemHandlers.ItemDocumentBody.Create(id, pk, item, binary: true);

        Assert.Equal(0x80, body.Memory.Span[0]);
        Assert.Equal(DecoderEquivalentText(id, pk, item), Decode(body.Memory));
    }

    [Theory]
    [InlineData(false, (byte)'{')]
    [InlineData(true, (byte)0x80)]
    public void BuildItemDocumentBytes_selects_encoding(bool binary, byte expectedFirstByte)
    {
        var item = Parse("{\"name\":{\"S\":\"Alice\"},\"n\":{\"N\":\"42\"}}");

        byte[] bytes = ItemHandlers.BuildItemDocumentBytes("k", "p", item, binary);

        Assert.Equal(expectedFirstByte, bytes[0]);
        if (binary)
        {
            Assert.Equal(DecoderEquivalentText("k", "p", item), Decode(bytes));
        }
        else
        {
            Assert.Equal(TextDocument("k", "p", item), bytes);
        }
    }

    [Fact]
    public void BuildItemDocumentBytes_three_arg_overload_is_text()
    {
        var item = Parse("{\"name\":{\"S\":\"Alice\"}}");

        byte[] bytes = ItemHandlers.BuildItemDocumentBytes("k", "p", item);

        Assert.Equal((byte)'{', bytes[0]);
        Assert.Equal(TextDocument("k", "p", item), bytes);
    }
}
