using Aws2Azure.Amqp.Codec;
using Aws2Azure.Amqp.Connection;
using Aws2Azure.Amqp.Framing;

namespace Aws2Azure.UnitTests.Amqp.Framing;

public class AmqpMessageBodyValueBytesTests
{
    [Fact]
    public void BodyValueBytes_round_trips_through_write_and_parse()
    {
        // Build a body = map { "k" → 42i }.
        Span<byte> pair = stackalloc byte[64];
        AmqpVariableWriter.WriteString(pair, "k", out var keyLen);
        AmqpPrimitiveWriter.WriteInt(pair[keyLen..], 42, out var valLen);
        var pairLen = keyLen + valLen;

        var mapBuf = new byte[pairLen + 16];
        AmqpCompoundWriter.WriteMap(mapBuf, pair[..pairLen], pairCount: 1, out var mapLen);
        var body = mapBuf.AsMemory(0, mapLen);

        var msg = new AmqpMessage
        {
            Properties = new AmqpProperties { MessageId = "id-1" },
            BodyValueBytes = body,
        };

        var encoded = new byte[2048];
        msg.Write(encoded, out var written);

        var parsed = AmqpMessage.Parse(encoded.AsMemory(0, written));
        Assert.Equal("id-1", parsed.Properties.MessageId as string);
        Assert.NotNull(parsed.BodyValueBytes);
        Assert.True(parsed.BodyValueBytes!.Value.Span.SequenceEqual(body.Span));

        // Decode the parsed body bytes again to confirm they form a valid map.
        var view = AmqpCompoundReader.ReadMap(parsed.BodyValueBytes.Value.Span, out _);
        Assert.Equal(2, view.Count); // 1 pair = 2 elements
        var k = AmqpVariableReader.ReadString(view.Elements, out var ka);
        Assert.Equal("k", k);
        Assert.Equal(42, AmqpPrimitiveReader.ReadInt(view.Elements[ka..], out _));
    }
}
