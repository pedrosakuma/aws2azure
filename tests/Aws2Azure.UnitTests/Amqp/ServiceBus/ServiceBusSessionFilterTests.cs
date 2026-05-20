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
    public void Encode_then_TryDecode_round_trips_long_session_id()
    {
        // Push the value into the String32 path (≥256 bytes).
        var longId = new string('s', 300);
        var encoded = ServiceBusSessionFilter.Encode(longId);

        Assert.True(ServiceBusSessionFilter.TryDecode(encoded, out var sessionId));
        Assert.Equal(longId, sessionId);
    }

    [Fact]
    public void TryDecode_returns_false_for_empty_payload()
    {
        Assert.False(ServiceBusSessionFilter.TryDecode(ReadOnlyMemory<byte>.Empty, out var sessionId));
        Assert.Null(sessionId);
    }
}
