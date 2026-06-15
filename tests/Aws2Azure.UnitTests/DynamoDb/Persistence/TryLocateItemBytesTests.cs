using System;
using System.Buffers;
using System.Text;
using System.Text.Json;
using Aws2Azure.Modules.DynamoDb.Operations;
using Aws2Azure.Modules.DynamoDb.Persistence;
using Xunit;

namespace Aws2Azure.UnitTests.DynamoDb.Persistence;

/// <summary>
/// Guards the #342 request-body item locator and the wire
/// <see cref="ItemHandlers.ItemDocumentBody"/> overload: the no-alloc scan must
/// return the exact byte range of the top-level <c>"Item"</c> value, and the
/// wire-encoded body must be byte-identical to the parsed-JsonElement body
/// (falling back to it when the item bytes are not recoverable).
/// </summary>
public class TryLocateItemBytesTests
{
    private static JsonElement ParseItem(byte[] body)
    {
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("Item").Clone();
    }

    [Theory]
    [InlineData("{\"TableName\":\"t\",\"Item\":{\"name\":{\"S\":\"Alice\"}}}")]
    [InlineData("{\"Item\":{\"a\":{\"N\":\"1\"}},\"TableName\":\"t\"}")]
    [InlineData("{\"TableName\":\"t\",\"Item\":{\"m\":{\"M\":{\"x\":{\"S\":\"y\"}}},\"l\":{\"L\":[{\"N\":\"1\"}]}},\"ReturnValues\":\"NONE\"}")]
    [InlineData("{\"ExpressionAttributeValues\":{\":v\":{\"S\":\"z\"}},\"Item\":{\"k\":{\"S\":\"v\"}}}")]
    public void Locates_item_value_byte_range(string requestJson)
    {
        byte[] body = Encoding.UTF8.GetBytes(requestJson);

        Assert.True(ItemHandlers.TryLocateItemBytes(body, out int start, out int length));

        // The located slice must parse to the same object as the "Item" property.
        var slice = body.AsSpan(start, length);
        using var sliceDoc = JsonDocument.Parse(slice.ToArray());
        var expected = ParseItem(body);

        Assert.Equal(JsonValueKind.Object, sliceDoc.RootElement.ValueKind);
        Assert.Equal(expected.GetRawText(), sliceDoc.RootElement.GetRawText());
    }

    [Theory]
    [InlineData("{\"TableName\":\"t\"}")]            // no Item
    [InlineData("[1,2,3]")]                          // not an object
    [InlineData("{\"item\":{\"S\":\"x\"}}")]         // non-canonical casing → not optimized
    [InlineData("{\"Item\":{\"a\":{\"N\":\"1\"}},\"Item\":{\"b\":{\"N\":\"2\"}}}")] // duplicate → JsonSerializer last-wins, bail to JsonElement path
    [InlineData("{\"Item\":{\"a\":{\"N\":\"1\"}},\"item\":{\"b\":{\"N\":\"2\"}}}")] // case-insensitive duplicate → req.Item binds last, bail
    public void Returns_false_when_item_not_recoverable(string requestJson)
    {
        byte[] body = Encoding.UTF8.GetBytes(requestJson);
        Assert.False(ItemHandlers.TryLocateItemBytes(body, out _, out _));
    }

    [Fact]
    public void Trailing_comma_request_locates_item_without_throwing()
    {
        // The serializer allows trailing commas; the locator must accept the same
        // grammar rather than throw mid-scan.
        byte[] body = Encoding.UTF8.GetBytes("{\"Item\":{\"a\":{\"N\":\"1\"},}}");
        Assert.True(ItemHandlers.TryLocateItemBytes(body, out int start, out int length));
        string slice = Encoding.UTF8.GetString(body, start, length);
        Assert.Equal("{\"a\":{\"N\":\"1\"},}", slice);
    }

    [Fact]
    public void Duplicate_item_wire_overload_encodes_the_last_winning_item()
    {
        // JsonSerializer binds req.Item to the LAST duplicate; the wire overload
        // must bail to the JsonElement path so the encoded body matches the
        // validated/routed item rather than the first occurrence.
        byte[] body = Encoding.UTF8.GetBytes(
            "{\"Item\":{\"v\":{\"S\":\"first\"}},\"Item\":{\"v\":{\"S\":\"last\"}}}");
        var lastItem = Parse("{\"v\":{\"S\":\"last\"}}");

        using var expected = ItemHandlers.ItemDocumentBody.Create("k", "p", lastItem, binary: false);
        using var actual = ItemHandlers.ItemDocumentBody.Create("k", "p", body, lastItem, binary: false);

        Assert.Equal(expected.Memory.ToArray(), actual.Memory.ToArray());
        Assert.Contains("\"v\":\"last\"", Encoding.UTF8.GetString(actual.Memory.Span));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ItemDocumentBody_wire_overload_is_byte_identical(bool binary)
    {
        string requestJson = "{\"TableName\":\"t\",\"Item\":{\"name\":{\"S\":\"Alice\"},\"n\":{\"N\":\"42\"},\"set\":{\"SS\":[\"a\",\"b\"]}}}";
        byte[] body = Encoding.UTF8.GetBytes(requestJson);
        var item = ParseItem(body);

        using var expected = ItemHandlers.ItemDocumentBody.Create("k", "p", item, binary);
        using var actual = ItemHandlers.ItemDocumentBody.Create("k", "p", body, item, binary);

        Assert.Equal(expected.Memory.ToArray(), actual.Memory.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ItemDocumentBody_wire_overload_falls_back_when_item_missing(bool binary)
    {
        // body has no top-level "Item" (simulating a synthesized item), so the
        // overload must fall back to the JsonElement path and still produce the
        // identical body.
        byte[] body = Encoding.UTF8.GetBytes("{\"TableName\":\"t\"}");
        var item = Parse("{\"name\":{\"S\":\"Alice\"}}");

        using var expected = ItemHandlers.ItemDocumentBody.Create("k", "p", item, binary);
        using var actual = ItemHandlers.ItemDocumentBody.Create("k", "p", body, item, binary);

        Assert.Equal(expected.Memory.ToArray(), actual.Memory.ToArray());
    }

    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
