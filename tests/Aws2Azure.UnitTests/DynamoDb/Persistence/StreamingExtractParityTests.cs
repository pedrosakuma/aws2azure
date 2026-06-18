using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using Aws2Azure.Modules.DynamoDb.Internal;
using Aws2Azure.Modules.DynamoDb.Persistence;
using Xunit;

namespace Aws2Azure.UnitTests.DynamoDb.Persistence;

/// <summary>
/// Parity guard for issue #431: the text read-materialization path was switched
/// from a whole-page <see cref="JsonDocument"/> DOM
/// (<c>JsonDocument.Parse → ExtractItem(JsonElement)</c>) to the same
/// reader-agnostic streaming extractor the CosmosBinary path already used
/// (<see cref="InferredAttributeStorage.ExtractItemsFused"/> /
/// <see cref="InferredAttributeStorage.ExtractItemFused"/> driven off a
/// <see cref="Utf8JsonTokenReader"/>). The streaming walk must produce
/// AttributeValue maps element-for-element identical to the legacy DOM walk —
/// otherwise the (now-unused-in-production) DOM extractor and the streaming one
/// have diverged. These tests pin that equivalence over a representative Cosmos
/// feed page: Cosmos system fields, shadow-encoded id, and every envelope
/// type (N / B / SS / NS / BS), plus nested maps/lists.
/// </summary>
public class StreamingExtractParityTests
{
    // A DDB item exercising every encoding branch the extractor strips/unwraps.
    private const string SampleItem = """
        {
          "id":      { "S": "real-id-value" },
          "name":    { "S": "alice" },
          "age":     { "N": "30" },
          "score":   { "N": "12345678901234567890.0001" },
          "active":  { "BOOL": true },
          "absent":  { "NULL": true },
          "blob":    { "B": "aGVsbG8=" },
          "tags":    { "SS": ["x", "y", "z"] },
          "nums":    { "NS": ["1", "2", "3"] },
          "blobs":   { "BS": ["aGk=", "Ynll"] },
          "nested":  { "M": { "inner": { "S": "deep" }, "n": { "N": "7" } } },
          "mixed":   { "L": [ { "S": "a" }, { "N": "1" }, { "BOOL": false } ] }
        }
        """;

    private static string BuildCosmosDoc(string sortId, string pk, int sysSeed)
    {
        using var item = JsonDocument.Parse(SampleItem);
        var stored = InferredAttributeStorage.BuildCosmosDocument(sortId, pk, item.RootElement);

        // Splice in the Cosmos system fields a real feed/query response carries
        // (_rid/_self/_etag/_attachments/_ts) so the parity covers system-field
        // stripping, not just the user attributes.
        using var doc = JsonDocument.Parse(stored);

        // Re-emit the object with system fields appended.
        using var ms = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                prop.WriteTo(writer);
            }
            writer.WriteString("_rid", $"rid{sysSeed}==");
            writer.WriteString("_self", $"dbs/x/colls/y/docs/rid{sysSeed}==/");
            writer.WriteString("_etag", $"\"0000{sysSeed}-0000-0000\"");
            writer.WriteString("_attachments", "attachments/");
            writer.WriteNumber("_ts", 1_700_000_000 + sysSeed);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static string BuildPage(int docCount)
    {
        var sb = new StringBuilder();
        sb.Append("{\"_rid\":\"feedrid==\",\"Documents\":[");
        for (int i = 0; i < docCount; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(BuildCosmosDoc($"sk{i}", "p", i));
        }
        sb.Append("],\"_count\":").Append(docCount).Append('}');
        return sb.ToString();
    }

    private static string Canon(JsonElement el)
    {
        using var ms = new System.IO.MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            el.WriteTo(w);
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static string Canon(Dictionary<string, JsonElement> map)
    {
        // Insertion order is preserved identically by both extractors (both walk
        // the document's properties in order), so a plain join is a faithful
        // order-sensitive comparison.
        var sb = new StringBuilder();
        sb.Append('{');
        bool first = true;
        foreach (var kvp in map)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append('"').Append(kvp.Key).Append("\":").Append(Canon(kvp.Value));
        }
        sb.Append('}');
        return sb.ToString();
    }

    /// <summary>Legacy whole-page DOM extraction (the pre-#431 production path).</summary>
    private static List<Dictionary<string, JsonElement>> ExtractViaDom(string pageJson)
    {
        var sink = new List<Dictionary<string, JsonElement>>();
        using var doc = JsonDocument.Parse(pageJson);
        if (doc.RootElement.TryGetProperty("Documents", out var docsEl)
            && docsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var docEl in docsEl.EnumerateArray())
            {
                var item = InferredAttributeStorage.ExtractItem(docEl);
                if (item is not null) sink.Add(item);
            }
        }
        return sink;
    }

    /// <summary>New streaming extraction (the post-#431 production path).</summary>
    private static List<Dictionary<string, JsonElement>> ExtractViaStreaming(string pageJson)
    {
        var sink = new List<Dictionary<string, JsonElement>>();
        var reader = new Utf8JsonTokenReader(Encoding.UTF8.GetBytes(pageJson));
        InferredAttributeStorage.ExtractItemsFused(ref reader, sink);
        return sink;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(10)]
    public void Streaming_multi_item_matches_dom(int docCount)
    {
        string page = BuildPage(docCount);

        var dom = ExtractViaDom(page);
        var streaming = ExtractViaStreaming(page);

        Assert.Equal(dom.Count, streaming.Count);
        for (int i = 0; i < dom.Count; i++)
        {
            Assert.Equal(Canon(dom[i]), Canon(streaming[i]));
        }
    }

    [Fact]
    public void Streaming_single_item_matches_dom()
    {
        string docJson = BuildCosmosDoc("sk0", "p", 0);

        using var doc = JsonDocument.Parse(docJson);
        var dom = InferredAttributeStorage.ExtractItem(doc.RootElement);
        Assert.NotNull(dom);

        var reader = new Utf8JsonTokenReader(Encoding.UTF8.GetBytes(docJson));
        var streaming = InferredAttributeStorage.ExtractItemFused(ref reader);
        Assert.NotNull(streaming);

        Assert.Equal(Canon(dom!), Canon(streaming!));
    }

    [Fact]
    public void Streaming_empty_documents_array_yields_no_items()
    {
        var streaming = ExtractViaStreaming("{\"Documents\":[],\"_count\":0}");
        Assert.Empty(streaming);
    }
}
