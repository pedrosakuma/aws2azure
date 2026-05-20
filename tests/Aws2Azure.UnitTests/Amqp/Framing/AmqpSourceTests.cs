using Aws2Azure.Amqp.Framing;
using Aws2Azure.Amqp.ServiceBus;

namespace Aws2Azure.UnitTests.Amqp.Framing;

/// <summary>
/// Slice 7a — round-trip tests for the <see cref="AmqpSource"/> typed
/// performative, including the new <c>filter</c> field (index 8) that
/// carries the Service Bus session-filter for FIFO receivers.
/// </summary>
public sealed class AmqpSourceTests
{
    [Fact]
    public void Round_trips_address_only_with_no_filter()
    {
        var source = new AmqpSource { Address = "queue-1" };
        Span<byte> buffer = stackalloc byte[256];
        AmqpSource.Write(buffer, in source, out var written);

        AmqpSource.Read(buffer[..written].ToArray(), out var roundTripped, out var consumed);
        Assert.Equal(written, consumed);
        Assert.Equal("queue-1", roundTripped.Address);
        Assert.True(roundTripped.Filter.IsEmpty);
    }

    [Fact]
    public void Round_trips_address_with_session_filter_for_explicit_session()
    {
        var filter = ServiceBusSessionFilter.Encode("order-42");
        var source = new AmqpSource { Address = "fifo-queue", Filter = filter };

        Span<byte> buffer = stackalloc byte[512];
        AmqpSource.Write(buffer, in source, out var written);

        AmqpSource.Read(buffer[..written].ToArray(), out var roundTripped, out var consumed);
        Assert.Equal(written, consumed);
        Assert.Equal("fifo-queue", roundTripped.Address);
        Assert.False(roundTripped.Filter.IsEmpty);
        Assert.True(ServiceBusSessionFilter.TryDecode(roundTripped.Filter, out var sessionId));
        Assert.Equal("order-42", sessionId);
    }

    [Fact]
    public void Round_trips_address_with_session_filter_for_any_session()
    {
        var filter = ServiceBusSessionFilter.Encode(sessionId: null);
        var source = new AmqpSource { Address = "fifo-queue", Filter = filter };

        Span<byte> buffer = stackalloc byte[512];
        AmqpSource.Write(buffer, in source, out var written);

        AmqpSource.Read(buffer[..written].ToArray(), out var roundTripped, out var _);
        Assert.True(ServiceBusSessionFilter.TryDecode(roundTripped.Filter, out var sessionId));
        Assert.Null(sessionId);
    }
}
