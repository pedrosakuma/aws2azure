using System.Buffers.Binary;
using System.Text;
using Aws2Azure.Amqp.Codec;
using Aws2Azure.Amqp.Connection;
using Aws2Azure.Amqp.Framing;

namespace Aws2Azure.UnitTests.Amqp.Framing;

public class AmqpHeaderTests
{
    [Fact]
    public void Reads_all_fields_when_present()
    {
        var w = new SectionWriter();
        w.WriteDescribed(MessageSectionDescriptor.Header);
        w.BeginList8(count: 5);
        w.WriteBool(true);
        w.WriteUByte(7);
        w.WriteUInt(60000);
        w.WriteBool(false);
        w.WriteUInt(42);
        w.EndList8();

        AmqpHeader.Read(w.ToMemory(), out var h, out var consumed);

        Assert.Equal(w.Length, consumed);
        Assert.True(h.Durable);
        Assert.Equal((byte)7, h.Priority);
        Assert.Equal(60000u, h.Ttl);
        Assert.False(h.FirstAcquirer);
        Assert.Equal(42u, h.DeliveryCount);
    }

    [Fact]
    public void Trailing_fields_default_to_null_when_list_truncated()
    {
        var w = new SectionWriter();
        w.WriteDescribed(MessageSectionDescriptor.Header);
        w.BeginList8(count: 1);
        w.WriteBool(true);
        w.EndList8();

        AmqpHeader.Read(w.ToMemory(), out var h, out _);

        Assert.True(h.Durable);
        Assert.Null(h.Priority);
        Assert.Null(h.Ttl);
        Assert.Null(h.FirstAcquirer);
        Assert.Null(h.DeliveryCount);
    }

    [Fact]
    public void Null_field_decodes_as_null()
    {
        var w = new SectionWriter();
        w.WriteDescribed(MessageSectionDescriptor.Header);
        w.BeginList8(count: 5);
        w.WriteNull();
        w.WriteNull();
        w.WriteByte(AmqpFormatCode.UInt0);
        w.WriteNull();
        w.WriteByte(AmqpFormatCode.UIntSmall); w.WriteByte(3);
        w.EndList8();

        AmqpHeader.Read(w.ToMemory(), out var h, out _);

        Assert.Null(h.Durable);
        Assert.Null(h.Priority);
        Assert.Equal(0u, h.Ttl);
        Assert.Null(h.FirstAcquirer);
        Assert.Equal(3u, h.DeliveryCount);
    }
}

public class AmqpMessageAnnotationsTests
{
    [Fact]
    public void Reads_known_service_bus_annotations()
    {
        var enqueuedMs = 1_700_000_000_000L;
        var lockedMs = 1_700_000_060_000L;
        var w = new SectionWriter();
        w.WriteDescribed(MessageSectionDescriptor.MessageAnnotations);
        w.BeginMap8(pairCount: 4);
        w.WriteSymbol(AmqpMessageAnnotations.KeySequenceNumber);
        w.WriteLong(987_654_321L);
        w.WriteSymbol(AmqpMessageAnnotations.KeyEnqueuedTime);
        w.WriteTimestamp(enqueuedMs);
        w.WriteSymbol(AmqpMessageAnnotations.KeyLockedUntil);
        w.WriteTimestamp(lockedMs);
        w.WriteSymbol(AmqpMessageAnnotations.KeyPartitionKey);
        w.WriteString("session-7");
        w.EndMap8();

        var ann = AmqpMessageAnnotations.Read(w.ToMemory(), out var consumed);

        Assert.Equal(w.Length, consumed);
        Assert.Equal(987_654_321L, ann.SequenceNumber);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(enqueuedMs), ann.EnqueuedTime);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(lockedMs), ann.LockedUntil);
        Assert.Equal("session-7", ann.PartitionKey);
        Assert.Null(ann.ScheduledEnqueueTime);
        Assert.Null(ann.ViaPartitionKey);
        Assert.Null(ann.MessageState);
    }

    [Fact]
    public void Unknown_keys_are_silently_skipped()
    {
        var w = new SectionWriter();
        w.WriteDescribed(MessageSectionDescriptor.MessageAnnotations);
        w.BeginMap8(pairCount: 3);
        w.WriteSymbol("x-opt-vendor-extension");
        w.WriteString("opaque");
        w.WriteSymbol(AmqpMessageAnnotations.KeyMessageState);
        w.WriteInt(2);
        w.WriteSymbol("x-opt-future-extension");
        w.WriteLong(42L);
        w.EndMap8();

        var ann = AmqpMessageAnnotations.Read(w.ToMemory(), out _);

        Assert.Equal(2, ann.MessageState);
    }

    [Fact]
    public void Value_with_wrong_type_for_known_key_yields_null()
    {
        var w = new SectionWriter();
        w.WriteDescribed(MessageSectionDescriptor.MessageAnnotations);
        w.BeginMap8(pairCount: 1);
        w.WriteSymbol(AmqpMessageAnnotations.KeySequenceNumber);
        w.WriteString("not-a-long");
        w.EndMap8();

        var ann = AmqpMessageAnnotations.Read(w.ToMemory(), out _);

        Assert.Null(ann.SequenceNumber);
    }
}

