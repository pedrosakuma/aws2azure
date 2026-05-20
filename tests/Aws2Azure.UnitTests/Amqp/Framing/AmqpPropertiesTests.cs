using Aws2Azure.Amqp.Framing;

namespace Aws2Azure.UnitTests.Amqp.Framing;

/// <summary>
/// Round-trip coverage for the <see cref="AmqpProperties"/> fields the
/// aws2azure proxy actually observes, with a focus on the slice 7c.2
/// additions: <c>group-id</c> (carries the SB session-id surfaced as
/// SQS <c>MessageGroupId</c> on FIFO receive) and <c>group-sequence</c>
/// (its monotonic counter).
/// </summary>
public class AmqpPropertiesTests
{
    [Fact]
    public void Write_then_Read_round_trips_group_id_and_group_sequence()
    {
        var props = new AmqpProperties
        {
            MessageId = "m-1",
            GroupId = "session-a",
            GroupSequence = 42u,
        };

        var buf = new byte[256];
        AmqpProperties.Write(buf, in props, out var written);

        AmqpProperties.Read(buf.AsMemory(0, written), out var read, out var consumed);

        Assert.Equal(written, consumed);
        Assert.Equal("m-1", read.MessageId);
        Assert.Equal("session-a", read.GroupId);
        Assert.Equal(42u, read.GroupSequence);
    }

    [Fact]
    public void Group_sequence_zero_round_trips()
    {
        // §1.6.5 uint encodes 0 with the dedicated single-byte uint0
        // (0x43) form rather than a regular 5-byte uint. The reader
        // must decode uint0 back to 0 and surface it as a real value,
        // not null — group-sequence == 0 is meaningful (the first
        // message in a group).
        var props = new AmqpProperties
        {
            GroupId = "g",
            GroupSequence = 0u,
        };

        var buf = new byte[128];
        AmqpProperties.Write(buf, in props, out var written);

        AmqpProperties.Read(buf.AsMemory(0, written), out var read, out _);

        Assert.Equal("g", read.GroupId);
        Assert.Equal(0u, read.GroupSequence);
    }

    [Fact]
    public void Group_sequence_above_byte_max_uses_full_uint_encoding()
    {
        var props = new AmqpProperties
        {
            GroupId = "g",
            GroupSequence = 0xCAFEBABE,
        };

        var buf = new byte[128];
        AmqpProperties.Write(buf, in props, out var written);

        AmqpProperties.Read(buf.AsMemory(0, written), out var read, out _);

        Assert.Equal(0xCAFEBABE, read.GroupSequence);
    }

    [Fact]
    public void Null_group_fields_round_trip_as_null()
    {
        var props = new AmqpProperties
        {
            MessageId = "id-only",
        };

        var buf = new byte[128];
        AmqpProperties.Write(buf, in props, out var written);

        AmqpProperties.Read(buf.AsMemory(0, written), out var read, out _);

        Assert.Equal("id-only", read.MessageId);
        Assert.Null(read.GroupId);
        Assert.Null(read.GroupSequence);
    }

    [Fact]
    public void Read_tolerates_truncated_field_list_without_group_fields()
    {
        // A peer (or older codec build) may emit a shorter field list
        // that doesn't include group-id / group-sequence at all — the
        // reader must surface them as null rather than throwing.
        var props = new AmqpProperties
        {
            MessageId = "m",
            ReplyTo = "r",
            CorrelationId = "c",
        };

        var buf = new byte[128];
        AmqpProperties.Write(buf, in props, out var written);

        AmqpProperties.Read(buf.AsMemory(0, written), out var read, out _);

        Assert.Equal("m", read.MessageId);
        Assert.Equal("r", read.ReplyTo);
        Assert.Equal("c", read.CorrelationId);
        Assert.Null(read.GroupId);
        Assert.Null(read.GroupSequence);
    }

    [Fact]
    public void Read_skips_unknown_group_sequence_variant_as_null()
    {
        // Forge a properties body whose group-sequence field is some
        // non-uint variant. The reader must skip it cleanly and
        // surface null rather than throwing.
        // Strategy: write a known good payload, then surgically replace
        // the group-sequence uint0 byte (0x43) with a null (0x40). We
        // already cover the null-skip path explicitly via the helper,
        // but this also exercises the "unknown variant" branch.
        var props = new AmqpProperties { GroupId = "g", GroupSequence = 0u };
        var buf = new byte[128];
        AmqpProperties.Write(buf, in props, out var written);

        // Locate the group-sequence byte by scanning for 0x43 after the
        // group-id string section. This is fragile if the writer
        // changes, but kept simple — and the round-trip tests above
        // would catch any structural shift.
        var span = buf.AsSpan(0, written);
        var gIdx = span.IndexOf((byte)'g');
        Assert.True(gIdx > 0);
        // group-sequence follows the group-id string immediately.
        // Replace the 0x43 (uint0) right after "g" with a 0x40 (null).
        var seqIdx = gIdx + 1;
        Assert.Equal(0x43, buf[seqIdx]);
        buf[seqIdx] = 0x40;

        AmqpProperties.Read(buf.AsMemory(0, written), out var read, out _);
        Assert.Equal("g", read.GroupId);
        Assert.Null(read.GroupSequence);
    }

    [Fact]
    public void AmqpMessage_round_trip_preserves_group_only_properties()
    {
        // Regression for the slice 7c.2 review fix: AmqpMessage.Write
        // used to gate the properties section on MessageId / ReplyTo /
        // CorrelationId only; a message whose ONLY properties are
        // GroupId / GroupSequence would silently drop the section.
        var msg = new Aws2Azure.Amqp.Connection.AmqpMessage
        {
            Properties = new AmqpProperties
            {
                GroupId = "session-x",
                GroupSequence = 7u,
            },
            Body = new byte[] { 1, 2, 3 },
        };

        var encoded = new byte[256];
        msg.Write(encoded, out var written);

        var parsed = Aws2Azure.Amqp.Connection.AmqpMessage.Parse(encoded.AsMemory(0, written));
        Assert.Equal("session-x", parsed.Properties.GroupId);
        Assert.Equal(7u, parsed.Properties.GroupSequence);
    }
}
