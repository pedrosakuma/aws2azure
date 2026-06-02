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

    private static string EncN(string num) => Encode("N", "{\"N\":\"" + num + "\"}");

    [Fact]
    public void Number_equal_values_collapse_to_same_encoding()
    {
        var canonical = EncN("42");
        Assert.Equal(canonical, EncN("42.0"));
        Assert.Equal(canonical, EncN("42.000"));
        Assert.Equal(canonical, EncN("4.2e1"));
        Assert.Equal(canonical, EncN("0.42e2"));
        Assert.Equal(canonical, EncN("+42"));

        var zero = EncN("0");
        Assert.Equal(zero, EncN("0.0"));
        Assert.Equal(zero, EncN("-0"));
        Assert.Equal(zero, EncN("0e125"));
        Assert.Equal("1", zero);
    }

    [Fact]
    public void Number_sign_flags_partition_the_keyspace()
    {
        // negatives start '0', zero is "1", positives start '2'.
        Assert.StartsWith("0", EncN("-1"));
        Assert.Equal("1", EncN("0"));
        Assert.StartsWith("2", EncN("1"));
        Assert.True(string.CompareOrdinal(EncN("-1"), EncN("0")) < 0);
        Assert.True(string.CompareOrdinal(EncN("0"), EncN("1")) < 0);
    }

    [Theory]
    // lower numeric value first; encoding must compare ordinally-smaller.
    [InlineData("-15", "-2")]
    [InlineData("-2", "-1.5")]
    [InlineData("-1.5", "-1.23")]
    [InlineData("-1.2", "-1.19")]
    [InlineData("-1", "-0.5")]
    [InlineData("-0.001", "0")]
    [InlineData("0", "0.001")]
    [InlineData("0", "1")]
    [InlineData("9", "10")]
    [InlineData("20", "100")]
    [InlineData("1.5", "1.6")]
    [InlineData("1.23", "1.3")]
    [InlineData("99", "100")]
    [InlineData("1e-130", "1e-100")]
    [InlineData("1e124", "1e125")]
    [InlineData("-1e125", "-9.99e124")]
    [InlineData("123456789012345678901234567890", "123456789012345678901234567891")]
    public void Number_encoding_is_numeric_order_preserving(string lower, string higher)
    {
        Assert.True(
            string.CompareOrdinal(EncN(lower), EncN(higher)) < 0,
            $"expected enc({lower}) < enc({higher})");
    }

    [Fact]
    public void Number_encoding_matches_decimal_order_on_random_pairs()
    {
        // Property test: ordinal order of the encoding must equal numeric
        // order for a spread of values across sign / magnitude / precision.
        string[] samples =
        {
            "-1e125", "-1e124", "-12345.6789", "-1000", "-100", "-99.9",
            "-2", "-1.5", "-1.23", "-1", "-0.5", "-0.001", "-1e-130",
            "0",
            "1e-130", "0.001", "0.5", "1", "1.23", "1.5", "2", "9", "10",
            "20", "99.9", "100", "1000", "12345.6789", "1e124", "1e125",
        };

        for (int a = 0; a < samples.Length; a++)
        {
            for (int b = 0; b < samples.Length; b++)
            {
                int numeric = ParseOracle(samples[a]).CompareTo(ParseOracle(samples[b]));
                int encoded = Math.Sign(string.CompareOrdinal(EncN(samples[a]), EncN(samples[b])));
                Assert.True(
                    Math.Sign(numeric) == encoded,
                    $"order mismatch for {samples[a]} vs {samples[b]}: numeric={Math.Sign(numeric)} encoded={encoded}");
            }
        }
    }

    private static double ParseOracle(string s) =>
        double.Parse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture);

    // ---- Exact-oracle ordering proof -------------------------------------
    //
    // A DDB number is sign * M * 10^lsdExp where M is the significant-digit
    // integer (no trailing zero) and lsdExp is the exponent of its least
    // significant digit. BigInteger lets us compare any two such values
    // exactly — no float rounding — which is the part `double` cannot do for
    // 16-38 digit mantissas or magnitudes near 1e+-125. This is the
    // independent oracle that validates the encoder across the FULL accepted
    // domain, including "large magnitude, low precision vs high precision".
    private readonly record struct ExactNumber(
        int Sign, System.Numerics.BigInteger Magnitude, int LsdExp, string Wire);

    private static int OracleCompare(in ExactNumber a, in ExactNumber b)
    {
        if (a.Sign != b.Sign) return a.Sign.CompareTo(b.Sign);
        int e = Math.Min(a.LsdExp, b.LsdExp);
        var scaledA = a.Magnitude * System.Numerics.BigInteger.Pow(10, a.LsdExp - e);
        var scaledB = b.Magnitude * System.Numerics.BigInteger.Pow(10, b.LsdExp - e);
        int magCmp = scaledA.CompareTo(scaledB);
        return a.Sign > 0 ? magCmp : -magCmp; // for negatives, larger magnitude = smaller value
    }

    private static ExactNumber MakeExact(int sign, string significantDigits, int lsdExp)
    {
        // significantDigits is MSD-first with no trailing zero; build wire form
        // "<sign><digits>e<lsdExp>" which the codec normalizes back.
        var mag = System.Numerics.BigInteger.Parse(significantDigits);
        string wire = (sign < 0 ? "-" : "") + significantDigits + "e" + lsdExp.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return new ExactNumber(sign, mag, lsdExp, wire);
    }

    [Fact]
    public void Number_encoding_matches_exact_bigint_order_across_full_domain()
    {
        var rng = new Random(20260602); // fixed seed for CI determinism
        var samples = new System.Collections.Generic.List<ExactNumber>();

        // Targeted stress: large magnitude, low vs high precision at the same
        // and adjacent exponents — the case `double` oracles cannot resolve.
        void Add(int sign, string digits, int lsd) => samples.Add(MakeExact(sign, digits, lsd));
        string D38a = "1" + new string('0', 36) + "1";              // 38 digits: 1...01
        string D38nines = new string('9', 38);                       // 38 nines
        string D38int = "12345678901234567890123456789012345678";    // 38 digits
        string D38intPlus1 = "12345678901234567890123456789012345679"; // differs in last digit
        Assert.Equal(38, D38a.Length);
        Assert.Equal(38, D38nines.Length);
        Assert.Equal(38, D38int.Length);
        Assert.Equal(38, D38intPlus1.Length);
        foreach (var sign in new[] { 1, -1 })
        {
            Add(sign, "1", 125);          // 1e125 (one significant digit)
            Add(sign, D38a, 88);          // 1e125 + 1e88 (38 digits, MSD at E=125)
            Add(sign, D38nines, 87);      // ~9.99..e124 (38 nines, MSD at E=124)
            Add(sign, "1", 124);          // 1e124
            Add(sign, "1", -130);         // 1e-130 (magnitude floor)
            Add(sign, "2", -130);         // 2e-130
            Add(sign, "1", -129);         // 1e-129
            Add(sign, D38int, 0);         // 38-digit integer
            Add(sign, D38intPlus1, 0);    // differs only in the last (38th) digit
            Add(sign, "1", 0);
            Add(sign, "2", 0);
            Add(sign, "15", -1);          // 1.5
        }

        // Random spread across sign / precision / exponent within DDB range.
        for (int i = 0; i < 400; i++)
        {
            int sign = rng.Next(2) == 0 ? 1 : -1;
            int digitLen = rng.Next(1, 39); // 1..38 significant digits
            var digitsArr = new char[digitLen];
            digitsArr[0] = (char)('1' + rng.Next(9)); // MSD non-zero
            for (int k = 1; k < digitLen - 1; k++) digitsArr[k] = (char)('0' + rng.Next(10));
            if (digitLen > 1) digitsArr[digitLen - 1] = (char)('1' + rng.Next(9)); // no trailing zero
            string digits = new string(digitsArr);

            // Keep within DDB bounds: msdExp <= 125 and lsdExp >= -130.
            int minMsd = -130 + (digitLen - 1);
            int msdExp = rng.Next(minMsd, 126);
            int lsdExp = msdExp - (digitLen - 1);
            samples.Add(MakeExact(sign, digits, lsdExp));
        }

        // Every ordered pair must encode in the same order the exact oracle says.
        for (int i = 0; i < samples.Count; i++)
        {
            for (int j = 0; j < samples.Count; j++)
            {
                int expected = Math.Sign(OracleCompare(samples[i], samples[j]));
                int actual = Math.Sign(string.CompareOrdinal(
                    EncN(samples[i].Wire), EncN(samples[j].Wire)));
                Assert.True(
                    expected == actual,
                    $"order mismatch: {samples[i].Wire} vs {samples[j].Wire}: oracle={expected} encoded={actual}");
            }
        }
    }

    [Fact]
    public void Number_encoding_has_fixed_length_for_nonzero()
    {
        Assert.Equal(42, EncN("1").Length);
        Assert.Equal(42, EncN("-1").Length);
        Assert.Equal(42, EncN("1e125").Length);
        Assert.Equal(42, EncN("-9.99e124").Length);
        Assert.Equal("1", EncN("0"));
    }

    [Fact]
    public void Number_out_of_range_or_malformed_is_rejected()
    {
        Assert.False(TryEncode("N", "{\"N\":\"1e126\"}").ok);   // magnitude over 1e125
        Assert.False(TryEncode("N", "{\"N\":\"1e-131\"}").ok);  // magnitude under 1e-130
        Assert.False(TryEncode("N", "{\"N\":\"not-a-number\"}").ok);
        Assert.False(TryEncode("N", "{\"N\":\"1/2\"}").ok);
        Assert.False(TryEncode("N", "{\"N\":\"\"}").ok);
        // 39 significant digits exceeds DDB's 38-digit precision.
        Assert.False(TryEncode("N", "{\"N\":\"" + new string('1', 39) + "\"}").ok);
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
