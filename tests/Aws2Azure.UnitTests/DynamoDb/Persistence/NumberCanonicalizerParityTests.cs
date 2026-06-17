using System;
using System.Buffers;
using System.Text;
using System.Text.Json;
using Aws2Azure.Modules.DynamoDb.Persistence;

namespace Aws2Azure.UnitTests.DynamoDb.Persistence;

/// <summary>
/// Pins the #429 single-pass UTF-8 DDB Number path byte-identical to the proven
/// string path it shadows. The string path
/// (<see cref="InferredAttributeStorage.TryNormalizeDdbNumber"/> /
/// <see cref="InferredAttributeStorage.CanRoundTripAsBareJsonNumber"/>) still
/// serves the key-codec and filter-pushdown callers; the UTF-8 path
/// (<see cref="InferredAttributeStorage.TryCanonicalizeDdbNumberUtf8"/> /
/// <see cref="InferredAttributeStorage.CanRoundTripAsBareJsonNumberUtf8"/>)
/// serves the item write path. Any divergence between them is a correctness bug.
/// </summary>
public class NumberCanonicalizerParityTests
{
    // A wide corpus spanning the bare/envelope boundary, sign + exponent forms,
    // leading/trailing-zero stripping, the 15/16-digit and 38-digit limits, the
    // 1e±125/1e-130 magnitude edges, and malformed/out-of-range inputs.
    public static TheoryData<string> Corpus()
    {
        var data = new TheoryData<string>();
        foreach (var s in new[]
        {
            // valid — small / bare
            "0", "-0", "-0.0", "42", "-42", "+42", "3.14", "-0.001",
            "42.0", "42.00", "0.10", "01", "00", "1e3", "1.5e2", "5e-3", "-1.23e2",
            "999999999999999", "0.0000000001", "100000", "1000000000000000",
            // valid — high precision / magnitude (envelope)
            "9999999999999999", "12345678901234567890123456789",
            "1.234567890123456789", "1e30", "1.5e20",
            "99999999999999999999999999999999999999",   // 38 nines
            "1e125", "9.9999e124", "1e-130", "1.1e-130", "1.0e-130",
            "0.0000000000000001", "123456789012345678",
            "12345678901234567", // 17 digits — double precision edge
            "9007199254740993",  // 2^53 + 1 — not exactly representable as double
            // malformed / out of range
            "999999999999999999999999999999999999999", "1e126", "1e-131",
            "9.9e-131", "", "-", ".", "1.", ".5", "1e", "abc", "1.2.3",
            "1e+", "++1", "1-2", " 1", "1 ", "0x1f", "NaN", "Infinity",
        })
        {
            data.Add(s);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Corpus))]
    public void Utf8_canonicalizer_matches_string_path(string raw)
    {
        bool strOk = InferredAttributeStorage.TryNormalizeDdbNumber(
            raw, out string strCanon, out int strSig, out string strErr);

        Span<byte> dest = stackalloc byte[InferredAttributeStorage.MaxCanonicalDdbNumberUtf8Length];
        ReadOnlySpan<byte> rawUtf8 = Encoding.UTF8.GetBytes(raw);
        bool u8Ok = InferredAttributeStorage.TryCanonicalizeDdbNumberUtf8(
            rawUtf8, dest, out int u8Written, out int u8Sig, out string u8Err);

        Assert.Equal(strOk, u8Ok);

        if (!strOk)
        {
            Assert.Equal(strErr, u8Err);
            return;
        }

        string u8Canon = Encoding.UTF8.GetString(dest[..u8Written]);
        Assert.Equal(strCanon, u8Canon);
        Assert.Equal(strSig, u8Sig);

        // Bare-vs-envelope decision must agree byte-for-byte too.
        bool strBare = InferredAttributeStorage.CanRoundTripAsBareJsonNumber(strCanon);
        bool u8Bare = InferredAttributeStorage.CanRoundTripAsBareJsonNumberUtf8(dest[..u8Written]);
        Assert.Equal(strBare, u8Bare);
    }

    // Items mixing numbers with other attribute kinds, to exercise the number
    // path inside the real document walk (wire UTF-8 vs JsonElement string).
    public static TheoryData<string> Items()
    {
        var data = new TheoryData<string>();
        foreach (var s in new[]
        {
            "{\"n\":{\"N\":\"42\"}}",
            "{\"n\":{\"N\":\"-0.001\"}}",
            "{\"n\":{\"N\":\"1.5e2\"}}",
            "{\"big\":{\"N\":\"12345678901234567890123456789\"}}",
            "{\"mag\":{\"N\":\"1e30\"}}",
            "{\"edge\":{\"N\":\"9007199254740993\"}}",
            "{\"s\":{\"S\":\"hi\"},\"n\":{\"N\":\"3.14\"},\"b\":{\"BOOL\":true}}",
            "{\"a\":{\"N\":\"0\"},\"c\":{\"N\":\"-42\"},\"d\":{\"N\":\"999999999999999\"}}",
            "{\"list\":{\"L\":[{\"N\":\"1\"},{\"N\":\"2.5\"},{\"N\":\"1e20\"}]}}",
            "{\"map\":{\"M\":{\"inner\":{\"N\":\"7.77\"}}}}",
            "{\"ns\":{\"NS\":[\"1\",\"2.5\",\"1e20\"]}}",
        })
        {
            data.Add(s);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Items))]
    public void Wire_text_encode_matches_jsonelement(string itemJson)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(itemJson);
        using var doc = JsonDocument.Parse(itemJson);

        var fromWire = new ArrayBufferWriter<byte>(256);
        var fromElement = new ArrayBufferWriter<byte>(256);
        InferredAttributeStorage.WriteCosmosDocument(fromWire, "id1", "pk1", utf8);
        InferredAttributeStorage.WriteCosmosDocument(fromElement, "id1", "pk1", doc.RootElement);

        Assert.Equal(
            Encoding.UTF8.GetString(fromElement.WrittenSpan),
            Encoding.UTF8.GetString(fromWire.WrittenSpan));
    }

    [Theory]
    [MemberData(nameof(Items))]
    public void Wire_binary_encode_matches_jsonelement(string itemJson)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(itemJson);
        using var doc = JsonDocument.Parse(itemJson);

        var fromWire = new ArrayBufferWriter<byte>(256);
        var fromElement = new ArrayBufferWriter<byte>(256);
        InferredAttributeStorage.WriteCosmosDocumentBinary(fromWire, "id1", "pk1", utf8);
        InferredAttributeStorage.WriteCosmosDocumentBinary(fromElement, "id1", "pk1", doc.RootElement);

        Assert.True(fromWire.WrittenSpan.SequenceEqual(fromElement.WrittenSpan),
            $"binary wire != JsonElement for {itemJson}");
    }
}
