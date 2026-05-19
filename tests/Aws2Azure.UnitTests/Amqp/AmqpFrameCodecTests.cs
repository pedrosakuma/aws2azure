using System.Buffers.Binary;
using Aws2Azure.Amqp.Framing;

namespace Aws2Azure.UnitTests.Amqp;

/// <summary>
/// AMQP 1.0 frame envelope + protocol header tests (Phase 2.5 Slice 3a).
/// Validates the byte layout per spec §2.2 and §2.3 plus the error
/// conditions the upper layers rely on for fail-fast diagnostics.
/// </summary>
public sealed class AmqpFrameCodecTests
{
    [Fact]
    public void AmqpProtocolHeader_is_AMQP_0_1_0_0()
    {
        ReadOnlySpan<byte> expected = stackalloc byte[]
        {
            (byte)'A', (byte)'M', (byte)'Q', (byte)'P', 0, 1, 0, 0,
        };
        Assert.True(AmqpFrameCodec.AmqpProtocolHeader.SequenceEqual(expected));
    }

    [Fact]
    public void SaslProtocolHeader_is_AMQP_3_1_0_0()
    {
        ReadOnlySpan<byte> expected = stackalloc byte[]
        {
            (byte)'A', (byte)'M', (byte)'Q', (byte)'P', 3, 1, 0, 0,
        };
        Assert.True(AmqpFrameCodec.SaslProtocolHeader.SequenceEqual(expected));
    }

    // --- writing ---------------------------------------------------------

    [Fact]
    public void WriteFrame_emits_header_with_doff2_then_body()
    {
        var body = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        var buf = new byte[8 + body.Length];
        var written = AmqpFrameCodec.WriteFrame(buf, AmqpFrameType.Amqp, channel: 0x0102, body);

        Assert.Equal(12, written);
        Assert.Equal(12u, BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(0, 4)));
        Assert.Equal(2, buf[4]);       // DOFF
        Assert.Equal(0x00, buf[5]);    // type=AMQP
        Assert.Equal(0x0102, BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(6, 2)));
        Assert.Equal(body, buf.AsSpan(8).ToArray());
    }

    [Fact]
    public void WriteFrame_forces_channel_zero_for_sasl_frames()
    {
        var body = new byte[] { 0x01 };
        var buf = new byte[8 + body.Length];
        // Caller passes a nonzero channel; spec says we must emit 0.
        AmqpFrameCodec.WriteFrame(buf, AmqpFrameType.Sasl, channel: 0xBEEF, body);

        Assert.Equal(0x01, buf[5]);
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(6, 2)));
    }

    [Fact]
    public void WriteFrame_round_trips_with_empty_body()
    {
        var buf = new byte[8];
        var written = AmqpFrameCodec.WriteFrame(buf, AmqpFrameType.Amqp, 0, ReadOnlySpan<byte>.Empty);
        Assert.Equal(8, written);

        var header = AmqpFrameCodec.ReadHeader(buf);
        Assert.Equal(8, header.Size);
        Assert.Equal(8, header.DataOffset);
        Assert.Equal(AmqpFrameType.Amqp, header.Type);
        Assert.Equal(0, header.Channel);
        Assert.True(AmqpFrameCodec.GetBody(buf, header).IsEmpty);
    }

    // --- reading ---------------------------------------------------------

    [Fact]
    public void ReadHeader_decodes_a_handcrafted_frame()
    {
        // 16-byte frame: 8-byte header, DOFF=2, type=AMQP, channel=5, then 8 bytes of body.
        var wire = new byte[] { 0, 0, 0, 16, 2, 0, 0, 5, 1, 2, 3, 4, 5, 6, 7, 8 };
        var header = AmqpFrameCodec.ReadHeader(wire);
        Assert.Equal(16, header.Size);
        Assert.Equal(8, header.DataOffset);
        Assert.Equal(AmqpFrameType.Amqp, header.Type);
        Assert.Equal(5, header.Channel);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, AmqpFrameCodec.GetBody(wire, header).ToArray());
    }

    [Fact]
    public void ReadHeader_skips_extended_header_via_doff()
    {
        // 16-byte frame, DOFF=3 → body starts at byte 12; bytes 8..11 are extended header.
        var wire = new byte[]
        {
            0, 0, 0, 16, 3, 0, 0, 7,
            0xEE, 0xEE, 0xEE, 0xEE, // extended header — opaque to us
            0xAA, 0xBB, 0xCC, 0xDD,
        };
        var header = AmqpFrameCodec.ReadHeader(wire);
        Assert.Equal(12, header.DataOffset);
        Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC, 0xDD }, AmqpFrameCodec.GetBody(wire, header).ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    public void ReadHeader_rejects_buffer_smaller_than_header(int len)
    {
        var wire = new byte[len];
        Assert.Throws<InvalidDataException>(() => AmqpFrameCodec.ReadHeader(wire));
    }

    [Fact]
    public void ReadHeader_rejects_size_below_header_minimum()
    {
        var wire = new byte[] { 0, 0, 0, 7, 2, 0, 0, 0 };
        Assert.Throws<InvalidDataException>(() => AmqpFrameCodec.ReadHeader(wire));
    }

    [Fact]
    public void ReadHeader_rejects_doff_below_two()
    {
        var wire = new byte[] { 0, 0, 0, 8, 1, 0, 0, 0 };
        Assert.Throws<InvalidDataException>(() => AmqpFrameCodec.ReadHeader(wire));
    }

    [Fact]
    public void ReadHeader_rejects_doff_beyond_size()
    {
        // DOFF=3 → 12 bytes body offset; declared size 9 → invalid.
        var wire = new byte[] { 0, 0, 0, 9, 3, 0, 0, 0, 0xAA };
        Assert.Throws<InvalidDataException>(() => AmqpFrameCodec.ReadHeader(wire));
    }

    [Fact]
    public void ReadHeader_rejects_unknown_frame_type()
    {
        var wire = new byte[] { 0, 0, 0, 8, 2, 0xFF, 0, 0 };
        Assert.Throws<InvalidDataException>(() => AmqpFrameCodec.ReadHeader(wire));
    }

    [Fact]
    public void GetBody_throws_when_buffer_shorter_than_declared_size()
    {
        var wire = new byte[] { 0, 0, 0, 16, 2, 0, 0, 0 }; // declares 16 but only 8 bytes provided
        var header = AmqpFrameCodec.ReadHeader(wire);
        Assert.Throws<InvalidDataException>(() => AmqpFrameCodec.GetBody(wire, header));
    }
}
