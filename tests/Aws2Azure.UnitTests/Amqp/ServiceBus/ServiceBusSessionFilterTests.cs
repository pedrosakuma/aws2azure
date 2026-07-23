using Aws2Azure.Amqp.Codec;
using Aws2Azure.Amqp.ServiceBus;

namespace Aws2Azure.UnitTests.Amqp.ServiceBus;

/// <summary>
/// Slice 7a — round-trip tests for the
/// <see cref="ServiceBusSessionFilter"/> codec. Verifies the encoded
/// <c>com.microsoft:session-filter</c> map can be decoded back, both
/// for the "bind to a specific session" and "any session" variants.
/// </summary>
public sealed class ServiceBusSessionFilterTests
{
    [Fact]
    public void Encode_then_TryDecode_round_trips_explicit_session_id()
    {
        var encoded = ServiceBusSessionFilter.Encode("order-42");
        Assert.False(encoded.IsEmpty);

        Assert.True(ServiceBusSessionFilter.TryDecode(encoded, out var sessionId));
        Assert.Equal("order-42", sessionId);
    }

    [Fact]
    public void Encode_then_TryDecode_round_trips_any_session()
    {
        var encoded = ServiceBusSessionFilter.Encode(sessionId: null);
        Assert.False(encoded.IsEmpty);

        Assert.True(ServiceBusSessionFilter.TryDecode(encoded, out var sessionId));
        Assert.Null(sessionId);
    }

    [Fact]
    public void Encode_then_TryDecode_round_trips_non_ascii_session_id()
    {
        var encoded = ServiceBusSessionFilter.Encode("café-naïve-💥");

        Assert.True(ServiceBusSessionFilter.TryDecode(encoded, out var sessionId));
        Assert.Equal("café-naïve-💥", sessionId);
    }

    [Fact]
    public void Encode_then_TryDecode_round_trips_max_length_session_id()
    {
        // 128 chars is the Service Bus session-id limit (and the cap
        // ServiceBusSessionFilter.Encode enforces). Still encodes via
        // the String8 path (length fits in one byte).
        var maxId = new string('s', ServiceBusSessionFilter.MaxSessionIdLength);
        var encoded = ServiceBusSessionFilter.Encode(maxId);

        Assert.True(ServiceBusSessionFilter.TryDecode(encoded, out var sessionId));
        Assert.Equal(maxId, sessionId);
    }

    [Fact]
    public void Encode_rejects_session_id_above_max_length()
    {
        var tooLong = new string('s', ServiceBusSessionFilter.MaxSessionIdLength + 1);
        Assert.Throws<ArgumentException>(() => ServiceBusSessionFilter.Encode(tooLong));
    }

    [Fact]
    public void Encode_uses_bare_session_filter_value_emitted_by_dotnet_and_java_sdks()
    {
        var encoded = ServiceBusSessionFilter.Encode("s1").ToArray();

        var span = encoded.AsSpan();
        Assert.Equal(0xC1, span[0]); // map8
        Assert.Equal(0xA3, span[3]);
        var keyLen = span[4];
        var afterKey = 5 + keyLen;
        var valueOffset = afterKey;
        Assert.Equal(0xA1, span[valueOffset]); // str8
        Assert.Equal(2, span[valueOffset + 1]);
        Assert.Equal((byte)'s', span[valueOffset + 2]);
        Assert.Equal((byte)'1', span[valueOffset + 3]);
    }

    [Fact]
    public void TryDecode_returns_false_for_empty_payload()
    {
        Assert.False(ServiceBusSessionFilter.TryDecode(ReadOnlyMemory<byte>.Empty, out var sessionId));
        Assert.Null(sessionId);
    }

    [Fact]
    public void TryDecode_rejects_trailing_payload_after_null_assignment()
    {
        var valid = ServiceBusSessionFilter.Encode(sessionId: null);
        var malformed = new byte[valid.Length + 1];
        valid.CopyTo(malformed);
        malformed[^1] = AmqpFormatCode.Null;

        Assert.False(ServiceBusSessionFilter.TryDecode(malformed, out var sessionId));
        Assert.Null(sessionId);
    }

    [Fact]
    public void TryDecode_accepts_legacy_numeric_descriptor()
    {
        var encoded = EncodeDescribedFilter(
            descriptor: 0x0000_0013_7000_000CUL,
            sessionId: "legacy-session");

        Assert.True(ServiceBusSessionFilter.TryDecode(encoded, out var sessionId));
        Assert.Equal("legacy-session", sessionId);
    }

    [Fact]
    public void TryDecode_rejects_unrecognized_descriptor()
    {
        var encoded = EncodeDescribedFilter(
            descriptor: 0xDEAD_BEEFUL,
            sessionId: "invalid-session");

        Assert.False(ServiceBusSessionFilter.TryDecode(encoded, out var sessionId));
        Assert.Null(sessionId);
    }

    private static byte[] EncodeDescribedFilter(ulong descriptor, string sessionId)
    {
        Span<byte> elements = stackalloc byte[256];
        var offset = 0;
        AmqpVariableWriter.WriteSymbol(
            elements[offset..],
            ServiceBusSessionFilter.FilterSymbol,
            out var keyLength);
        offset += keyLength;
        elements[offset++] = AmqpFormatCode.Described;
        AmqpPrimitiveWriter.WriteULong(elements[offset..], descriptor, out var descriptorLength);
        offset += descriptorLength;
        AmqpVariableWriter.WriteString(elements[offset..], sessionId, out var valueLength);
        offset += valueLength;

        var encoded = new byte[offset + 16];
        AmqpCompoundWriter.WriteMap(
            encoded,
            elements[..offset],
            pairCount: 1,
            out var encodedLength);
        Array.Resize(ref encoded, encodedLength);
        return encoded;
    }
}
