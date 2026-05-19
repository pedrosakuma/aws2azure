using System.Text;
using Aws2Azure.Amqp.Codec;

namespace Aws2Azure.UnitTests.Amqp;

/// <summary>
/// AMQP 1.0 variable-width primitive codec tests (Phase 2.5 Slice 1):
/// binary8/32, string8/32 UTF-8, symbol8/32 ASCII. Validates short/long
/// form selection at the 255-byte boundary, big-endian length prefixes,
/// UTF-8 fidelity, ASCII validation for symbols, and truncation errors.
/// </summary>
public sealed class AmqpVariableCodecTests
{
    // --- binary ---------------------------------------------------------

    [Fact]
    public void Binary_empty_payload_uses_short_form_with_zero_length()
    {
        var buf = new byte[8];
        AmqpVariableWriter.WriteBinary(buf, ReadOnlySpan<byte>.Empty, out var w);
        Assert.Equal(2, w);
        Assert.Equal(0xA0, buf[0]);
        Assert.Equal(0, buf[1]);
        Assert.True(AmqpVariableReader.ReadBinary(buf.AsSpan(0, w), out var r).IsEmpty);
        Assert.Equal(2, r);
    }

    [Fact]
    public void Binary_255_bytes_is_last_short_form_size()
    {
        var payload = new byte[255];
        for (var i = 0; i < payload.Length; i++) payload[i] = (byte)(i & 0xFF);
        var buf = new byte[2 + 255];

        AmqpVariableWriter.WriteBinary(buf, payload, out var w);
        Assert.Equal(257, w);
        Assert.Equal(0xA0, buf[0]);
        Assert.Equal(255, buf[1]);

        var decoded = AmqpVariableReader.ReadBinary(buf.AsSpan(0, w), out var r);
        Assert.Equal(257, r);
        Assert.True(decoded.SequenceEqual(payload));
    }

    [Fact]
    public void Binary_256_bytes_switches_to_long_form()
    {
        var payload = new byte[256];
        for (var i = 0; i < payload.Length; i++) payload[i] = (byte)i;
        var buf = new byte[5 + 256];

        AmqpVariableWriter.WriteBinary(buf, payload, out var w);
        Assert.Equal(261, w);
        Assert.Equal(0xB0, buf[0]);
        // Length is big-endian 4-byte uint.
        Assert.Equal(0x00, buf[1]);
        Assert.Equal(0x00, buf[2]);
        Assert.Equal(0x01, buf[3]);
        Assert.Equal(0x00, buf[4]);

        var decoded = AmqpVariableReader.ReadBinary(buf.AsSpan(0, w), out var r);
        Assert.Equal(261, r);
        Assert.True(decoded.SequenceEqual(payload));
    }

    [Fact]
    public void Binary_truncated_long_form_throws()
    {
        // 0xB0 announces 100 bytes but the buffer only carries 4.
        var wire = new byte[] { 0xB0, 0x00, 0x00, 0x00, 0x64, 0x01, 0x02, 0x03, 0x04 };
        Assert.Throws<InvalidDataException>(() => AmqpVariableReader.ReadBinary(wire, out _));
    }

    // --- string ---------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData("amqp:open:list")]
    public void String_short_form_round_trips_ascii(string value)
    {
        var buf = new byte[256];
        AmqpVariableWriter.WriteString(buf, value, out var w);
        Assert.Equal(0xA1, buf[0]);
        Assert.Equal(value.Length, buf[1]);
        Assert.Equal(value, AmqpVariableReader.ReadString(buf.AsSpan(0, w), out var r));
        Assert.Equal(w, r);
    }

    [Fact]
    public void String_round_trips_multibyte_utf8()
    {
        // "héllo🦀" — mixes 1, 2, and 4-byte UTF-8 sequences.
        const string value = "h\u00e9llo\U0001F980";
        var byteCount = Encoding.UTF8.GetByteCount(value);
        var buf = new byte[2 + byteCount];

        AmqpVariableWriter.WriteString(buf, value, out var w);
        Assert.Equal(0xA1, buf[0]);
        Assert.Equal(byteCount, buf[1]);
        Assert.Equal(2 + byteCount, w);
        Assert.Equal(value, AmqpVariableReader.ReadString(buf.AsSpan(0, w), out _));
    }

    [Fact]
    public void String_above_255_bytes_uses_long_form_and_round_trips()
    {
        var value = new string('x', 1024);
        var buf = new byte[5 + 1024];

        AmqpVariableWriter.WriteString(buf, value, out var w);
        Assert.Equal(0xB1, buf[0]);
        // 1024 = 0x00000400 big-endian.
        Assert.Equal(0x00, buf[1]);
        Assert.Equal(0x00, buf[2]);
        Assert.Equal(0x04, buf[3]);
        Assert.Equal(0x00, buf[4]);
        Assert.Equal(value, AmqpVariableReader.ReadString(buf.AsSpan(0, w), out _));
    }

    [Fact]
    public void String_invalid_utf8_throws_on_read()
    {
        // 0xA1, length 2, bytes 0xC3 0x28 — a known invalid UTF-8 sequence.
        var wire = new byte[] { 0xA1, 0x02, 0xC3, 0x28 };
        Assert.Throws<DecoderFallbackException>(() => AmqpVariableReader.ReadString(wire, out _));
    }

    // --- symbol ---------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("amqp:open:list")]
    [InlineData("ANONYMOUS")]
    public void Symbol_short_form_round_trips(string value)
    {
        var buf = new byte[256];
        AmqpVariableWriter.WriteSymbol(buf, value, out var w);
        Assert.Equal(0xA3, buf[0]);
        Assert.Equal(value.Length, buf[1]);
        Assert.Equal(value, AmqpVariableReader.ReadSymbol(buf.AsSpan(0, w), out _));
    }

    [Fact]
    public void Symbol_above_255_bytes_uses_long_form()
    {
        var value = new string('a', 300);
        var buf = new byte[5 + 300];
        AmqpVariableWriter.WriteSymbol(buf, value, out var w);
        Assert.Equal(0xB3, buf[0]);
        Assert.Equal(305, w);
        Assert.Equal(value, AmqpVariableReader.ReadSymbol(buf.AsSpan(0, w), out _));
    }

    [Fact]
    public void Symbol_rejects_non_ascii_on_write()
    {
        var buf = new byte[64];
        Assert.Throws<ArgumentException>(() =>
        {
            AmqpVariableWriter.WriteSymbol(buf, "café", out _);
        });
    }

    [Fact]
    public void Symbol_rejects_non_ascii_on_read()
    {
        // 0xA3, length 1, byte 0x80 — invalid for symbol.
        var wire = new byte[] { 0xA3, 0x01, 0x80 };
        Assert.Throws<InvalidDataException>(() => AmqpVariableReader.ReadSymbol(wire, out _));
    }

    [Fact]
    public void Unexpected_format_code_throws_InvalidData()
    {
        var wire = new byte[] { 0xFF, 0x00 };
        Assert.Throws<InvalidDataException>(() => AmqpVariableReader.ReadBinary(wire, out _));
        Assert.Throws<InvalidDataException>(() => AmqpVariableReader.ReadString(wire, out _));
        Assert.Throws<InvalidDataException>(() => AmqpVariableReader.ReadSymbol(wire, out _));
    }
}
