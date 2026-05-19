using Aws2Azure.Amqp.Codec;

namespace Aws2Azure.UnitTests.Amqp;

/// <summary>
/// AMQP 1.0 fixed-width primitive codec tests (Slice 0 of Phase 2.5).
/// Validates each writer/reader against:
/// <list type="bullet">
/// <item><description>Hand-computed byte sequences from OASIS AMQP 1.0 §1.6.</description></item>
/// <item><description>Round-trips for the boundaries of the encoded ranges.</description></item>
/// </list>
/// </summary>
public sealed class AmqpPrimitiveCodecTests
{
    private const int Cap = 32;

    [Fact]
    public void Null_round_trips_as_single_0x40()
    {
        Span<byte> buf = stackalloc byte[Cap];
        AmqpPrimitiveWriter.WriteNull(buf, out var w);
        Assert.Equal(1, w);
        Assert.Equal(0x40, buf[0]);
        AmqpPrimitiveReader.ReadNull(buf[..w], out var r);
        Assert.Equal(1, r);
    }

    [Theory]
    [InlineData(true, 0x41)]
    [InlineData(false, 0x42)]
    public void Boolean_short_form_is_one_byte(bool value, byte expected)
    {
        Span<byte> buf = stackalloc byte[Cap];
        AmqpPrimitiveWriter.WriteBoolean(buf, value, out var w);
        Assert.Equal(1, w);
        Assert.Equal(expected, buf[0]);
        var decoded = AmqpPrimitiveReader.ReadBoolean(buf[..w], out var r);
        Assert.Equal(value, decoded);
        Assert.Equal(1, r);
    }

    [Fact]
    public void Boolean_long_form_0x56_is_accepted_on_read()
    {
        // We don't emit 0x56, but peers may. Spec §1.6.4.
        ReadOnlySpan<byte> wire = stackalloc byte[] { 0x56, 0x01 };
        var decoded = AmqpPrimitiveReader.ReadBoolean(wire, out var r);
        Assert.True(decoded);
        Assert.Equal(2, r);
    }

    [Fact]
    public void UByte_round_trips_full_range()
    {
        Span<byte> buf = stackalloc byte[Cap];
        for (var v = 0; v <= byte.MaxValue; v++)
        {
            AmqpPrimitiveWriter.WriteUByte(buf, (byte)v, out var w);
            Assert.Equal(2, w);
            Assert.Equal(0x50, buf[0]);
            var decoded = AmqpPrimitiveReader.ReadUByte(buf[..w], out var r);
            Assert.Equal((byte)v, decoded);
            Assert.Equal(2, r);
        }
    }

    [Theory]
    [InlineData((ushort)0, 0x60, 0x00, 0x00)]
    [InlineData((ushort)0x1234, 0x60, 0x12, 0x34)]
    [InlineData(ushort.MaxValue, 0x60, 0xFF, 0xFF)]
    public void UShort_is_big_endian(ushort value, byte b0, byte b1, byte b2)
    {
        Span<byte> buf = stackalloc byte[Cap];
        AmqpPrimitiveWriter.WriteUShort(buf, value, out var w);
        Assert.Equal(3, w);
        Assert.Equal(b0, buf[0]);
        Assert.Equal(b1, buf[1]);
        Assert.Equal(b2, buf[2]);
        Assert.Equal(value, AmqpPrimitiveReader.ReadUShort(buf[..w], out _));
    }

