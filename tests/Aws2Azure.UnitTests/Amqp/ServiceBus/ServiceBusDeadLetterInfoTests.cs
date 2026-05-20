using Aws2Azure.Amqp.ServiceBus;

namespace Aws2Azure.UnitTests.Amqp.ServiceBus;

/// <summary>
/// Slice 8c.4 — direct round-trip tests for the
/// <see cref="ServiceBusDeadLetterInfo"/> fields-map codec. The end-to-end
/// assertion (rejected disposition carrying the encoded info) lives in
/// <see cref="ServiceBusReceiverTests.DeadLetterAsync_emits_rejected_disposition"/>.
/// </summary>
public sealed class ServiceBusDeadLetterInfoTests
{
    [Fact]
    public void Encode_then_TryDecode_round_trips_both_fields()
    {
        var encoded = ServiceBusDeadLetterInfo.Encode("TooManyRetries", "lock expired 3 times");
        Assert.False(encoded.IsEmpty);

        Assert.True(ServiceBusDeadLetterInfo.TryDecode(encoded, out var reason, out var description));
        Assert.Equal("TooManyRetries", reason);
        Assert.Equal("lock expired 3 times", description);
    }

    [Fact]
    public void Encode_then_TryDecode_round_trips_reason_only()
    {
        var encoded = ServiceBusDeadLetterInfo.Encode("PoisonMessage", description: null);

        Assert.True(ServiceBusDeadLetterInfo.TryDecode(encoded, out var reason, out var description));
        Assert.Equal("PoisonMessage", reason);
        Assert.Null(description);
    }

    [Fact]
    public void Encode_then_TryDecode_round_trips_description_only()
    {
        var encoded = ServiceBusDeadLetterInfo.Encode(reason: null, description: "thrown by handler");

        Assert.True(ServiceBusDeadLetterInfo.TryDecode(encoded, out var reason, out var description));
        Assert.Null(reason);
        Assert.Equal("thrown by handler", description);
    }

    [Fact]
    public void Encode_returns_empty_when_both_inputs_are_null_or_empty()
    {
        Assert.True(ServiceBusDeadLetterInfo.Encode(null, null).IsEmpty);
        Assert.True(ServiceBusDeadLetterInfo.Encode(string.Empty, string.Empty).IsEmpty);
        Assert.True(ServiceBusDeadLetterInfo.Encode(string.Empty, null).IsEmpty);
        Assert.True(ServiceBusDeadLetterInfo.Encode(null, string.Empty).IsEmpty);
    }

    [Fact]
    public void TryDecode_returns_false_for_empty_payload()
    {
        Assert.False(ServiceBusDeadLetterInfo.TryDecode(ReadOnlyMemory<byte>.Empty, out var r, out var d));
        Assert.Null(r);
        Assert.Null(d);
    }

    [Fact]
    public void Encode_handles_long_description_above_short_form_cap()
    {
        // 300-char UTF-8 description forces String32 path inside WriteString.
        var longDescription = new string('x', 300);
        var encoded = ServiceBusDeadLetterInfo.Encode("reason", longDescription);

        Assert.True(ServiceBusDeadLetterInfo.TryDecode(encoded, out var reason, out var description));
        Assert.Equal("reason", reason);
        Assert.Equal(longDescription, description);
    }

    [Fact]
    public void Encode_preserves_non_ascii_utf8_in_values()
    {
        var encoded = ServiceBusDeadLetterInfo.Encode("café", "naïve 💥");

        Assert.True(ServiceBusDeadLetterInfo.TryDecode(encoded, out var reason, out var description));
        Assert.Equal("café", reason);
        Assert.Equal("naïve 💥", description);
    }
}
