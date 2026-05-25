using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Aws2Azure.Modules.DynamoDb.Persistence;

namespace Aws2Azure.UnitTests.DynamoDb.Persistence;

public class InferredAttributeStorageTests
{
    // ---------- helpers ----------

    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static string EncodeDoc(string id, string pk, string itemJson)
    {
        var item = Parse(itemJson);
        return InferredAttributeStorage.BuildCosmosDocument(id, pk, item);
    }

    private static Dictionary<string, JsonElement> Decode(string cosmosDocJson)
    {
        var bytes = Encoding.UTF8.GetBytes(cosmosDocJson);
        using var ms = new MemoryStream(bytes);
        var item = InferredAttributeStorage.ExtractItem(ms);
        Assert.NotNull(item);
        return item!;
    }

    private static string Canon(JsonElement el)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            el.WriteTo(w);
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void RoundTrip(string ddbItemJson, string expectedItemJson)
    {
        var doc = EncodeDoc("id1", "pk1", ddbItemJson);
        var item = Decode(doc);
        var sb = new StringBuilder();
        sb.Append('{');
        bool first = true;
        foreach (var kvp in item)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append('"').Append(kvp.Key).Append("\":").Append(Canon(kvp.Value));
        }
        sb.Append('}');
        var actual = sb.ToString();
        var expected = Canon(Parse(expectedItemJson));
        Assert.Equal(expected, actual);
    }

    // ---------- reserved names ----------

    [Theory]
    [InlineData("id")]
    [InlineData("_a2a_pk")]
    [InlineData("_a2a")]
    [InlineData("_a2a:N")]
    [InlineData("_a2a:custom")]
    [InlineData("_a2a$id")]
    [InlineData("_a2a_attr")]
    public void IsReservedTopLevelName_recognises_reserved(string name)
    {
        Assert.True(InferredAttributeStorage.IsReservedTopLevelName(name));
    }

    [Theory]
    [InlineData("name")]
    [InlineData("pk")]   // freed by /_a2a_pk routing path
    [InlineData("sk")]
    [InlineData("a2a:foo")]
    [InlineData("ID")]
    public void IsReservedTopLevelName_allows_normal_names(string name)
    {
        Assert.False(InferredAttributeStorage.IsReservedTopLevelName(name));
    }

    [Fact]
    public void BuildCosmosDocument_shadow_encodes_id_attribute()
    {
        var item = Parse("{\"id\":{\"S\":\"user-42\"}}");
        var doc = InferredAttributeStorage.BuildCosmosDocument("user-42", "user-42", item);
        // The user's "id" attribute rides under the shadow name; the
        // routing "id" field carries the formatted key.
        Assert.Contains("\"_a2a$id\":\"user-42\"", doc);
    }

    [Fact]
    public void Roundtrip_unmangles_shadow_encoded_id_attribute()
    {
        var item = Parse("{\"id\":{\"S\":\"user-42\"},\"name\":{\"S\":\"Alice\"}}");
        var doc = InferredAttributeStorage.BuildCosmosDocument("user-42", "user-42", item);
        using var parsed = JsonDocument.Parse(doc);
        var decoded = InferredAttributeStorage.ExtractItem(parsed.RootElement);
        Assert.NotNull(decoded);
        Assert.Equal("user-42", decoded!["id"].GetProperty("S").GetString());
        Assert.Equal("Alice", decoded["name"].GetProperty("S").GetString());
    }

    [Fact]
    public void BuildCosmosDocument_rejects_a2a_namespace_attribute()
    {
        // Only the "id" collision is shadow-encoded; any other name in
        // the _a2a namespace is a hard validation failure.
        var item = Parse("{\"_a2a:custom\":{\"S\":\"oops\"}}");
        Assert.Throws<System.ArgumentException>(
            () => InferredAttributeStorage.BuildCosmosDocument("id1", "pk1", item));
    }

    // ---------- single-type round trips ----------

    [Fact]
    public void String_roundtrips_as_bare_value()
    {
        RoundTrip(
            ddbItemJson: "{\"name\":{\"S\":\"Alice\"}}",
            expectedItemJson: "{\"name\":{\"S\":\"Alice\"}}");
        var doc = EncodeDoc("i", "p", "{\"name\":{\"S\":\"Alice\"}}");
        Assert.Contains("\"name\":\"Alice\"", doc);
    }

    [Fact]
    public void Bool_roundtrips_as_bare_value()
    {
        RoundTrip("{\"x\":{\"BOOL\":true}}", "{\"x\":{\"BOOL\":true}}");
        RoundTrip("{\"x\":{\"BOOL\":false}}", "{\"x\":{\"BOOL\":false}}");
        var doc = EncodeDoc("i", "p", "{\"x\":{\"BOOL\":true}}");
        Assert.Contains("\"x\":true", doc);
    }

    [Fact]
    public void Null_roundtrips_as_bare_value()
    {
        RoundTrip("{\"x\":{\"NULL\":true}}", "{\"x\":{\"NULL\":true}}");
        var doc = EncodeDoc("i", "p", "{\"x\":{\"NULL\":true}}");
        Assert.Contains("\"x\":null", doc);
    }

    [Theory]
    [InlineData("42")]
    [InlineData("-42")]
    [InlineData("0")]
    [InlineData("3.14")]
    [InlineData("-0.001")]
    [InlineData("999999999999999999")]
    [InlineData("12345678901234567890123456789")]
    public void Number_small_roundtrips_as_bare_value(string n)
    {
        RoundTrip($"{{\"x\":{{\"N\":\"{n}\"}}}}", $"{{\"x\":{{\"N\":\"{n}\"}}}}");
        var doc = EncodeDoc("i", "p", $"{{\"x\":{{\"N\":\"{n}\"}}}}");
        Assert.Contains($"\"x\":{n}", doc);
    }

    [Theory]
    [InlineData("9999999999999999999999999999999999")] // 34 digits — exceeds decimal precision
    [InlineData("1.7976931348623157E+308")]            // scientific notation — TryFormat emits plain decimal
    [InlineData("1.0E-130")]                            // tiny exponent
    [InlineData("+42")]                                 // leading + — strictly stripped by decimal.ToString
    [InlineData("1e10")]                                // lowercase scientific
    public void Number_big_or_format_sensitive_uses_envelope(string n)
    {
        var doc = EncodeDoc("i", "p", $"{{\"x\":{{\"N\":\"{n}\"}}}}");
        Assert.Contains($"\"x\":{{\"_a2a:N\":\"{n}\"}}", doc);
        RoundTrip($"{{\"x\":{{\"N\":\"{n}\"}}}}", $"{{\"x\":{{\"N\":\"{n}\"}}}}");
    }

    [Theory]
    [InlineData("42.0")]   // decimal scale preserved
    [InlineData("42.00")]  // multiple trailing zeros preserved
    [InlineData("0.10")]   // leading 0 + trailing 0 preserved
    public void Number_with_decimal_trailing_zeros_roundtrips_bare(string n)
    {
        // decimal preserves scale, so these formats survive the bare path.
        var doc = EncodeDoc("i", "p", $"{{\"x\":{{\"N\":\"{n}\"}}}}");
        Assert.Contains($"\"x\":{n}", doc);
        RoundTrip($"{{\"x\":{{\"N\":\"{n}\"}}}}", $"{{\"x\":{{\"N\":\"{n}\"}}}}");
    }

    [Fact]
    public void Binary_uses_envelope()
    {
        const string b64 = "SGVsbG8=";
        var doc = EncodeDoc("i", "p", $"{{\"x\":{{\"B\":\"{b64}\"}}}}");
        Assert.Contains($"\"x\":{{\"_a2a:B\":\"{b64}\"}}", doc);
        RoundTrip($"{{\"x\":{{\"B\":\"{b64}\"}}}}", $"{{\"x\":{{\"B\":\"{b64}\"}}}}");
    }

    [Fact]
    public void StringSet_uses_envelope()
    {
        var doc = EncodeDoc("i", "p", "{\"x\":{\"SS\":[\"a\",\"b\"]}}");
        Assert.Contains("\"x\":{\"_a2a:SS\":[\"a\",\"b\"]}", doc);
        RoundTrip("{\"x\":{\"SS\":[\"a\",\"b\"]}}", "{\"x\":{\"SS\":[\"a\",\"b\"]}}");
    }

    [Fact]
    public void NumberSet_uses_envelope()
    {
        var doc = EncodeDoc("i", "p", "{\"x\":{\"NS\":[\"1\",\"2\",\"3\"]}}");
        Assert.Contains("\"x\":{\"_a2a:NS\":[\"1\",\"2\",\"3\"]}", doc);
        RoundTrip("{\"x\":{\"NS\":[\"1\",\"2\",\"3\"]}}", "{\"x\":{\"NS\":[\"1\",\"2\",\"3\"]}}");
    }

    [Fact]
    public void BinarySet_uses_envelope()
    {
        var doc = EncodeDoc("i", "p", "{\"x\":{\"BS\":[\"AQI=\",\"AwQ=\"]}}");
        Assert.Contains("\"x\":{\"_a2a:BS\":[\"AQI=\",\"AwQ=\"]}", doc);
        RoundTrip("{\"x\":{\"BS\":[\"AQI=\",\"AwQ=\"]}}", "{\"x\":{\"BS\":[\"AQI=\",\"AwQ=\"]}}");
    }

    [Fact]
    public void Map_roundtrips_with_nested_attributes()
    {
        const string item = "{\"profile\":{\"M\":{\"name\":{\"S\":\"Alice\"},\"age\":{\"N\":\"30\"}}}}";
        RoundTrip(item, item);
        var doc = EncodeDoc("i", "p", item);
        Assert.Contains("\"profile\":{\"name\":\"Alice\",\"age\":30}", doc);
    }

    [Fact]
    public void List_roundtrips_with_mixed_types()
    {
        const string item = "{\"vals\":{\"L\":[{\"S\":\"a\"},{\"N\":\"42\"},{\"BOOL\":true},{\"NULL\":true}]}}";
        RoundTrip(item, item);
        var doc = EncodeDoc("i", "p", item);
        Assert.Contains("\"vals\":[\"a\",42,true,null]", doc);
    }

    [Fact]
    public void List_with_set_element_keeps_envelope_inside()
    {
        const string item = "{\"vals\":{\"L\":[{\"SS\":[\"a\",\"b\"]}]}}";
        RoundTrip(item, item);
    }

    [Fact]
    public void Deeply_nested_map_in_list_in_map_roundtrips()
    {
        const string item = "{\"outer\":{\"M\":{\"list\":{\"L\":[{\"M\":{\"deep\":{\"S\":\"v\"}}}]}}}}";
        RoundTrip(item, item);
    }

    // ---------- envelope-vs-map disambiguation ----------

    [Fact]
    public void Map_named_a2a_colon_X_is_rejected_on_encode()
    {
        var item = Parse("{\"x\":{\"M\":{\"_a2a:N\":{\"S\":\"v\"}}}}");
        Assert.Throws<System.ArgumentException>(
            () => InferredAttributeStorage.BuildCosmosDocument("i", "p", item));
    }

    [Fact]
    public void Single_property_map_without_envelope_prefix_decodes_as_M()
    {
        const string item = "{\"profile\":{\"M\":{\"name\":{\"S\":\"Alice\"}}}}";
        RoundTrip(item, item);
        var doc = EncodeDoc("i", "p", item);
        Assert.Contains("\"profile\":{\"name\":\"Alice\"}", doc);
    }

    // ---------- multi-attribute document ----------

    [Fact]
    public void Realistic_item_with_many_types_roundtrips()
    {
        const string item =
            "{" +
            "\"name\":{\"S\":\"Alice\"}," +
            "\"age\":{\"N\":\"30\"}," +
            "\"active\":{\"BOOL\":true}," +
            "\"deleted\":{\"NULL\":true}," +
            "\"tags\":{\"SS\":[\"x\",\"y\"]}," +
            "\"blob\":{\"B\":\"AQID\"}," +
            "\"profile\":{\"M\":{\"city\":{\"S\":\"SP\"}}}," +
            "\"scores\":{\"L\":[{\"N\":\"1\"},{\"N\":\"2\"}]}," +
            "\"big\":{\"N\":\"1.7976931348623157E+308\"}" +
            "}";
        RoundTrip(item, item);
    }

    // ---------- doc shape ----------

    [Fact]
    public void Cosmos_doc_contains_routing_and_discriminator()
    {
        var doc = EncodeDoc("the-id", "the-pk", "{\"name\":{\"S\":\"Alice\"}}");
        Assert.Contains("\"id\":\"the-id\"", doc);
        Assert.Contains("\"_a2a_pk\":\"the-pk\"", doc);
        Assert.Contains("\"_a2a\":\"item\"", doc);
        Assert.Contains("\"name\":\"Alice\"", doc);
    }

    [Fact]
    public void Reserved_props_in_cosmos_doc_are_skipped_on_extract()
    {
        const string doc =
            "{\"id\":\"i\",\"_a2a_pk\":\"p\",\"_a2a\":\"item\",\"_a2a:internal\":\"xyz\",\"name\":\"Bob\"}";
        var item = Decode(doc);
        Assert.Single(item);
        Assert.True(item.ContainsKey("name"));
    }

    // ---------- decimal boundary ----------

    [Theory]
    [InlineData("79228162514264337593543950335")]
    [InlineData("-79228162514264337593543950335")]
    public void Number_at_decimal_max_fits_bare(string n)
    {
        var doc = EncodeDoc("i", "p", $"{{\"x\":{{\"N\":\"{n}\"}}}}");
        Assert.Contains($"\"x\":{n}", doc);
    }

    [Fact]
    public void Number_exceeding_decimal_uses_envelope()
    {
        const string n = "79228162514264337593543950336";
        var doc = EncodeDoc("i", "p", $"{{\"x\":{{\"N\":\"{n}\"}}}}");
        Assert.Contains($"\"x\":{{\"_a2a:N\":\"{n}\"}}", doc);
    }
}