    [Fact]
    public void UInt_uses_compact_forms_per_spec()
    {
        Span<byte> buf = stackalloc byte[Cap];

        // 0 → uint0 (0x43), 1 byte total.
        AmqpPrimitiveWriter.WriteUInt(buf, 0, out var w);
        Assert.Equal(1, w);
        Assert.Equal(0x43, buf[0]);
        Assert.Equal(0u, AmqpPrimitiveReader.ReadUInt(buf[..w], out _));

        // 1..255 → smalluint (0x52 + 1 byte).
        AmqpPrimitiveWriter.WriteUInt(buf, 0xAB, out w);
        Assert.Equal(2, w);
        Assert.Equal(0x52, buf[0]);
        Assert.Equal(0xAB, buf[1]);
        Assert.Equal(0xABu, AmqpPrimitiveReader.ReadUInt(buf[..w], out _));

        // 256+ → uint (0x70 + 4 bytes big-endian).
        AmqpPrimitiveWriter.WriteUInt(buf, 0x01020304u, out w);
        Assert.Equal(5, w);
        Assert.Equal(0x70, buf[0]);
        Assert.Equal(0x01, buf[1]);
        Assert.Equal(0x02, buf[2]);
        Assert.Equal(0x03, buf[3]);
        Assert.Equal(0x04, buf[4]);
        Assert.Equal(0x01020304u, AmqpPrimitiveReader.ReadUInt(buf[..w], out _));
    }

