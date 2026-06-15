using System;
using System.Buffers;
using System.Text;
using System.Text.Json;
using Aws2Azure.Modules.DynamoDb.Internal;
using Aws2Azure.Modules.DynamoDb.Operations;
using Aws2Azure.Modules.DynamoDb.Persistence;
using Xunit;

namespace Aws2Azure.UnitTests.DynamoDb.Persistence;

/// <summary>
/// Guards the #342 single-pass wire write path: encoding the Cosmos document
/// straight from the raw UTF-8 bytes of the DynamoDB attribute-map (via
/// <see cref="Utf8JsonReader"/>) must be byte-identical to the
/// <see cref="JsonElement"/> overloads, for both the JSON-text and CosmosBinary
/// (<c>0x80</c>) back-ends. The wire path drops the per-attribute
/// <c>GetString()</c> materialization but must change <b>nothing</b> on the
/// wire.
/// </summary>
public class WriteCosmosDocumentWireTests
{
    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    public static TheoryData<string, string, string> Corpus =>
        new()
        {
            { "i", "p", "{\"name\":{\"S\":\"Alice\"}}" },
            { "user-42", "user-42", "{\"id\":{\"S\":\"user-42\"},\"name\":{\"S\":\"Alice\"}}" },
            { "k", "pk", "{\"n\":{\"N\":\"12345\"},\"b\":{\"BOOL\":true},\"nul\":{\"NULL\":true}}" },
            { "k", "pk", "{\"i32\":{\"N\":\"2147483647\"},\"neg\":{\"N\":\"-2147483648\"}}" },
            { "k", "pk", "{\"i64\":{\"N\":\"9007199254740991\"},\"negi64\":{\"N\":\"-9007199254740991\"}}" },
            { "k", "pk", "{\"a\":{\"N\":\"1.5\"},\"b\":{\"N\":\"0.001\"},\"c\":{\"N\":\"-2.5\"},\"d\":{\"N\":\"3.14159\"}}" },
            { "k", "pk", "{\"zero\":{\"N\":\"0\"},\"big\":{\"N\":\"100\"}}" },
            { "k", "pk", "{\"big\":{\"N\":\"123456789012345678901234567890.0001\"}}" },
            { "k", "pk", "{\"m\":{\"M\":{\"inner\":{\"S\":\"x\\\"quote\\\"\"}}},\"l\":{\"L\":[{\"S\":\"a\"},{\"N\":\"1\"}]}}" },
            { "k", "pk", "{\"bin\":{\"B\":\"AQID\"},\"ss\":{\"SS\":[\"a\",\"b\"]},\"ns\":{\"NS\":[\"1\",\"2\"]},\"bs\":{\"BS\":[\"BAU=\"]}}" },
            { "k", "pk", "{\"unicode\":{\"S\":\"héllo → 世界 😀\"}}" },
            // characters that escape differently under relaxed vs default encoder
            { "k", "pk", "{\"html\":{\"S\":\"<a href='x'>&amp; +\"}}" },
            // escaped sequences in a value: control char, quote, unicode escape
            { "k", "pk", "{\"esc\":{\"S\":\"line1\\nline2\\ttab\\u0041\\\"q\\\"\"}}" },
            // escaped property names (top-level and nested)
            { "k", "pk", "{\"a\\u0062c\":{\"S\":\"v\"},\"m\":{\"M\":{\"x\\ty\":{\"N\":\"1\"}}}}" },
            { "k", "pk", "{\"emptyM\":{\"M\":{}},\"emptyL\":{\"L\":[]},\"emptyS\":{\"S\":\"\"}}" },
            { "k", "pk", "{\"d\":{\"L\":[{\"L\":[{\"M\":{\"x\":{\"L\":[{\"N\":\"7\"}]}}}]}]}}" },
            { "user-42", "user-42", "{\"id\":{\"S\":\"user-42\"},\"role\":{\"S\":\"admin\"}}" },
            // escaped type tags (a client may legally escape the tag name)
            { "k", "pk", "{\"x\":{\"\\u0053\":\"v\"},\"y\":{\"\\u004e\":\"7\"}}" },
            { "k", "pk", "{\"s\":{\"\\u0053\\u0053\":[\"a\",\"b\"]},\"flag\":{\"\\u0042\\u004f\\u004f\\u004c\":true}}" },
        };

    [Theory]
    [MemberData(nameof(Corpus))]
    public void Wire_text_is_byte_identical_to_JsonElement_text(string id, string pk, string itemJson)
    {
        var item = Parse(itemJson);
        byte[] itemUtf8 = Encoding.UTF8.GetBytes(itemJson);

        var expected = new ArrayBufferWriter<byte>();
        InferredAttributeStorage.WriteCosmosDocument(expected, id, pk, item);

        var actual = new ArrayBufferWriter<byte>();
        InferredAttributeStorage.WriteCosmosDocument(actual, id, pk, itemUtf8);

        Assert.Equal(expected.WrittenSpan.ToArray(), actual.WrittenSpan.ToArray());
    }