public class AmqpMessageParseHeaderAndAnnotationsTests
{
    [Fact]
    public void Parse_populates_header_and_annotations_alongside_body()
    {
        var w = new SectionWriter();
        w.WriteDescribed(MessageSectionDescriptor.Header);
        w.BeginList8(count: 5);
        w.WriteBool(false);
        w.WriteNull();
        w.WriteNull();
        w.WriteNull();
        w.WriteUInt(5);
        w.EndList8();
        w.WriteDescribed(MessageSectionDescriptor.MessageAnnotations);
        w.BeginMap8(pairCount: 1);
        w.WriteSymbol(AmqpMessageAnnotations.KeySequenceNumber);
        w.WriteLong(123L);
        w.EndMap8();
        w.WriteDescribed(MessageSectionDescriptor.Data);
        w.WriteByte(AmqpFormatCode.Binary8);
        w.WriteByte(3);
        w.WriteByte(0xDE); w.WriteByte(0xAD); w.WriteByte(0xBE);

        var msg = AmqpMessage.Parse(w.ToArray());

        Assert.NotNull(msg.Header);
        Assert.Equal(5u, msg.Header!.Value.DeliveryCount);
        Assert.False(msg.Header.Value.Durable);

        Assert.NotNull(msg.MessageAnnotations);
        Assert.Equal(123L, msg.MessageAnnotations!.SequenceNumber);

        Assert.Equal(3, msg.Body.Length);
        Assert.Equal(0xDE, msg.Body.Span[0]);
    }
}

internal sealed class SectionWriter
{
    private byte[] _buffer = new byte[256];
    private int _pos;
    private int _list8SizeIndex = -1;
    private int _map8SizeIndex = -1;

    public int Length => _pos;

    public ReadOnlyMemory<byte> ToMemory() => _buffer.AsMemory(0, _pos);
    public byte[] ToArray()
    {
        var copy = new byte[_pos];
        Array.Copy(_buffer, copy, _pos);
        return copy;
    }

    private void Ensure(int more)
    {
        if (_pos + more > _buffer.Length)
            Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _pos + more));
    }

    public void WriteByte(byte b)
    {
        Ensure(1);
        _buffer[_pos++] = b;
    }

    public void WriteDescribed(ulong descriptor)
    {
        WriteByte(AmqpFormatCode.Described);
        WriteByte(AmqpFormatCode.ULong);
        Ensure(8);
        BinaryPrimitives.WriteUInt64BigEndian(_buffer.AsSpan(_pos, 8), descriptor);
        _pos += 8;
    }

    public void BeginList8(int count)
    {
        WriteByte(AmqpFormatCode.List8);
        _list8SizeIndex = _pos;
        WriteByte(0);
        WriteByte((byte)count);
    }

    public void EndList8()
    {
        var size = _pos - _list8SizeIndex - 1;
        _buffer[_list8SizeIndex] = (byte)size;
        _list8SizeIndex = -1;
    }

    public void BeginMap8(int pairCount)
    {
        WriteByte(AmqpFormatCode.Map8);
        _map8SizeIndex = _pos;
        WriteByte(0);
        WriteByte((byte)(pairCount * 2));
    }

    public void EndMap8()
    {
        var size = _pos - _map8SizeIndex - 1;
        _buffer[_map8SizeIndex] = (byte)size;
        _map8SizeIndex = -1;
    }

    public void WriteNull() => WriteByte(AmqpFormatCode.Null);
    public void WriteBool(bool v) => WriteByte(v ? AmqpFormatCode.BooleanTrue : AmqpFormatCode.BooleanFalse);
    public void WriteUByte(byte v) { WriteByte(AmqpFormatCode.UByte); WriteByte(v); }

    public void WriteUInt(uint v)
    {
        WriteByte(AmqpFormatCode.UInt);
        Ensure(4);
        BinaryPrimitives.WriteUInt32BigEndian(_buffer.AsSpan(_pos, 4), v);
        _pos += 4;
    }

    public void WriteInt(int v)
    {
        WriteByte(AmqpFormatCode.Int);
        Ensure(4);
        BinaryPrimitives.WriteInt32BigEndian(_buffer.AsSpan(_pos, 4), v);
        _pos += 4;
    }

    public void WriteLong(long v)
    {
        WriteByte(AmqpFormatCode.Long);
        Ensure(8);
        BinaryPrimitives.WriteInt64BigEndian(_buffer.AsSpan(_pos, 8), v);
        _pos += 8;
    }

    public void WriteTimestamp(long ms)
    {
        WriteByte(AmqpFormatCode.TimestampMs);
        Ensure(8);
        BinaryPrimitives.WriteInt64BigEndian(_buffer.AsSpan(_pos, 8), ms);
        _pos += 8;
    }

    public void WriteSymbol(string ascii)
    {
        var bytes = Encoding.ASCII.GetBytes(ascii);
        if (bytes.Length <= 255)
        {
            WriteByte(AmqpFormatCode.Symbol8);
            WriteByte((byte)bytes.Length);
        }
        else
        {
            WriteByte(AmqpFormatCode.Symbol32);
            Ensure(4);
            BinaryPrimitives.WriteInt32BigEndian(_buffer.AsSpan(_pos, 4), bytes.Length);
            _pos += 4;
        }
        Ensure(bytes.Length);
        Array.Copy(bytes, 0, _buffer, _pos, bytes.Length);
        _pos += bytes.Length;
    }

    public void WriteString(string utf8)
    {
        var bytes = Encoding.UTF8.GetBytes(utf8);
        if (bytes.Length <= 255)
        {
            WriteByte(AmqpFormatCode.String8Utf8);
            WriteByte((byte)bytes.Length);
        }
        else
        {
            WriteByte(AmqpFormatCode.String32Utf8);
            Ensure(4);
            BinaryPrimitives.WriteInt32BigEndian(_buffer.AsSpan(_pos, 4), bytes.Length);
            _pos += 4;
        }
        Ensure(bytes.Length);
        Array.Copy(bytes, 0, _buffer, _pos, bytes.Length);
        _pos += bytes.Length;
    }
}