    [Fact]
    public void ULong_uses_compact_forms_per_spec()
    {
        Span<byte> buf = stackalloc byte[Cap];

        AmqpPrimitiveWriter.WriteULong(buf, 0, out var w);
        Assert.Equal(1, w);
        Assert.Equal(0x44, buf[0]);
        Assert.Equal(0ul, AmqpPrimitiveReader.ReadULong(buf[..w], out _));

        AmqpPrimitiveWriter.WriteULong(buf, 0xCD, out w);
        Assert.Equal(2, w);
        Assert.Equal(0x53, buf[0]);
        Assert.Equal(0xCD, buf[1]);
        Assert.Equal(0xCDul, AmqpPrimitiveReader.ReadULong(buf[..w], out _));

        AmqpPrimitiveWriter.WriteULong(buf, 0x0102030405060708ul, out w);
        Assert.Equal(9, w);
        Assert.Equal(0x80, buf[0]);
        Assert.Equal(0x01, buf[1]);
        Assert.Equal(0x08, buf[8]);
        Assert.Equal(0x0102030405060708ul, AmqpPrimitiveReader.ReadULong(buf[..w], out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(127)]
    [InlineData(-128)]
    public void Byte_round_trips_signed_range(int value)
    {
        Span<byte> buf = stackalloc byte[Cap];
        AmqpPrimitiveWriter.WriteByte(buf, (sbyte)value, out var w);
        Assert.Equal(2, w);
        Assert.Equal(0x51, buf[0]);
        Assert.Equal((sbyte)value, AmqpPrimitiveReader.ReadByte(buf[..w], out _));
    }

    [Theory]
    [InlineData((short)0)]
    [InlineData(short.MinValue)]
    [InlineData(short.MaxValue)]
    public void Short_round_trips(short value)
    {
        Span<byte> buf = stackalloc byte[Cap];
        AmqpPrimitiveWriter.WriteShort(buf, value, out var w);
        Assert.Equal(3, w);
        Assert.Equal(0x61, buf[0]);
        Assert.Equal(value, AmqpPrimitiveReader.ReadShort(buf[..w], out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    [InlineData(-1)]
    public void Int_round_trips_long_form(int value)
    {
        Span<byte> buf = stackalloc byte[Cap];
        AmqpPrimitiveWriter.WriteInt(buf, value, out var w);
        Assert.Equal(5, w);
        Assert.Equal(0x71, buf[0]);
        Assert.Equal(value, AmqpPrimitiveReader.ReadInt(buf[..w], out _));
    }

    [Fact]
    public void Int_smallint_form_is_accepted_on_read()
    {
        // 0x54 smallint, -5 = 0xFB.
        ReadOnlySpan<byte> wire = stackalloc byte[] { 0x54, 0xFB };
        Assert.Equal(-5, AmqpPrimitiveReader.ReadInt(wire, out var r));
        Assert.Equal(2, r);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(long.MinValue)]
    [InlineData(long.MaxValue)]
    public void Long_round_trips_long_form(long value)
    {
        Span<byte> buf = stackalloc byte[Cap];
        AmqpPrimitiveWriter.WriteLong(buf, value, out var w);
        Assert.Equal(9, w);
        Assert.Equal(0x81, buf[0]);
        Assert.Equal(value, AmqpPrimitiveReader.ReadLong(buf[..w], out _));
    }

    [Fact]
    public void Long_smalllong_form_is_accepted_on_read()
    {
        // 0x55 smalllong, +42 = 0x2A.
        ReadOnlySpan<byte> wire = stackalloc byte[] { 0x55, 0x2A };
        Assert.Equal(42L, AmqpPrimitiveReader.ReadLong(wire, out var r));
        Assert.Equal(2, r);
    }

    [Theory]
    [InlineData(0.0f)]
    [InlineData(1.5f)]
    [InlineData(float.NegativeInfinity)]
    [InlineData(float.NaN)]
    public void Float_round_trips_big_endian(float value)
    {
        Span<byte> buf = stackalloc byte[Cap];
        AmqpPrimitiveWriter.WriteFloat(buf, value, out var w);
        Assert.Equal(5, w);
        Assert.Equal(0x72, buf[0]);
        var decoded = AmqpPrimitiveReader.ReadFloat(buf[..w], out _);
        if (float.IsNaN(value)) Assert.True(float.IsNaN(decoded));
        else Assert.Equal(value, decoded);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-3.14159265358979)]
    [InlineData(double.MaxValue)]
    public void Double_round_trips_big_endian(double value)
    {
        Span<byte> buf = stackalloc byte[Cap];
        AmqpPrimitiveWriter.WriteDouble(buf, value, out var w);
        Assert.Equal(9, w);
        Assert.Equal(0x82, buf[0]);
        Assert.Equal(value, AmqpPrimitiveReader.ReadDouble(buf[..w], out _));
    }

    [Fact]
    public void Timestamp_is_signed_milliseconds_since_unix_epoch()
    {
        Span<byte> buf = stackalloc byte[Cap];
        // 2024-01-15T12:34:56.789Z = 1705322096789
        const long sample = 1705322096789L;
        AmqpPrimitiveWriter.WriteTimestamp(buf, sample, out var w);
        Assert.Equal(9, w);
        Assert.Equal(0x83, buf[0]);
        Assert.Equal(sample, AmqpPrimitiveReader.ReadTimestamp(buf[..w], out _));
    }

    [Fact]
    public void Timestamp_round_trips_negative_values_for_pre_epoch()
    {
        Span<byte> buf = stackalloc byte[Cap];
        AmqpPrimitiveWriter.WriteTimestamp(buf, -1L, out var w);
        Assert.Equal(-1L, AmqpPrimitiveReader.ReadTimestamp(buf[..w], out _));
    }

    [Fact]
    public void Uuid_round_trips_big_endian_per_rfc4122()
    {
        Span<byte> buf = stackalloc byte[Cap];
        var id = new Guid("01020304-0506-0708-090A-0B0C0D0E0F10");
        AmqpPrimitiveWriter.WriteUuid(buf, id, out var w);
        Assert.Equal(17, w);
        Assert.Equal(0x98, buf[0]);
        // Big-endian: the wire bytes match the canonical text order exactly.
        Assert.Equal(0x01, buf[1]);
        Assert.Equal(0x02, buf[2]);
        Assert.Equal(0x05, buf[5]);
        Assert.Equal(0x10, buf[16]);
        Assert.Equal(id, AmqpPrimitiveReader.ReadUuid(buf[..w], out _));
    }

    [Fact]
    public void Unexpected_format_code_throws_InvalidData()
    {
        ReadOnlySpan<byte> garbage = stackalloc byte[] { 0xFF, 0x00 };
        Assert.Throws<InvalidDataException>(() =>
        {
            // Local copy — Span cannot cross the lambda.
            var b = new byte[] { 0xFF, 0x00 };
            AmqpPrimitiveReader.ReadUShort(b, out _);
        });
        _ = garbage;
    }
}
