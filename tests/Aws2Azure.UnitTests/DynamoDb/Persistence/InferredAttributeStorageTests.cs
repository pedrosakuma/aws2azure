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
    // Already-canonical inputs round-trip byte-identical.
    [InlineData("42", "42")]
    [InlineData("-42", "-42")]
    [InlineData("0", "0")]
    [InlineData("3.14", "3.14")]
    [InlineData("-0.001", "-0.001")]
    [InlineData("999999999999999", "999999999999999")]  // 15 sig digits — bare boundary
    // DDB-normalised: trailing zeros stripped, exponent expanded,
    // leading zeros / +0 / -0 collapsed (matches real DDB behaviour).
    [InlineData("42.0", "42")]
    [InlineData("42.00", "42")]
    [InlineData("0.10", "0.1")]
    [InlineData("01", "1")]
    [InlineData("00", "0")]
    [InlineData("-0", "0")]
    [InlineData("-0.0", "0")]
    [InlineData("+42", "42")]
    [InlineData("1e3", "1000")]
    [InlineData("1.5e2", "150")]
    [InlineData("5e-3", "0.005")]
    [InlineData("-1.23e2", "-123")]
    public void Number_small_normalises_and_roundtrips_bare(string input, string canonical)
    {
        // Encoder writes the canonical form bare in the Cosmos doc.
        var doc = EncodeDoc("i", "p", $"{{\"x\":{{\"N\":\"{input}\"}}}}");
        Assert.Contains($"\"x\":{canonical}", doc);
        Assert.DoesNotContain("_a2a:N", doc);
        // Decoder echoes whatever Cosmos stored — i.e. the canonical form.
        RoundTrip($"{{\"x\":{{\"N\":\"{input}\"}}}}", $"{{\"x\":{{\"N\":\"{canonical}\"}}}}");
    }

    [Theory]
    // 16+ significant digits — bare path would lose precision through
    // Cosmos's double-precision JSON number storage, so envelope as string.
    [InlineData("9999999999999999", "9999999999999999")]                       // 16 sig digits
    [InlineData("12345678901234567890123456789", "12345678901234567890123456789")] // 29 sig digits
    [InlineData("1.234567890123456789", "1.234567890123456789")]                // 19 sig digits, fraction
    [InlineData("1e30", "1000000000000000000000000000000")]                     // expanded then envelope (only 1 sig digit, but magnitude 30)
    [InlineData("1.5e20", "150000000000000000000")]                             // 2 sig digits + huge magnitude
    public void Number_high_precision_or_magnitude_envelopes(string input, string canonical)
    {
        var doc = EncodeDoc("i", "p", $"{{\"x\":{{\"N\":\"{input}\"}}}}");
        Assert.Contains($"\"x\":{{\"_a2a:N\":\"{canonical}\"}}", doc);
        RoundTrip($"{{\"x\":{{\"N\":\"{input}\"}}}}", $"{{\"x\":{{\"N\":\"{canonical}\"}}}}");
    }

    [Theory]
    // DDB's published bounds: ≤38 significant digits, magnitude in
    // [1e-130, 9.99...e+125]. Outside that → ValidationException.
    [InlineData("999999999999999999999999999999999999999")]   // 39 sig digits
    [InlineData("1e126")]                                       // magnitude over 1e125
    [InlineData("1e-131")]                                      // magnitude below 1e-130
    [InlineData("9.9e-131")]                                    // multi-digit, magnitude below 1e-130 (msdExp -131)
    [InlineData("")]
    [InlineData("-")]
    [InlineData(".")]
    [InlineData("1.")]
    [InlineData(".5")]
    [InlineData("1e")]
    [InlineData("abc")]
    [InlineData("1.2.3")]
    public void Number_outside_ddb_range_throws(string input)
    {
        Assert.Throws<ArgumentException>(() =>
            EncodeDoc("i", "p", $"{{\"x\":{{\"N\":\"{input}\"}}}}"));
    }

    [Theory]
    [InlineData("0", "0", 1)]
    [InlineData("42", "42", 2)]
    [InlineData("-42", "-42", 2)]
    [InlineData("42.0", "42", 2)]
    [InlineData("0.10", "0.1", 1)]
    [InlineData("01", "1", 1)]
    [InlineData("-0", "0", 1)]
    [InlineData("1e3", "1000", 1)]
    [InlineData("1.5e2", "150", 2)]
    [InlineData("5e-3", "0.005", 1)]
    [InlineData("-1.23e2", "-123", 3)]
    [InlineData("1000", "1000", 1)]
    [InlineData("0.5", "0.5", 1)]
    public void Normaliser_emits_canonical_form_and_significant_digit_count(
        string input, string expected, int expectedSig)
    {
        Assert.True(InferredAttributeStorage.TryNormalizeDdbNumber(
            input, out var actual, out var sig, out _));
        Assert.Equal(expected, actual);
        Assert.Equal(expectedSig, sig);
    }

    [Theory]
    // Regression for #189: DynamoDB's lower bound is a *magnitude* (MSD)
    // constraint on the value, not a constraint on the least-significant-digit
    // position. A value with magnitude >= 1e-130 and more than one significant
    // digit near the floor (so its LSD sits below 1e-130) is accepted by real
    // DynamoDB — the proxy previously rejected it.
    [InlineData("1.1e-130", 129, "11")]
    [InlineData("1.5e-130", 129, "15")]
    [InlineData("9.9e-130", 129, "99")]
    [InlineData("2.5e-130", 129, "25")]
    public void Number_at_magnitude_floor_with_sub_floor_lsd_is_accepted(
        string input, int leadingZeros, string mantissa)
    {
        string expected = "0." + new string('0', leadingZeros) + mantissa;
        Assert.True(
            InferredAttributeStorage.TryNormalizeDdbNumber(input, out var actual, out _, out var error),
            error);
        Assert.Equal(expected, actual);

        // Full encode/decode path must accept and round-trip it (envelope path,
        // since the long fraction is not exactly double-representable).
        RoundTrip($"{{\"x\":{{\"N\":\"{input}\"}}}}", $"{{\"x\":{{\"N\":\"{expected}\"}}}}");
    }

    [Fact]
    public void Number_38_significant_digits_at_magnitude_floor_is_accepted()
    {
        // 38 significant digits with MSD at 1e-130 → LSD at 1e-167, far below
        // the floor, but the value's magnitude is exactly at the floor. Real
        // DynamoDB accepts (38-digit precision, magnitude >= 1e-130).
        string mantissa = "1" + new string('0', 36) + "1"; // 38 digits: 1...01
        string input = "1." + new string('0', 36) + "1e-130";
        string expected = "0." + new string('0', 129) + mantissa;
        Assert.True(
            InferredAttributeStorage.TryNormalizeDdbNumber(input, out var actual, out var sig, out var error),
            error);
        Assert.Equal(expected, actual);
        Assert.Equal(38, sig);
        RoundTrip($"{{\"x\":{{\"N\":\"{input}\"}}}}", $"{{\"x\":{{\"N\":\"{expected}\"}}}}");
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
            // 1.7976931348623157E+308 is outside DynamoDB's published
            // numeric range (|exp| <= 125). Use a value inside the
            // band that still exceeds the bare ≤15 sig-digit budget
            // and therefore exercises the envelope path.
            "\"big\":{\"N\":\"1.23456789012345678901234567890123456E+120\"}" +
            "}";
        string expected =
            "{" +
            "\"name\":{\"S\":\"Alice\"}," +
            "\"age\":{\"N\":\"30\"}," +
            "\"active\":{\"BOOL\":true}," +
            "\"deleted\":{\"NULL\":true}," +
            "\"tags\":{\"SS\":[\"x\",\"y\"]}," +
            "\"blob\":{\"B\":\"AQID\"}," +
            "\"profile\":{\"M\":{\"city\":{\"S\":\"SP\"}}}," +
            "\"scores\":{\"L\":[{\"N\":\"1\"},{\"N\":\"2\"}]}," +
            "\"big\":{\"N\":\"123456789012345678901234567890123456" +
            new string('0', 120 - 35) + "\"}" +
            "}";
        RoundTrip(item, expected);
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
    // 29 sig digits exceeds the ≤15 bare budget → envelope.
    [InlineData("79228162514264337593543950335")]
    [InlineData("-79228162514264337593543950335")]
    public void Number_at_decimal_max_uses_envelope(string n)
    {
        var doc = EncodeDoc("i", "p", $"{{\"x\":{{\"N\":\"{n}\"}}}}");
        Assert.Contains($"\"x\":{{\"_a2a:N\":\"{n}\"}}", doc);
    }

    [Fact]
    public void Number_exceeding_decimal_uses_envelope()
    {
        const string n = "79228162514264337593543950336";
        var doc = EncodeDoc("i", "p", $"{{\"x\":{{\"N\":\"{n}\"}}}}");
        Assert.Contains($"\"x\":{{\"_a2a:N\":\"{n}\"}}", doc);
    }
}
