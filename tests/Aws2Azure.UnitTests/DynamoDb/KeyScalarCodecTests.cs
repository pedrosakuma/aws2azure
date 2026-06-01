using System;
using System.Text;
using System.Text.Json;
using Aws2Azure.Modules.DynamoDb.Operations;
using Xunit;

namespace Aws2Azure.UnitTests.DynamoDb;

public class KeyScalarCodecTests
{
    private static ParsedAttributeValue Parse(string typedJson)
    {
        using var doc = JsonDocument.Parse(typedJson);
        Assert.True(ParsedAttributeValue.TryParse(doc.RootElement.Clone(), out var parsed));
        return parsed;
    }

    private static string Encode(string declaredType, string typedJson)
    {
        Assert.True(
            KeyScalarCodec.TryEncode(declaredType, Parse(typedJson), "k", out var encoded, out var error),
            error);
        return encoded;
    }

    private static (bool ok, string error) TryEncode(string declaredType, string typedJson)
    {
        var ok = KeyScalarCodec.TryEncode(declaredType, Parse(typedJson), "k", out _, out var error);
        return (ok, error);
    }

    [Fact]
    public void String_is_lowercase_hex_of_utf8()
    {
        Assert.Equal("61", Encode("S", "{\"S\":\"a\"}"));
        Assert.Equal(
            Convert.ToHexStringLower(Encoding.UTF8.GetBytes("order-42")),
            Encode("S", "{\"S\":\"order-42\"}"));
    }

    [Fact]
    public void String_multibyte_uses_utf8_bytes()
    {
        // 'é' is 0xC3 0xA9 in UTF-8.
        Assert.Equal("c3a9", Encode("S", "{\"S\":\"\u00e9\"}"));
    }

    [Fact]
    public void Binary_is_lowercase_hex_of_decoded_bytes()
    {
        // base64("a") == "YQ==" -> byte 0x61.
        Assert.Equal("61", Encode("B", "{\"B\":\"YQ==\"}"));
        // 0xFF 0x00 -> "/wA=" in base64.
        Assert.Equal("ff00", Encode("B", "{\"B\":\"/wA=\"}"));
    }

    [Fact]
    public void Number_is_passthrough()
    {
        Assert.Equal("42", Encode("N", "{\"N\":\"42\"}"));
        Assert.Equal("42.0", Encode("N", "{\"N\":\"42.0\"}"));
    }

    [Theory]
    [InlineData("0f", "10")] // byte 0x0F < 0x10
    [InlineData("39", "61")] // '9' < 'a'
    [InlineData("61", "6162")] // prefix sorts before extension
    [InlineData("80", "ff")] // high bytes preserved unsigned
    public void Hex_encoding_is_order_preserving(string lower, string higher)
    {
        Assert.True(string.CompareOrdinal(lower, higher) < 0);
    }

    [Fact]
    public void String_order_matches_source_byte_order()
    {
        var a = Encode("S", "{\"S\":\"a\"}");
        var ab = Encode("S", "{\"S\":\"ab\"}");
        var b = Encode("S", "{\"S\":\"b\"}");
        Assert.True(string.CompareOrdinal(a, ab) < 0);
        Assert.True(string.CompareOrdinal(ab, b) < 0);
    }

    [Fact]
    public void String_begins_with_is_prefix_preserving()
    {
        var prefix = Encode("S", "{\"S\":\"ord\"}");
        var full = Encode("S", "{\"S\":\"order\"}");
        Assert.StartsWith(prefix, full, StringComparison.Ordinal);
    }

    [Fact]
    public void Binary_order_matches_unsigned_byte_order()
    {
        // 0x00 must sort before 0xFF (base64: "AA==" vs "/w==").
        var low = Encode("B", "{\"B\":\"AA==\"}");
        var high = Encode("B", "{\"B\":\"/w==\"}");
        Assert.Equal("00", low);
        Assert.Equal("ff", high);
        Assert.True(string.CompareOrdinal(low, high) < 0);
    }

    [Fact]
    public void Type_mismatch_is_rejected()
    {
        var (ok, error) = TryEncode("S", "{\"B\":\"YQ==\"}");
        Assert.False(ok);
        Assert.Contains("type B", error);
    }

    [Fact]
    public void Number_for_binary_declared_key_is_rejected()
    {
        var (ok, _) = TryEncode("B", "{\"N\":\"1\"}");
        Assert.False(ok);
    }

    [Fact]
    public void Invalid_base64_binary_is_rejected()
    {
        var (ok, error) = TryEncode("B", "{\"B\":\"not base64!!\"}");
        Assert.False(ok);
        Assert.Contains("base64", error);
    }

    [Fact]
    public void Empty_string_is_rejected()
    {
        var (ok, error) = TryEncode("S", "{\"S\":\"\"}");
        Assert.False(ok);
        Assert.Contains("empty", error);
    }

    [Fact]
    public void Empty_binary_is_rejected()
    {
        var (ok, _) = TryEncode("B", "{\"B\":\"\"}");
        Assert.False(ok);
    }

    [Fact]
    public void Number_with_forbidden_char_is_rejected()
    {
        var (ok, _) = TryEncode("N", "{\"N\":\"1/2\"}");
        Assert.False(ok);
    }

    [Fact]
    public void Encoded_value_exceeding_cosmos_id_limit_is_rejected()
    {
        // 128 chars -> 256 hex chars, over the 255 limit.
        var (ok, error) = TryEncode("S", "{\"S\":\"" + new string('a', 128) + "\"}");
        Assert.False(ok);
        Assert.Contains("255", error);
    }

    [Fact]
    public void Encoded_value_at_cosmos_id_limit_is_accepted()
    {
        // 127 chars -> 254 hex chars, under the limit.
        var encoded = Encode("S", "{\"S\":\"" + new string('a', 127) + "\"}");
        Assert.Equal(254, encoded.Length);
    }

    [Fact]
    public void Cosmos_forbidden_chars_in_string_are_accepted_via_hex()
    {
        // '/', '\\', '?', '#' previously rejected; now hex-encoded.
        var encoded = Encode("S", "{\"S\":\"a/b\\\\c?d#e\"}");
        Assert.Equal(Convert.ToHexStringLower(Encoding.UTF8.GetBytes("a/b\\c?d#e")), encoded);
    }
}
