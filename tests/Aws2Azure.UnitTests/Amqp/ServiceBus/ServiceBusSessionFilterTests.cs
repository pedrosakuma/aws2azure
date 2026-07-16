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
    public void Encode_uses_verbatim_string_value_expected_by_service_bus()
    {
        var encoded = ServiceBusSessionFilter.Encode("s1").ToArray();

        var span = encoded.AsSpan();
        Assert.Equal(0xC1, span[0]); // map8
        Assert.Equal(0xA3, span[3]);
        var keyLen = span[4];
        var afterKey = 5 + keyLen;
        Assert.Equal(0xA1, span[afterKey]); // str8
        Assert.Equal(2, span[afterKey + 1]);
        Assert.Equal((byte)'s', span[afterKey + 2]);
        Assert.Equal((byte)'1', span[afterKey + 3]);
    }

    [Fact]
    public void TryDecode_returns_false_for_empty_payload()
    {
        Assert.False(ServiceBusSessionFilter.TryDecode(ReadOnlyMemory<byte>.Empty, out var sessionId));
        Assert.Null(sessionId);
    }
}
