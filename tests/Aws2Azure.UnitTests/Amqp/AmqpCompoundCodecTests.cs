using System.Buffers.Binary;
using Aws2Azure.Amqp.Codec;

namespace Aws2Azure.UnitTests.Amqp;

/// <summary>
/// AMQP 1.0 compound + array + described-type codec tests
/// (Phase 2.5 Slice 2).
/// </summary>
public sealed class AmqpCompoundCodecTests
{
    // --- list ------------------------------------------------------------

    [Fact]
    public void Empty_list_writes_list0_as_single_byte()
    {
        var buf = new byte[16];
        AmqpCompoundWriter.WriteList(buf, ReadOnlySpan<byte>.Empty, 0, out var w);
        Assert.Equal(1, w);
        Assert.Equal(0x45, buf[0]);
        var view = AmqpCompoundReader.ReadList(buf.AsSpan(0, w), out var r);
        Assert.Equal(0, view.Count);
        Assert.True(view.Elements.IsEmpty);
        Assert.Equal(1, r);
    }

    [Fact]
    public void List_with_three_uint_elements_uses_short_form()
    {
        // Three uint values pre-encoded: 0 (uint0, 1 byte), 1 (smalluint, 2 bytes), 0x10000 (uint, 5 bytes).
        var elements = new byte[1 + 2 + 5];
        AmqpPrimitiveWriter.WriteUInt(elements.AsSpan(0), 0, out var w0);
        AmqpPrimitiveWriter.WriteUInt(elements.AsSpan(w0), 1, out var w1);
        AmqpPrimitiveWriter.WriteUInt(elements.AsSpan(w0 + w1), 0x10000, out var w2);
        Assert.Equal(elements.Length, w0 + w1 + w2);

        var buf = new byte[3 + elements.Length];
        AmqpCompoundWriter.WriteList(buf, elements, count: 3, out var w);
        Assert.Equal(0xC0, buf[0]);
        Assert.Equal(1 + elements.Length, buf[1]); // size = 1 (count byte) + element bytes
        Assert.Equal(3, buf[2]);
        Assert.Equal(3 + elements.Length, w);

        var view = AmqpCompoundReader.ReadList(buf.AsSpan(0, w), out var r);
        Assert.Equal(3, view.Count);
        Assert.Equal(elements.Length, view.Elements.Length);
        // Decode each element back.
        var consumed = 0;
        Assert.Equal(0u, AmqpPrimitiveReader.ReadUInt(view.Elements[consumed..], out var c0)); consumed += c0;
        Assert.Equal(1u, AmqpPrimitiveReader.ReadUInt(view.Elements[consumed..], out var c1)); consumed += c1;
        Assert.Equal(0x10000u, AmqpPrimitiveReader.ReadUInt(view.Elements[consumed..], out _));
        Assert.Equal(w, r);
    }

