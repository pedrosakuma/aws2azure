using System;
using System.Buffers;
using System.Text;
using System.Text.Json;
using Aws2Azure.Modules.DynamoDb.Operations;
using Aws2Azure.Modules.DynamoDb.Persistence;
using Xunit;

namespace Aws2Azure.UnitTests.DynamoDb.Persistence;

/// <summary>
/// Golden byte-equivalence tests for the allocation-lean GetItem response
/// path (<see cref="InferredAttributeStorage.WriteGetItemEnvelope"/>) against
/// the current materialized path
/// (<c>ExtractItem → GetItemResponse model → JsonSerializer</c>). The two
/// MUST produce byte-identical output for every input, otherwise the
/// optimization would be an observable behavior change.
///
/// The corpus deliberately exercises the escaping drift the design review
/// flagged: non-ASCII, HTML-sensitive (&lt; &gt; &amp; +), emoji, quotes and
/// control characters. The current path terminates in
/// <c>JavaScriptEncoder.Default</c>; the new writer must too.
/// </summary>
public class GetItemEnvelopeGoldenTests
{
    /// <summary>Current (materialized) path: exactly what the handler emits.</summary>
    private static byte[] CurrentPath(JsonElement cosmosDocRoot)
    {
        var item = InferredAttributeStorage.ExtractItem(cosmosDocRoot);
        var response = new GetItemResponse { Item = item };
        return JsonSerializer.SerializeToUtf8Bytes(response, ItemJsonContext.Default.GetItemResponse);
    }

    /// <summary>New (model-elimination) path under test.</summary>
    private static byte[] NewPath(JsonElement cosmosDocRoot)
    {
        var bw = new ArrayBufferWriter<byte>(256);
        // Default encoder (no override) to match the serializer's escaping.
        using (var writer = new Utf8JsonWriter(bw, new JsonWriterOptions { SkipValidation = false }))
        {
            InferredAttributeStorage.WriteGetItemEnvelope(writer, cosmosDocRoot);
        }
        return bw.WrittenSpan.ToArray();
    }

