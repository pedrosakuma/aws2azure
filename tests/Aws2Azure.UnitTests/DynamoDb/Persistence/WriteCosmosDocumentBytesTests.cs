using System;
using System.Buffers;
using System.Text;
using System.Text.Json;
using Aws2Azure.Modules.DynamoDb.Persistence;

namespace Aws2Azure.UnitTests.DynamoDb.Persistence;

/// <summary>
/// Guards the #334 zero-copy write tail: the UTF-8 bytes produced by
/// <see cref="InferredAttributeStorage.WriteCosmosDocument"/> (the new HTTP-body
/// path) must be byte-identical to the UTF-8 of the legacy string-producing
/// <see cref="InferredAttributeStorage.BuildCosmosDocument"/> — no wire change.
/// </summary>
public class WriteCosmosDocumentBytesTests
{
    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Theory]
    [InlineData("i", "p", "{\"name\":{\"S\":\"Alice\"}}")]
    [InlineData("user-42", "user-42", "{\"id\":{\"S\":\"user-42\"},\"name\":{\"S\":\"Alice\"}}")]
    [InlineData("k", "pk", "{\"n\":{\"N\":\"12345\"},\"b\":{\"BOOL\":true},\"nul\":{\"NULL\":true}}")]
    [InlineData("k", "pk", "{\"big\":{\"N\":\"123456789012345678901234567890.0001\"}}")]
    [InlineData("k", "pk", "{\"m\":{\"M\":{\"inner\":{\"S\":\"x\\\"quote\\\"\"}}},\"l\":{\"L\":[{\"S\":\"a\"},{\"N\":\"1\"}]}}")]
    [InlineData("k", "pk", "{\"bin\":{\"B\":\"AQID\"},\"ss\":{\"SS\":[\"a\",\"b\"]},\"ns\":{\"NS\":[\"1\",\"2\"]}}")]
    [InlineData("k", "pk", "{\"unicode\":{\"S\":\"héllo → 世界 😀\"}}")]
    public void WriteCosmosDocument_is_byte_identical_to_BuildCosmosDocument(string id, string pk, string itemJson)
    {
        var item = Parse(itemJson);

        var expected = Encoding.UTF8.GetBytes(InferredAttributeStorage.BuildCosmosDocument(id, pk, item));

        var bw = new ArrayBufferWriter<byte>();
        InferredAttributeStorage.WriteCosmosDocument(bw, id, pk, item);

        Assert.Equal(expected, bw.WrittenSpan.ToArray());
    }

    [Fact]
    public void WriteCosmosDocument_rejects_reserved_a2a_namespace_attribute()
    {
        var item = Parse("{\"_a2a:custom\":{\"S\":\"oops\"}}");
        var bw = new ArrayBufferWriter<byte>();
        Assert.Throws<ArgumentException>(
            () => InferredAttributeStorage.WriteCosmosDocument(bw, "id1", "pk1", item));
    }

    [Fact]
    public void WriteCosmosDocument_rejects_non_object_item()
    {
        var item = Parse("[1,2,3]");
        var bw = new ArrayBufferWriter<byte>();
        Assert.Throws<ArgumentException>(
            () => InferredAttributeStorage.WriteCosmosDocument(bw, "id1", "pk1", item));
    }
}
