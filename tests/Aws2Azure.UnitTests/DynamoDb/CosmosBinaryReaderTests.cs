using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using Aws2Azure.Modules.DynamoDb.Internal;
using Aws2Azure.Modules.DynamoDb.Persistence;
using Xunit;

namespace Aws2Azure.UnitTests.DynamoDb;

/// <summary>
/// Differential tests pinning <see cref="CosmosBinaryReader"/> as a faithful
/// streaming equivalent of the recursive <see cref="CosmosBinaryDecoder"/>:
/// echoing the reader's token stream back to JSON must be byte-identical to
/// decoding the same binary body. Plus end-to-end goldens that the fused GetItem
/// envelope (reader-driven) equals the proven decode-to-text envelope.
/// </summary>
public class CosmosBinaryReaderTests
{
    // Re-emits the reader's token stream as canonical JSON. If the reader is a
    // faithful streaming equivalent of the decoder, this equals Decode(binary).
    private static byte[] Echo(byte[] binary)
    {
        var bw = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(bw))
        {
            var r = new CosmosBinaryReader(binary);
            try
            {
                while (r.Read())
                {
                    switch (r.TokenType)
                    {
                        case JsonTokenType.StartObject: w.WriteStartObject(); break;
                        case JsonTokenType.EndObject: w.WriteEndObject(); break;
                        case JsonTokenType.StartArray: w.WriteStartArray(); break;
                        case JsonTokenType.EndArray: w.WriteEndArray(); break;
                        case JsonTokenType.PropertyName: w.WritePropertyName(r.ValueSpan); break;
                        case JsonTokenType.String: w.WriteStringValue(r.ValueSpan); break;
                        case JsonTokenType.Number: w.WriteRawValue(r.ValueSpan); break;
                        case JsonTokenType.True: w.WriteBooleanValue(true); break;
                        case JsonTokenType.False: w.WriteBooleanValue(false); break;
                        case JsonTokenType.Null: w.WriteNullValue(); break;
                        default: throw new Xunit.Sdk.XunitException($"unexpected token {r.TokenType}");
                    }
                }
            }
            finally
            {
                r.Dispose();
            }
            w.Flush();
        }
        return bw.WrittenSpan.ToArray();
    }

    private static byte[] Decode(byte[] binary)
    {
        var bw = new ArrayBufferWriter<byte>();
        CosmosBinaryDecoder.Decode(binary, bw);
        return bw.WrittenSpan.ToArray();
    }

    private static void AssertReaderMatchesDecoder(byte[] binary)
    {
        byte[] expected = Decode(binary);
        byte[] actual = Echo(binary);
        Assert.Equal(Encoding.UTF8.GetString(expected), Encoding.UTF8.GetString(actual));
    }

    // ---- small binary builder ----

    private sealed class Bin
    {
        private readonly List<byte> _b = new() { 0x80 };
        public Bin U8(int v) { _b.Add((byte)v); return this; }
        public Bin Bytes(params byte[] v) { _b.AddRange(v); return this; }
        public Bin I16(short v) { Span<byte> s = stackalloc byte[2]; BinaryPrimitives.WriteInt16LittleEndian(s, v); return Bytes(s.ToArray()); }
        public Bin U16(ushort v) { Span<byte> s = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(s, v); return Bytes(s.ToArray()); }
        public Bin I32(int v) { Span<byte> s = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(s, v); return Bytes(s.ToArray()); }
        public Bin U32(uint v) { Span<byte> s = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(s, v); return Bytes(s.ToArray()); }
        public Bin I64(long v) { Span<byte> s = stackalloc byte[8]; BinaryPrimitives.WriteInt64LittleEndian(s, v); return Bytes(s.ToArray()); }
        public Bin U64(ulong v) { Span<byte> s = stackalloc byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(s, v); return Bytes(s.ToArray()); }
        public Bin F64(double v) { Span<byte> s = stackalloc byte[8]; BinaryPrimitives.WriteDoubleLittleEndian(s, v); return Bytes(s.ToArray()); }
        public Bin F32(float v) { Span<byte> s = stackalloc byte[4]; BinaryPrimitives.WriteSingleLittleEndian(s, v); return Bytes(s.ToArray()); }
        public Bin F16(Half v) { Span<byte> s = stackalloc byte[2]; BinaryPrimitives.WriteHalfLittleEndian(s, v); return Bytes(s.ToArray()); }
        public Bin Inline(string s) { byte[] u = Encoding.UTF8.GetBytes(s); U8(0x80 + u.Length); return Bytes(u); }
        public byte[] Done() => _b.ToArray();
    }

    [Fact]
    public void LiteralSmallInt() => AssertReaderMatchesDecoder(new Bin().U8(0x07).Done());

    [Fact]
    public void Numbers_AllWidths()
    {
        AssertReaderMatchesDecoder(new Bin().U8(0xC7).U64(18446744073709551615).Done()); // uint64 max
        AssertReaderMatchesDecoder(new Bin().U8(0xC8).U8(200).Done());                    // uint8
        AssertReaderMatchesDecoder(new Bin().U8(0xC9).I16(-1234).Done());                 // int16
        AssertReaderMatchesDecoder(new Bin().U8(0xCA).I32(-123456).Done());               // int32
        AssertReaderMatchesDecoder(new Bin().U8(0xCB).I64(-9223372036854775808).Done());  // int64 min
        AssertReaderMatchesDecoder(new Bin().U8(0xCC).F64(3.14159265358979).Done());      // double
        AssertReaderMatchesDecoder(new Bin().U8(0xCD).F32(0.1f).Done());                  // single
        AssertReaderMatchesDecoder(new Bin().U8(0xCE).F64(1e300).Done());                 // double alias
        AssertReaderMatchesDecoder(new Bin().U8(0xCF).F16((Half)3.14).Done());            // half
        AssertReaderMatchesDecoder(new Bin().U8(0xD7).U8(255).Done());                    // uint8 alias
        AssertReaderMatchesDecoder(new Bin().U8(0xD8).U8(0x80).Done());                   // sbyte (-128)
        AssertReaderMatchesDecoder(new Bin().U8(0xD9).I16(short.MaxValue).Done());        // int16 alias
        AssertReaderMatchesDecoder(new Bin().U8(0xDA).I32(int.MaxValue).Done());          // int32 alias
        AssertReaderMatchesDecoder(new Bin().U8(0xDB).I64(long.MaxValue).Done());         // int64 alias
        AssertReaderMatchesDecoder(new Bin().U8(0xDC).U32(uint.MaxValue).Done());         // uint32
    }

    [Fact]
    public void BoolAndNull()
    {
        AssertReaderMatchesDecoder(new Bin().U8(0xD0).Done());
        AssertReaderMatchesDecoder(new Bin().U8(0xD1).Done());
        AssertReaderMatchesDecoder(new Bin().U8(0xD2).Done());
    }

    [Fact]
    public void GuidValue() =>
        AssertReaderMatchesDecoder(new Bin().U8(0xD3).Bytes(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16).Done());

    [Fact]
    public void Strings_InlineAndLengthPrefixed()
    {
        AssertReaderMatchesDecoder(new Bin().Inline("hello").Done());           // inline 0x80-0xBF
        AssertReaderMatchesDecoder(new Bin().Inline("acentuação ✓").Done());    // multibyte UTF-8 inline
        AssertReaderMatchesDecoder(new Bin().U8(0xC0).U8(3).Bytes((byte)'a', (byte)'b', (byte)'c').Done()); // 1-byte len
        AssertReaderMatchesDecoder(new Bin().U8(0xC1).U16(3).Bytes((byte)'x', (byte)'y', (byte)'z').Done()); // 2-byte len
        AssertReaderMatchesDecoder(new Bin().U8(0xC2).U32(2).Bytes((byte)'o', (byte)'k').Done());            // 4-byte len
    }

    [Fact]
    public void Strings_NeedingEscape() =>
        // A quote + backslash + control char must escape identically on both paths.
        AssertReaderMatchesDecoder(new Bin().U8(0xC0).U8(4).Bytes((byte)'"', (byte)'\\', (byte)'\n', (byte)'a').Done());

    [Fact]
    public void SystemDictionaryString() =>
        // 0x2C = system index 12 = "id".
        AssertReaderMatchesDecoder(new Bin().U8(0x2C).Done());

    [Fact]
    public void StringReference()
    {
        // Array [ "hi", <ref to "hi"> ]: 0xE5 count-prefixed, payload from offset 4.
        // offsets: 0=0x80 1=0xE5 2=len 3=count 4..6="hi" 7..8=ref(0xC3 0x04)
        var payload = new List<byte> { 0x82, (byte)'h', (byte)'i', 0xC3, 0x04 };
        var b = new List<byte> { 0x80, 0xE5, (byte)payload.Count, 2 };
        b.AddRange(payload);
        AssertReaderMatchesDecoder(b.ToArray());
    }

    [Fact]
    public void GuidStrings()
    {
        byte[] g = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 };
        AssertReaderMatchesDecoder(new Bin().U8(0x75).Bytes(g).Done()); // lower, unquoted
        AssertReaderMatchesDecoder(new Bin().U8(0x76).Bytes(g).Done()); // upper, unquoted
        AssertReaderMatchesDecoder(new Bin().U8(0x77).Bytes(g).Done()); // lower, quoted
    }

    [Fact]
    public void HexAndDateTimeCompressed()
    {
        AssertReaderMatchesDecoder(new Bin().U8(0x78).U8(4).Bytes(0x1A, 0x2B).Done()); // lower hex, 4 chars
        AssertReaderMatchesDecoder(new Bin().U8(0x79).U8(3).Bytes(0xCD, 0x0E).Done()); // upper hex, 3 chars
        AssertReaderMatchesDecoder(new Bin().U8(0x7A).U8(6).Bytes(0x12, 0x34, 0x05).Done()); // datetime, 6 chars
    }

    [Fact]
    public void Base64String()
    {
        // 0x71: lengthBytes=1, not url. lengthDiv4=2, padding=0 -> 6 source bytes.
        AssertReaderMatchesDecoder(new Bin().U8(0x71).U8(2).U8(0).Bytes(0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02).Done());
        // 0x73: url variant.
        AssertReaderMatchesDecoder(new Bin().U8(0x73).U8(2).U8(0).Bytes(0xFB, 0xFF, 0xFE, 0x10, 0x20, 0x30).Done());
    }

    [Fact]
    public void PackedString() =>
        // 0x7E: bits=7, no base char, 1-byte len. 4 chars -> ceil(28/8)=4 bytes.
        AssertReaderMatchesDecoder(new Bin().U8(0x7E).U8(4).Bytes(0x41, 0x42, 0x43, 0x44).Done());

    [Fact]
    public void BinaryBlob_EnvelopeEquivalence()
    {
        // Native binary (0xDD-0xDF) is written by the decoder via
        // WriteBase64StringValue (no '+'/'/' escaping), but the envelope transform
        // re-emits every string value via WriteStringValue on BOTH paths, so the
        // production-relevant property is fused-envelope == decode-then-text
        // envelope. (A raw token echo would diverge only because the decoder uses
        // a different writer method for blobs, not because the reader is wrong.)
        // {"id":"r","b":<blob DEADBEEF>} as an object: 0xED count-prefixed.
        var payload = new List<byte>
        {
            0x82, (byte)'i', (byte)'d', 0x81, (byte)'r', // "id":"r"
            0x81, (byte)'b', 0xDD, 0x04, 0xDE, 0xAD, 0xBE, 0xEF, // "b": blob
        };
        byte[] dd = BuildObject(payload, count: 2);
        Assert.Equal(
            Encoding.UTF8.GetString(OldEnvelope(dd)),
            Encoding.UTF8.GetString(FusedEnvelope(dd)));

        // Also exercise 0xDE (2-byte len) and 0xDF (4-byte len) blob prefixes.
        var p2 = new List<byte> { 0x82, (byte)'i', (byte)'d', 0x81, (byte)'r', 0x81, (byte)'b', 0xDE, 0x03, 0x00, 0x01, 0x02, 0x03 };
        byte[] de = BuildObject(p2, count: 2);
        Assert.Equal(Encoding.UTF8.GetString(OldEnvelope(de)), Encoding.UTF8.GetString(FusedEnvelope(de)));

        var p4 = new List<byte> { 0x82, (byte)'i', (byte)'d', 0x81, (byte)'r', 0x81, (byte)'b', 0xDF, 0x02, 0x00, 0x00, 0x00, 0xAA, 0xBB };
        byte[] df = BuildObject(p4, count: 2);
        Assert.Equal(Encoding.UTF8.GetString(OldEnvelope(df)), Encoding.UTF8.GetString(FusedEnvelope(df)));
    }

    private static byte[] BuildObject(List<byte> payload, int count)
    {
        var b = new List<byte> { 0x80, 0xED, (byte)payload.Count, (byte)count };
        b.AddRange(payload);
        return b.ToArray();
    }

    [Fact]
    public void NonFiniteFloats_AreRejected_LikeDecoder()
    {
        // The decoder formats numbers via Utf8JsonWriter.WriteNumberValue, which
        // throws on NaN/Infinity. The fused reader must NOT silently emit "NaN"/
        // "Infinity" — it must throw so the GetItem path falls back to (and matches)
        // the decode-to-text behaviour rather than producing a non-numeric token.
        foreach (byte[] body in new[]
        {
            new Bin().U8(0xCC).F64(double.NaN).Done(),
            new Bin().U8(0xCC).F64(double.PositiveInfinity).Done(),
            new Bin().U8(0xCE).F64(double.NegativeInfinity).Done(),
            new Bin().U8(0xCD).F32(float.NaN).Done(),
            new Bin().U8(0xCF).F16(Half.PositiveInfinity).Done(),
        })
        {
            Assert.Throws<JsonException>(() => Echo(body));   // reader rejects
            Assert.ThrowsAny<System.Exception>(() => Decode(body)); // decoder rejects too
        }
    }

    [Fact]
    public void ContainerChildExceedingBounds_ThrowsJsonException_LikeDecoder()
    {
        // Length-prefixed array (0xE2) declaring a 1-byte payload, but the single
        // child is a 9-byte double whose bytes are physically present (so the read
        // would otherwise succeed reading past the container's declared end). The
        // recursive decoder rejects this via its EnsureBounds check; the streaming
        // reader must do the same instead of silently reading sibling/trailing bytes.
        byte[] body = new Bin().U8(0xE2).U8(1).U8(0xCC).F64(3.14).Done();
        Assert.Throws<JsonException>(() => Echo(body));
        Assert.ThrowsAny<System.Exception>(() => Decode(body));
    }

    [Fact]
    public void TruncatedBody_ThrowsFallbackCatchableException()
    {
        // The reader does direct span slicing for speed, so a truncated body can
        // raise IndexOutOfRange/ArgumentOutOfRange rather than JsonException. The
        // GetItem fused path catches exactly those so it falls back to the decoder
        // (which surfaces the canonical JsonException) instead of leaking a 500.
        // Truncated GUID: 0xD3 promises 16 bytes, only 2 present.
        byte[] truncatedGuid = new Bin().U8(0xD3).Bytes(0x01, 0x02).Done();
        // Truncated length-prefixed string: 0xC0 promises 5 bytes, only 1 present.
        byte[] truncatedString = new Bin().U8(0xC0).U8(5).Bytes((byte)'a').Done();
        foreach (byte[] body in new[] { truncatedGuid, truncatedString })
        {
            System.Exception ex = Assert.ThrowsAny<System.Exception>(() => Echo(body));
            Assert.True(
                ex is JsonException or System.IndexOutOfRangeException or System.ArgumentOutOfRangeException,
                $"reader threw {ex.GetType().Name}, which the fused fallback does not catch");
        }
    }

    [Fact]
    public void Containers_Empty()
    {
        AssertReaderMatchesDecoder(new Bin().U8(0xE0).Done()); // []
        AssertReaderMatchesDecoder(new Bin().U8(0xE8).Done()); // {}
    }

    [Fact]
    public void Containers_SingleItem()
    {
        AssertReaderMatchesDecoder(new Bin().U8(0xE1).U8(0x07).Done());                 // [7]
        AssertReaderMatchesDecoder(new Bin().U8(0xE9).Inline("k").U8(0x07).Done());     // {"k":7}
    }

    [Fact]
    public void Containers_LengthPrefixed()
    {
        // E2/EA = L1 (1-byte payload length). Array [7, 8].
        AssertReaderMatchesDecoder(new Bin().U8(0xE2).U8(2).Bytes(0x07, 0x08).Done());
        // Object {"a":7}: payload = 0x81 'a' 0x07 = 3 bytes.
        AssertReaderMatchesDecoder(new Bin().U8(0xEA).U8(3).Bytes(0x81, (byte)'a', 0x07).Done());
    }

    [Fact]
    public void Containers_CountPrefixed()
    {
        // E5/ED = LC1 (1-byte payload len + 1-byte count).
        AssertReaderMatchesDecoder(new Bin().U8(0xE5).U8(2).U8(2).Bytes(0x07, 0x08).Done());                // [7,8]
        AssertReaderMatchesDecoder(new Bin().U8(0xED).U8(3).U8(1).Bytes(0x81, (byte)'a', 0x07).Done());     // {"a":7}
    }

    [Fact]
    public void UniformNumberArrays()
    {
        // F0: itemMarker 0xC8 (uint8, size 1), count 3.
        AssertReaderMatchesDecoder(new Bin().U8(0xF0).U8(0xC8).U8(3).Bytes(10, 20, 30).Done());
        // F1: itemMarker 0xCA (int32, size 4), count 2.
        AssertReaderMatchesDecoder(new Bin().U8(0xF1).U8(0xCA).U16(2).I32(100).I32(-200).Done());
        // F2: array of uniform arrays. itemMarker 0xC8, numberCount 2, arrayCount 2.
        AssertReaderMatchesDecoder(new Bin().U8(0xF2).U8(0xF0).U8(0xC8).U8(2).U8(2).Bytes(1, 2, 3, 4).Done());
        // F3: 2-byte counts. itemMarker 0xC8, numberCount 2, arrayCount 2.
        AssertReaderMatchesDecoder(new Bin().U8(0xF3).U8(0xF1).U8(0xC8).U16(2).U16(2).Bytes(5, 6, 7, 8).Done());
    }

    [Fact]
    public void NestedRealisticDocument()
    {
        // Object with nested object + array + mixed scalars, count-prefixed.
        // {"id":"x","n":7,"ok":true,"m":{"a":1},"l":[1,"y",null]}
        var inner = new List<byte> { 0xE5, 3, 1, 0x81, (byte)'a', 0xDA }; // m payload start (placeholder)
        // Build with the test encoder instead for the structural skeleton, then
        // assert reader==decoder on the encoder output.
        byte[] doc = CosmosBinaryTestEncoder.Encode(
            "{\"id\":\"x\",\"n\":7,\"ok\":true,\"bad\":null,\"m\":{\"a\":1,\"b\":\"z\"},\"l\":[1,\"y\",null,2.5]}");
        AssertReaderMatchesDecoder(doc);
        _ = inner;
    }

    // ---- End-to-end: fused GetItem envelope equals decode-to-text envelope ----

    private static byte[] OldEnvelope(byte[] binary)
    {
        var json = new ArrayBufferWriter<byte>();
        CosmosBinaryDecoder.Decode(binary, json);
        var scratch = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(scratch))
        {
            InferredAttributeStorage.WriteGetItemEnvelope(w, json.WrittenSpan);
            w.Flush();
        }
        return scratch.WrittenSpan.ToArray();
    }

    private static byte[] FusedEnvelope(byte[] binary)
    {
        var scratch = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(scratch))
        {
            var r = new CosmosBinaryReader(binary);
            try
            {
                InferredAttributeStorage.WriteGetItemEnvelope(w, ref r);
            }
            finally
            {
                r.Dispose();
            }
            w.Flush();
        }
        return scratch.WrittenSpan.ToArray();
    }

    public static IEnumerable<object[]> EnvelopeDocs()
    {
        yield return new object[] { "{\"id\":\"route-1\",\"_a2a\":\"item\",\"_a2a$id\":\"u-1\",\"sk\":\"s1\",\"n\":42,\"ok\":true,\"x\":null}" };
        yield return new object[] { "{\"id\":\"r\",\"nested\":{\"a\":\"x\",\"b\":7,\"c\":false},\"tags\":[\"x\",\"y\",3]}" };
        yield return new object[] { "{\"id\":\"r\",\"_a2a:N\":\"123\"}" };           // N envelope tag
        yield return new object[] { "{\"id\":\"r\",\"_a2a:SS\":[\"a\",\"b\",\"c\"]}" }; // string-set envelope tag
        yield return new object[] { "{\"id\":\"r\",\"_a2a:NS\":[\"1\",\"2\"]}" };       // number-set envelope tag
        yield return new object[] { "{\"id\":\"r\",\"empty_map\":{},\"empty_list\":[]}" };
        yield return new object[] { "{\"id\":\"r\",\"deep\":{\"a\":{\"b\":{\"c\":[1,2,{\"d\":\"e\"}]}}}}" };
        yield return new object[] { "{\"id\":\"r\",\"esc\":\"a\\\"b\\\\c\\nd\"}" };
    }

    [Theory]
    [MemberData(nameof(EnvelopeDocs))]
    public void FusedEnvelope_IsByteIdentical_ToDecodeThenText(string json)
    {
        byte[] binary = CosmosBinaryTestEncoder.Encode(json);
        byte[] expected = OldEnvelope(binary);
        byte[] actual = FusedEnvelope(binary);
        Assert.Equal(Encoding.UTF8.GetString(expected), Encoding.UTF8.GetString(actual));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(42)]
    public void Randomized_ReaderMatchesDecoder(int seed)
    {
        var rng = new Random(seed);
        for (int iter = 0; iter < 200; iter++)
        {
            string json = RandomJson(rng, depth: 0);
            byte[] binary = CosmosBinaryTestEncoder.Encode("{" + "\"r\":" + json + "}");
            AssertReaderMatchesDecoder(binary);
        }
    }

    private static string RandomJson(Random rng, int depth)
    {
        int choice = rng.Next(depth >= 4 ? 6 : 8);
        switch (choice)
        {
            case 0: return rng.Next(int.MinValue, int.MaxValue).ToString(System.Globalization.CultureInfo.InvariantCulture);
            case 1: return JsonSerializer.Serialize(rng.NextDouble() * 1e6 - 5e5);
            case 2: return "true";
            case 3: return "false";
            case 4: return "null";
            case 5: return JsonSerializer.Serialize(RandomString(rng));
            case 6:
            {
                int n = rng.Next(0, 4);
                var items = new string[n];
                for (int i = 0; i < n; i++) items[i] = RandomJson(rng, depth + 1);
                return "[" + string.Join(",", items) + "]";
            }
            default:
            {
                int n = rng.Next(0, 4);
                var sb = new StringBuilder("{");
                for (int i = 0; i < n; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(JsonSerializer.Serialize("k" + i)).Append(':').Append(RandomJson(rng, depth + 1));
                }
                return sb.Append('}').ToString();
            }
        }
    }

    private static string RandomString(Random rng)
    {
        int len = rng.Next(0, 12);
        var sb = new StringBuilder(len);
        const string alphabet = "abcXYZ0_ \"\\\n\tçé✓/+";
        for (int i = 0; i < len; i++) sb.Append(alphabet[rng.Next(alphabet.Length)]);
        return sb.ToString();
    }

    [Fact]
    public void Skip_SkipsNestedContainersCorrectly()
    {
        // Drive the reader and use Skip on a nested value; the surrounding walk
        // must continue from the right offset. Validate by full echo equality
        // after a manual skip-and-resume (decoder has no skip, so we only assert
        // Skip lands the reader on the matching end token).
        byte[] binary = CosmosBinaryTestEncoder.Encode("{\"a\":{\"x\":1,\"y\":2},\"b\":7}");
        var r = new CosmosBinaryReader(binary);
        try
        {
            Assert.True(r.Read()); Assert.Equal(JsonTokenType.StartObject, r.TokenType);
            Assert.True(r.Read()); Assert.Equal(JsonTokenType.PropertyName, r.TokenType); // "a"
            Assert.True(r.Read()); Assert.Equal(JsonTokenType.StartObject, r.TokenType); // {
            r.Skip();
            Assert.Equal(JsonTokenType.EndObject, r.TokenType); // skipped to matching }
            Assert.True(r.Read()); Assert.Equal(JsonTokenType.PropertyName, r.TokenType); // "b"
            Assert.True(r.Read()); Assert.Equal(JsonTokenType.Number, r.TokenType);
            Assert.Equal("7", Encoding.UTF8.GetString(r.ValueSpan));
            Assert.True(r.Read()); Assert.Equal(JsonTokenType.EndObject, r.TokenType);
            Assert.False(r.Read());
        }
        finally
        {
            r.Dispose();
        }
    }
}
