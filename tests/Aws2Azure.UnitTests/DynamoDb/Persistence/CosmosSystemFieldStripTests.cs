using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using Aws2Azure.Modules.DynamoDb.Persistence;
using Xunit;

namespace Aws2Azure.UnitTests.DynamoDb.Persistence;

/// <summary>
/// Regression guard for #203: Cosmos injects storage-metadata system fields
/// (<c>_rid</c>, <c>_self</c>, <c>_etag</c>, <c>_ts</c>, <c>_attachments</c>,
/// and on some response shapes <c>_lsn</c>/<c>_metadata</c>) at the document
/// root of every read body. They are NOT user attributes and must never
/// surface in a DynamoDB read response. The Cosmos emulator never injects
/// them, so emulator-backed tests could not catch the leak — these tests
/// synthesize a real-Azure-shaped document and pin the strip across all three
/// decode sites (DOM GetItem, streaming GetItem, and the
/// <c>ExtractItem</c> path shared by Query/Scan/BatchGet/sproc).
/// </summary>
public class CosmosSystemFieldStripTests
{
    private static readonly string[] SystemFields =
        { "_rid", "_self", "_etag", "_ts", "_attachments", "_lsn", "_metadata" };

    /// <summary>
    /// Encodes the user portion via the real write path, then splices Cosmos
    /// system fields into the document root — some leading (right after the
    /// opening brace), some trailing — so the strip is exercised regardless of
    /// property order in the forward-only streaming reader.
    /// </summary>
    private static string CosmosDocWithSystemFields(string ddbItemJson, string id = "sk", string pk = "p")
    {
        using var itemDoc = JsonDocument.Parse(ddbItemJson);
        var cosmosJson = InferredAttributeStorage.BuildCosmosDocument(id, pk, itemDoc.RootElement);

        const string leading =
            "\"_rid\":\"AhQ1APiVm14BAAAAAAAAAA==\"," +
            "\"_self\":\"dbs/AhQ1AA==/colls/AhQ1APiVm14=/docs/AhQ1APiVm14BAAAAAAAAAA==/\",";
        const string trailing =
            ",\"_etag\":\"\\\"00000000-0000-0000-0000-000000000000\\\"\"," +
            "\"_attachments\":\"attachments/\"," +
            "\"_ts\":1717000000," +
            "\"_lsn\":42," +
            "\"_metadata\":{\"nested\":{\"x\":1}}";

        var withLeading = "{" + leading + cosmosJson.Substring(1);
        return withLeading.Substring(0, withLeading.Length - 1) + trailing + "}";
    }

    private static byte[] DomEnvelope(JsonElement cosmosDocRoot)
    {
        var bw = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(bw))
        {
            InferredAttributeStorage.WriteGetItemEnvelope(writer, cosmosDocRoot);
        }
        return bw.WrittenSpan.ToArray();
    }

    private static byte[] StreamingEnvelope(byte[] cosmosDocUtf8)
    {
        var bw = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(bw))
        {
            InferredAttributeStorage.WriteGetItemEnvelope(writer, cosmosDocUtf8.AsSpan());
        }
        return bw.WrittenSpan.ToArray();
    }

    private static HashSet<string> EnvelopeItemKeys(byte[] envelopeUtf8)
    {
        using var doc = JsonDocument.Parse(envelopeUtf8);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        if (doc.RootElement.TryGetProperty("Item", out var item) && item.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in item.EnumerateObject())
                keys.Add(p.Name);
        }
        return keys;
    }

    [Fact]
    public void System_fields_are_stripped_from_all_read_paths()
    {
        var cosmosJson = CosmosDocWithSystemFields(
            "{\"pk\":{\"S\":\"p\"},\"name\":{\"S\":\"alice\"},\"age\":{\"N\":\"30\"}}");
        using var doc = JsonDocument.Parse(cosmosJson);
        var root = doc.RootElement;

        // 1. ExtractItem — Query / Scan / BatchGet / TransactGet / sproc ReturnValues.
        var item = InferredAttributeStorage.ExtractItem(root);
        Assert.NotNull(item);
        foreach (var sys in SystemFields)
            Assert.False(item!.ContainsKey(sys), $"ExtractItem leaked Cosmos system field '{sys}'");
        Assert.True(item!.ContainsKey("name"), "ExtractItem dropped user attribute 'name'");
        Assert.True(item.ContainsKey("age"), "ExtractItem dropped user attribute 'age'");

        // 2. DOM WriteGetItemEnvelope — GetItem.
        var domKeys = EnvelopeItemKeys(DomEnvelope(root));
        foreach (var sys in SystemFields)
            Assert.DoesNotContain(sys, domKeys);
        Assert.Contains("name", domKeys);
        Assert.Contains("age", domKeys);

        // 3. Streaming WriteGetItemEnvelope — GetItem allocation-lean fast path.
        var streamingBytes = StreamingEnvelope(Encoding.UTF8.GetBytes(cosmosJson));
        var streamingKeys = EnvelopeItemKeys(streamingBytes);
        foreach (var sys in SystemFields)
            Assert.DoesNotContain(sys, streamingKeys);
        Assert.Contains("name", streamingKeys);
        Assert.Contains("age", streamingKeys);

        // DOM and streaming envelopes must remain byte-identical to each other.
        Assert.True(
            DomEnvelope(root).AsSpan().SequenceEqual(streamingBytes),
            "DOM and streaming GetItem envelopes diverged after the system-field strip");
    }

    [Fact]
    public void Nested_system_field_names_are_preserved()
    {
        // Only the document root carries Cosmos metadata. A user map attribute
        // whose nested key happens to collide with a system-field name is real
        // user data and must round-trip untouched.
        var cosmosJson = CosmosDocWithSystemFields(
            "{\"pk\":{\"S\":\"p\"},\"m\":{\"M\":{\"_etag\":{\"S\":\"keep\"},\"_rid\":{\"N\":\"7\"}}}}");
        using var doc = JsonDocument.Parse(cosmosJson);

        var item = InferredAttributeStorage.ExtractItem(doc.RootElement);
        Assert.NotNull(item);
        Assert.False(item!.ContainsKey("_etag"), "root '_etag' should be stripped");
        Assert.False(item.ContainsKey("_rid"), "root '_rid' should be stripped");
        Assert.True(item.ContainsKey("m"), "user map attribute 'm' was dropped");

        var map = item["m"].GetProperty("M");
        Assert.True(map.TryGetProperty("_etag", out var nestedEtag));
        Assert.Equal("keep", nestedEtag.GetProperty("S").GetString());
        Assert.True(map.TryGetProperty("_rid", out var nestedRid));
        Assert.Equal("7", nestedRid.GetProperty("N").GetString());
    }

    [Fact]
    public void Write_path_still_accepts_user_attribute_named_like_a_system_field()
    {
        // #203 is a read-only strip (accepted caveat): the encoder must NOT
        // start rejecting a top-level user attribute literally named "_etag".
        // It is written verbatim; the read paths above strip it on the way out.
        using var itemDoc = JsonDocument.Parse("{\"pk\":{\"S\":\"p\"},\"_etag\":{\"S\":\"v\"}}");
        var ex = Record.Exception(
            () => InferredAttributeStorage.BuildCosmosDocument("sk", "p", itemDoc.RootElement));
        Assert.Null(ex);
    }
}