    /// <summary>Streaming (Utf8JsonReader → Utf8JsonWriter) path under test.</summary>
    private static byte[] NewPathStreaming(byte[] cosmosDocUtf8)
    {
        var bw = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(bw, new JsonWriterOptions { SkipValidation = false }))
        {
            InferredAttributeStorage.WriteGetItemEnvelope(writer, cosmosDocUtf8.AsSpan());
        }
        return bw.WrittenSpan.ToArray();
    }

    /// <summary>
    /// Encodes a DDB item map to a Cosmos document via the real write path,
    /// then parses it back so the golden assertion runs over a document with
    /// the exact shape the proxy persists.
    /// </summary>
    private static void AssertEquivalent(string id, string pk, string ddbItemJson)
    {
        using var itemDoc = JsonDocument.Parse(ddbItemJson);
        var cosmosJson = InferredAttributeStorage.BuildCosmosDocument(id, pk, itemDoc.RootElement);

        using var cosmosDoc = JsonDocument.Parse(cosmosJson);
        var root = cosmosDoc.RootElement;

        var expected = CurrentPath(root);
        var actual = NewPath(root);

        Assert.Equal(
            Encoding.UTF8.GetString(expected),
            Encoding.UTF8.GetString(actual));
        Assert.True(expected.AsSpan().SequenceEqual(actual), "byte sequences differ");

        // The streaming pump must be byte-identical to the same oracle.
        var streaming = NewPathStreaming(Encoding.UTF8.GetBytes(cosmosJson));
        Assert.Equal(
            Encoding.UTF8.GetString(expected),
            Encoding.UTF8.GetString(streaming));
        Assert.True(expected.AsSpan().SequenceEqual(streaming), "streaming byte sequences differ");
    }

    [Theory]
    // Plain scalars.
    [InlineData("{\"pk\":{\"S\":\"p\"},\"name\":{\"S\":\"alice\"}}")]
    [InlineData("{\"pk\":{\"S\":\"p\"},\"age\":{\"N\":\"30\"}}")]
    [InlineData("{\"pk\":{\"S\":\"p\"},\"flag\":{\"BOOL\":true}}")]
    [InlineData("{\"pk\":{\"S\":\"p\"},\"flag\":{\"BOOL\":false}}")]
    [InlineData("{\"pk\":{\"S\":\"p\"},\"nothing\":{\"NULL\":true}}")]
    // Escaping drift surface: non-ASCII, HTML-sensitive, emoji, quotes, control.
    [InlineData("{\"pk\":{\"S\":\"p\"},\"s\":{\"S\":\"caf\\u00e9\"}}")]
    [InlineData("{\"pk\":{\"S\":\"p\"},\"s\":{\"S\":\"a<b>&c+d=e\"}}")]
    [InlineData("{\"pk\":{\"S\":\"p\"},\"s\":{\"S\":\"emoji \\ud83d\\ude00 x\"}}")]
    [InlineData("{\"pk\":{\"S\":\"p\"},\"s\":{\"S\":\"quote\\\"and\\\\slash\"}}")]
    [InlineData("{\"pk\":{\"S\":\"p\"},\"s\":{\"S\":\"line\\nbreak\\ttab\"}}")]
    // Pre-escaped ASCII must normalize ("\u0061" → "a"), raw control char,
    // and CJK — extra parity pins for the WriteTo raw-copy path.
    [InlineData("{\"pk\":{\"S\":\"p\"},\"s\":{\"S\":\"\\u0061bc\"}}")]
    [InlineData("{\"pk\":{\"S\":\"p\"},\"s\":{\"S\":\"ctrl\\u0001end\"}}")]
    [InlineData("{\"pk\":{\"S\":\"p\"},\"s\":{\"S\":\"\\u65e5\\u672c\\u8a9e\"}}")]
    // High-precision number → forces the _a2a:N envelope round-trip.
    [InlineData("{\"pk\":{\"S\":\"p\"},\"big\":{\"N\":\"99999999999999999999999999999999999999\"}}")]
    [InlineData("{\"pk\":{\"S\":\"p\"},\"frac\":{\"N\":\"0.0000000000000000000000000001\"}}")]
    // Binary and sets → always-enveloped types.
    [InlineData("{\"pk\":{\"S\":\"p\"},\"bin\":{\"B\":\"aGVsbG8=\"}}")]
    [InlineData("{\"pk\":{\"S\":\"p\"},\"ss\":{\"SS\":[\"a\",\"b\",\"caf\\u00e9\"]}}")]
    [InlineData("{\"pk\":{\"S\":\"p\"},\"ns\":{\"NS\":[\"1\",\"2\",\"3\"]}}")]
    [InlineData("{\"pk\":{\"S\":\"p\"},\"bs\":{\"BS\":[\"aGVsbG8=\",\"d29ybGQ=\"]}}")]
    // Nested map and list.
    [InlineData("{\"pk\":{\"S\":\"p\"},\"m\":{\"M\":{\"k\":{\"S\":\"v\"},\"n\":{\"N\":\"7\"}}}}")]
    [InlineData("{\"pk\":{\"S\":\"p\"},\"l\":{\"L\":[{\"S\":\"x\"},{\"N\":\"1\"},{\"BOOL\":true},{\"NULL\":true}]}}")]
    // Attribute literally named "id" → shadow-encoded then unmangled.
    [InlineData("{\"pk\":{\"S\":\"p\"},\"id\":{\"S\":\"user-123\"}}")]
    // Deeply nested mix.
    [InlineData("{\"pk\":{\"S\":\"p\"},\"doc\":{\"M\":{\"items\":{\"L\":[{\"M\":{\"q\":{\"N\":\"42\"}}}]},\"tag\":{\"S\":\"a&b\"}}}}")]
    public void New_path_is_byte_identical_to_current(string ddbItemJson)
    {
        AssertEquivalent("sortKey", "p", ddbItemJson);
    }

    [Fact]
    public void Non_object_root_collapses_to_empty_object()
    {
        using var doc = JsonDocument.Parse("\"not-an-object\"");
        var actual = NewPath(doc.RootElement);
        Assert.Equal("{}", Encoding.UTF8.GetString(actual));

        var streaming = NewPathStreaming(Encoding.UTF8.GetBytes("\"not-an-object\""));
        Assert.Equal("{}", Encoding.UTF8.GetString(streaming));
    }
}