    [Fact]
    public void List_over_short_form_capacity_switches_to_long_form()
    {
        // Build elements summing to > 254 bytes by concatenating many
        // long uints (5 bytes each).
        const int count = 100; // 100 * 5 = 500 bytes of elements
        var elements = new byte[count * 5];
        for (var i = 0; i < count; i++)
        {
            AmqpPrimitiveWriter.WriteUInt(elements.AsSpan(i * 5), 0x10000u + (uint)i, out _);
        }
        var buf = new byte[9 + elements.Length];
        AmqpCompoundWriter.WriteList(buf, elements, count, out var w);

        Assert.Equal(0xD0, buf[0]);
        // size = 4 (count field) + elements.Length
        Assert.Equal((uint)(4 + elements.Length), BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(1, 4)));
        Assert.Equal((uint)count, BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(5, 4)));

        var view = AmqpCompoundReader.ReadList(buf.AsSpan(0, w), out _);
        Assert.Equal(count, view.Count);
        Assert.Equal(elements.Length, view.Elements.Length);
    }

    [Fact]
    public void List_short_form_at_high_count_boundary_round_trips()
    {
        // 255 list8 elements is the wire upper bound but the size also has
        // to fit in a byte. Smallest 1-byte-per-element value is a
        // 1-byte boolean. 255 booleans of 1 byte each = size 256 which
        // overflows the short form size field, so the writer must pick
        // list32. This test verifies that branch is hit at the boundary.
        const int count = 255;
        var elements = new byte[count];
        for (var i = 0; i < count; i++)
        {
            AmqpPrimitiveWriter.WriteBoolean(elements.AsSpan(i, 1), (i & 1) == 0, out _);
        }
        var buf = new byte[16 + count];
        AmqpCompoundWriter.WriteList(buf, elements, count, out var w);
        Assert.Equal(0xD0, buf[0]); // forced to long form due to size overflow

        var view = AmqpCompoundReader.ReadList(buf.AsSpan(0, w), out _);
        Assert.Equal(count, view.Count);
    }

    [Fact]
    public void Truncated_list32_throws()
    {
        // 0xD0 + size=1000 but buffer is only 12 bytes.
        var wire = new byte[12];
        wire[0] = 0xD0;
        BinaryPrimitives.WriteUInt32BigEndian(wire.AsSpan(1), 1000);
        BinaryPrimitives.WriteUInt32BigEndian(wire.AsSpan(5), 5);
        Assert.Throws<InvalidDataException>(() => AmqpCompoundReader.ReadList(wire, out _));
    }

    // --- map -------------------------------------------------------------

    [Fact]
    public void Map_round_trips_with_string_key_and_int_value()
    {
        // One pair: "k" → 42
        var key = new byte[16];
        AmqpVariableWriter.WriteString(key, "k", out var keyLen);
        var val = new byte[16];
        AmqpPrimitiveWriter.WriteInt(val, 42, out var valLen);
        var elements = new byte[keyLen + valLen];
        key.AsSpan(0, keyLen).CopyTo(elements);
        val.AsSpan(0, valLen).CopyTo(elements.AsSpan(keyLen));

        var buf = new byte[3 + elements.Length];
        AmqpCompoundWriter.WriteMap(buf, elements, pairCount: 1, out var w);
        Assert.Equal(0xC1, buf[0]);
        Assert.Equal(1 + elements.Length, buf[1]);
        Assert.Equal(2, buf[2]); // wire count = 2 * pairs

        var view = AmqpCompoundReader.ReadMap(buf.AsSpan(0, w), out _);
        Assert.Equal(2, view.Count);
        var consumed = 0;
        Assert.Equal("k", AmqpVariableReader.ReadString(view.Elements[consumed..], out var kc)); consumed += kc;
        Assert.Equal(42, AmqpPrimitiveReader.ReadInt(view.Elements[consumed..], out _));
    }

    [Fact]
    public void Map_with_odd_count_throws_on_read()
    {
        // Hand-craft a map8 with count=3 (odd). One string key then a
        // dangling value, total elements = 3 single-byte primitives so
        // size = 1 (count) + 3 = 4.
        var wire = new byte[6];
        wire[0] = 0xC1;
        wire[1] = 4;
        wire[2] = 3;
        wire[3] = 0x40; // null
        wire[4] = 0x40;
        wire[5] = 0x40;
        Assert.Throws<InvalidDataException>(() => AmqpCompoundReader.ReadMap(wire, out _));
    }

    // --- array -----------------------------------------------------------

    [Fact]
    public void Array_of_three_symbols_uses_shared_constructor_and_round_trips()
    {
        // We emit the array manually: constructor 0xA3 (symbol8), then for
        // each element the 1-byte length + ASCII bytes — no per-element
        // constructor.
        var elements = new List<byte>();
        foreach (var s in new[] { "ANONYMOUS", "PLAIN", "EXTERNAL" })
        {
            elements.Add((byte)s.Length);
            foreach (var c in s) elements.Add((byte)c);
        }
        var elementData = elements.ToArray();
        var elementCtor = new byte[] { 0xA3 };

        var buf = new byte[3 + elementCtor.Length + elementData.Length];
        AmqpCompoundWriter.WriteArray(buf, elementCtor, elementData, count: 3, out var w);
        Assert.Equal(0xE0, buf[0]);
        Assert.Equal(1 + elementCtor.Length + elementData.Length, buf[1]);
        Assert.Equal(3, buf[2]);
        Assert.Equal(0xA3, buf[3]);

        var view = AmqpCompoundReader.ReadArray(buf.AsSpan(0, w), out _);
        Assert.Equal(3, view.Count);
        Assert.Equal(0xA3, view.ElementConstructor);
        Assert.Equal(elementData, view.ElementData.ToArray());
    }

    [Fact]
    public void Array_long_form_round_trips()
    {
        // Force long form: 300 elements of 1-byte boolean payload = 300 bytes
        // of data, which overflows the short form size field.
        const int count = 300;
        var elementData = new byte[count];
        for (var i = 0; i < count; i++) elementData[i] = (i & 1) == 0 ? (byte)1 : (byte)0;
        var elementCtor = new byte[] { 0x56 }; // long-form boolean

        var buf = new byte[9 + elementCtor.Length + elementData.Length];
        AmqpCompoundWriter.WriteArray(buf, elementCtor, elementData, count, out var w);
        Assert.Equal(0xF0, buf[0]);
        Assert.Equal((uint)(4 + 1 + count), BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(1, 4)));
        Assert.Equal((uint)count, BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(5, 4)));

        var view = AmqpCompoundReader.ReadArray(buf.AsSpan(0, w), out _);
        Assert.Equal(count, view.Count);
        Assert.Equal(0x56, view.ElementConstructor);
        Assert.Equal(count, view.ElementData.Length);
    }

    // --- described type --------------------------------------------------

    [Fact]
    public void Described_type_round_trips_a_performative_envelope()
    {
        // Encode a fake "open" performative: descriptor = ulong 0x10, value = empty list.
        var descriptor = new byte[16];
        AmqpPrimitiveWriter.WriteULong(descriptor, 0x10ul, out var descLen);
        var value = new byte[16];
        AmqpCompoundWriter.WriteList(value, ReadOnlySpan<byte>.Empty, 0, out var valLen);

        var buf = new byte[1 + descLen + valLen];
        AmqpCompoundWriter.WriteDescribed(buf, descriptor.AsSpan(0, descLen), value.AsSpan(0, valLen), out var w);
        Assert.Equal(0x00, buf[0]);
        Assert.Equal(1 + descLen + valLen, w);

        var descOffset = AmqpCompoundReader.ReadDescribedHeader(buf.AsSpan(0, w));
        Assert.Equal(1, descOffset);
        Assert.Equal(0x10ul, AmqpPrimitiveReader.ReadULong(buf.AsSpan(descOffset), out var dc));
        var valView = AmqpCompoundReader.ReadList(buf.AsSpan(descOffset + dc), out _);
        Assert.Equal(0, valView.Count);
    }

    [Fact]
    public void Described_header_rejects_non_zero_constructor()
    {
        var wire = new byte[] { 0x71, 0x00, 0x00, 0x00, 0x01 };
        Assert.Throws<InvalidDataException>(() => AmqpCompoundReader.ReadDescribedHeader(wire));
    }

    [Fact]
    public void Unexpected_format_code_throws_on_list_map_array_readers()
    {
        var wire = new byte[] { 0xFF, 0x00 };
        Assert.Throws<InvalidDataException>(() => AmqpCompoundReader.ReadList(wire, out _));
        Assert.Throws<InvalidDataException>(() => AmqpCompoundReader.ReadMap(wire, out _));
        Assert.Throws<InvalidDataException>(() => AmqpCompoundReader.ReadArray(wire, out _));
    }
}