    [Theory]
    [MemberData(nameof(Corpus))]
    public void Wire_binary_is_byte_identical_to_JsonElement_binary(string id, string pk, string itemJson)
    {
        var item = Parse(itemJson);
        byte[] itemUtf8 = Encoding.UTF8.GetBytes(itemJson);

        var expected = new ArrayBufferWriter<byte>();
        InferredAttributeStorage.WriteCosmosDocumentBinary(expected, id, pk, item);

        var actual = new ArrayBufferWriter<byte>();
        InferredAttributeStorage.WriteCosmosDocumentBinary(actual, id, pk, itemUtf8);

        Assert.Equal(expected.WrittenSpan.ToArray(), actual.WrittenSpan.ToArray());
    }

    [Theory]
    [MemberData(nameof(Corpus))]
    public void Wire_binary_self_owned_is_byte_identical(string id, string pk, string itemJson)
    {
        byte[] itemUtf8 = Encoding.UTF8.GetBytes(itemJson);

        var expected = new ArrayBufferWriter<byte>();
        InferredAttributeStorage.WriteCosmosDocumentBinary(expected, id, pk, itemUtf8);

        using var writer = InferredAttributeStorage.WriteCosmosDocumentBinary(id, pk, itemUtf8);

        Assert.Equal(expected.WrittenSpan.ToArray(), writer.WrittenMemory.ToArray());
    }

    [Fact]
    public void Wire_text_handles_long_escaped_string_via_pooled_buffer()
    {
        // > WireUnescapeStackThreshold (256) unescaped bytes with embedded
        // escapes forces the ArrayPool rent branch of the unescape helper.
        string big = new string('a', 300) + "\\n\\\"end\\\"";
        string itemJson = "{\"v\":{\"S\":\"" + big + "\"}}";
        var item = Parse(itemJson);
        byte[] itemUtf8 = Encoding.UTF8.GetBytes(itemJson);

        var expected = new ArrayBufferWriter<byte>();
        InferredAttributeStorage.WriteCosmosDocument(expected, "k", "p", item);

        var actual = new ArrayBufferWriter<byte>();
        InferredAttributeStorage.WriteCosmosDocument(actual, "k", "p", itemUtf8);

        Assert.Equal(expected.WrittenSpan.ToArray(), actual.WrittenSpan.ToArray());
    }

    [Fact]
    public void Wire_text_rejects_reserved_a2a_namespace_attribute()
    {
        byte[] itemUtf8 = Encoding.UTF8.GetBytes("{\"_a2a:custom\":{\"S\":\"oops\"}}");
        var bw = new ArrayBufferWriter<byte>();
        Assert.Throws<ArgumentException>(
            () => InferredAttributeStorage.WriteCosmosDocument(bw, "id1", "pk1", itemUtf8));
    }

    [Fact]
    public void Wire_binary_rejects_reserved_a2a_namespace_attribute()
    {
        byte[] itemUtf8 = Encoding.UTF8.GetBytes("{\"_a2a:custom\":{\"S\":\"oops\"}}");
        var bw = new ArrayBufferWriter<byte>();
        Assert.Throws<ArgumentException>(
            () => InferredAttributeStorage.WriteCosmosDocumentBinary(bw, "id1", "pk1", itemUtf8));
    }

    [Fact]
    public void Wire_rejects_nested_a2a_prefixed_name()
    {
        byte[] itemUtf8 = Encoding.UTF8.GetBytes("{\"m\":{\"M\":{\"_a2a:x\":{\"S\":\"oops\"}}}}");
        var bw = new ArrayBufferWriter<byte>();
        Assert.Throws<ArgumentException>(
            () => InferredAttributeStorage.WriteCosmosDocument(bw, "id1", "pk1", itemUtf8));
    }

    [Fact]
    public void Wire_rejects_non_object_item()
    {
        byte[] itemUtf8 = Encoding.UTF8.GetBytes("[1,2,3]");
        var bw = new ArrayBufferWriter<byte>();
        Assert.Throws<ArgumentException>(
            () => InferredAttributeStorage.WriteCosmosDocument(bw, "id1", "pk1", itemUtf8));
    }

    [Fact]
    public void Wire_rejects_multi_property_attribute_value()
    {
        byte[] itemUtf8 = Encoding.UTF8.GetBytes("{\"x\":{\"S\":\"a\",\"N\":\"1\"}}");
        var bw = new ArrayBufferWriter<byte>();
        Assert.Throws<ArgumentException>(
            () => InferredAttributeStorage.WriteCosmosDocument(bw, "id1", "pk1", itemUtf8));
    }

    [Fact]
    public void Wire_shadow_encodes_id_attribute()
    {
        byte[] itemUtf8 = Encoding.UTF8.GetBytes("{\"id\":{\"S\":\"u-1\"}}");
        var bw = new ArrayBufferWriter<byte>();
        InferredAttributeStorage.WriteCosmosDocument(bw, "u-1", "u-1", itemUtf8);

        string doc = Encoding.UTF8.GetString(bw.WrittenSpan);
        Assert.Contains("\"_a2a$id\":\"u-1\"", doc);
    }
}
