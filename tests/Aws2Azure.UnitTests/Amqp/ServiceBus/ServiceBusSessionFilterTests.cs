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
    public void Encode_uses_ulong_descriptor_for_described_value()
    {
        // The described value's descriptor MUST be the canonical
        // Service Bus session-filter ulong code 0x0000_0137_0000_000C
        // — not the symbol. Microsoft's AMQP libraries and the Azure
        // SDKs (go-amqp, .NET Service Bus client) all emit and expect
        // the ulong form.
        var encoded = ServiceBusSessionFilter.Encode("s1").ToArray();

        // Locate the described-type marker (0x00). Skip the map header
        // (map8 = 3 bytes) and the key symbol header (sym8 + 1 length
        // byte + N body bytes).
        var span = encoded.AsSpan();
        Assert.Equal(0xC1, span[0]); // map8
        // key sym8: span[3]=0xA3, span[4]=keyLen, body follows.
        Assert.Equal(0xA3, span[3]);
        var keyLen = span[4];
        var afterKey = 5 + keyLen;
        Assert.Equal(0x00, span[afterKey]); // described constructor

        // Descriptor at span[afterKey+1] should be the ulong form: either
        // smallulong (0x53 + 1 byte) — won't fit, the code is 9 bytes — or
        // ulong (0x80 + 8 bytes big-endian). The code is > 255 so we
        // expect the full ulong form.
        Assert.Equal(0x80, span[afterKey + 1]);
        var descriptor =
            ((ulong)span[afterKey + 2] << 56) |
            ((ulong)span[afterKey + 3] << 48) |
            ((ulong)span[afterKey + 4] << 40) |
            ((ulong)span[afterKey + 5] << 32) |
            ((ulong)span[afterKey + 6] << 24) |
            ((ulong)span[afterKey + 7] << 16) |
            ((ulong)span[afterKey + 8] << 8) |
             (ulong)span[afterKey + 9];
        Assert.Equal(ServiceBusSessionFilter.FilterDescriptor, descriptor);
    }

    [Fact]
    public void TryDecode_returns_false_for_empty_payload()
    {
        Assert.False(ServiceBusSessionFilter.TryDecode(ReadOnlyMemory<byte>.Empty, out var sessionId));
        Assert.Null(sessionId);
    }
}
