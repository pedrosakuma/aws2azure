using Aws2Azure.Amqp.Framing;
using Aws2Azure.Amqp.Transport;

namespace Aws2Azure.UnitTests.Amqp.Transport;

public sealed class StreamAmqpTransportTests
{
    [Fact]
    public async Task Roundtrips_through_underlying_stream()
    {
        // Loopback over a MemoryStream isn't truly duplex, so use a pair of
        // anonymous pipes via two MemoryStreams stitched together. Easiest:
        // verify writes hit the stream and reads come from it independently.
        using var ms = new MemoryStream();
        await using var transport = new StreamAmqpTransport(ms, leaveOpen: true);

        var header = AmqpFrameCodec.AmqpProtocolHeader.ToArray();
        await transport.Output.WriteAsync(header);
        await transport.Output.FlushAsync();

        ms.Position = 0;
        var buf = new byte[8];
        var read = await ms.ReadAsync(buf.AsMemory(0, 8));
        Assert.Equal(8, read);
        Assert.Equal(header, buf);
    }

    [Fact]
    public void Rejects_non_duplex_stream()
    {
        using var ms = new MemoryStream(new byte[0], writable: false);
        Assert.Throws<ArgumentException>(() => new StreamAmqpTransport(ms));
    }

    [Fact]
    public void Rejects_null_stream()
    {
        Assert.Throws<ArgumentNullException>(() => new StreamAmqpTransport(null!));
    }

    [Fact]
    public async Task DisposeAsync_is_idempotent()
    {
        using var ms = new MemoryStream();
        var transport = new StreamAmqpTransport(ms, leaveOpen: true);
        await transport.DisposeAsync();
        await transport.DisposeAsync();
    }
}
