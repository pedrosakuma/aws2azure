using System.Buffers.Binary;
using Aws2Azure.Amqp.Codec;
using Aws2Azure.Amqp.Framing;

namespace Aws2Azure.UnitTests.Amqp.Framing;

public class AmqpApplicationPropertiesShortTests
{
    /// <summary>
    /// Service Bus management responses sometimes encode <c>statusCode</c>
    /// as the AMQP 1.0 <c>short</c> (0x61) type rather than <c>int</c>.
    /// The reader must promote both <c>short</c> and <c>ushort</c> to
    /// <see cref="int"/> so consumers (e.g. ServiceBusManagementClient)
    /// see a uniform type.
    /// </summary>
    [Theory]
    [InlineData((short)200)]
    [InlineData((short)-1)]
    public void Read_promotes_short_value_to_int(short value)
    {
        var payload = BuildApplicationPropertiesWithShortValue("statusCode", value);
        var dict = AmqpApplicationProperties.Read(payload, out _);
        Assert.True(dict.TryGetValue("statusCode", out var v));
        Assert.IsType<int>(v);
        Assert.Equal((int)value, (int)v!);
    }

    [Fact]
    public void Read_promotes_ushort_value_to_int()
    {
        var payload = BuildApplicationPropertiesWithUShortValue("statusCode", 410);
        var dict = AmqpApplicationProperties.Read(payload, out _);
        Assert.True(dict.TryGetValue("statusCode", out var v));
        Assert.IsType<int>(v);
        Assert.Equal(410, (int)v!);
    }

    private static byte[] BuildApplicationPropertiesWithShortValue(string key, short value)
    {
        return BuildApplicationPropertiesCore(key, valBuf =>
        {
            valBuf[0] = AmqpFormatCode.Short;
            BinaryPrimitives.WriteInt16BigEndian(valBuf.AsSpan(1, 2), value);
            return 3;
        });
    }

    private static byte[] BuildApplicationPropertiesWithUShortValue(string key, ushort value)
    {
        return BuildApplicationPropertiesCore(key, valBuf =>
        {
            valBuf[0] = AmqpFormatCode.UShort;
            BinaryPrimitives.WriteUInt16BigEndian(valBuf.AsSpan(1, 2), value);
            return 3;
        });
    }

    private static byte[] BuildApplicationPropertiesCore(string key, Func<byte[], int> writeValue)
    {
        // pair = string-key (string8) + short-encoded value
        Span<byte> keyBuf = stackalloc byte[64];
        AmqpVariableWriter.WriteString(keyBuf, key, out var keyLen);
        var valBuf = new byte[8];
        var valLen = writeValue(valBuf);
        var pair = new byte[keyLen + valLen];
        keyBuf[..keyLen].CopyTo(pair);
        valBuf.AsSpan(0, valLen).CopyTo(pair.AsSpan(keyLen));

        // Wrap as map.
        var mapBuf = new byte[pair.Length + 16];
        AmqpCompoundWriter.WriteMap(mapBuf, pair, pairCount: 1, out var mapLen);

        // Wrap as described type: 0x00 + ULong(descriptor) + map.
        const ulong AppPropsDescriptor = 0x0000_0000_0000_0074UL;
        var descBuf = new byte[16];
        descBuf[0] = 0x00;
        AmqpPrimitiveWriter.WriteULong(descBuf.AsSpan(1), AppPropsDescriptor, out var descLen);
        var hdrLen = 1 + descLen;

        var result = new byte[hdrLen + mapLen];
        descBuf.AsSpan(0, hdrLen).CopyTo(result);
        mapBuf.AsSpan(0, mapLen).CopyTo(result.AsSpan(hdrLen));
        return result;
    }
}
