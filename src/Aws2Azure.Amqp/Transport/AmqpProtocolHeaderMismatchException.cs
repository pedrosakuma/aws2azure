namespace Aws2Azure.Amqp.Transport;

/// <summary>
/// Thrown when the peer responds to our protocol-header proposal with a
/// different 8-byte header (AMQP §2.2 protocol version negotiation).
/// </summary>
/// <remarks>
/// Per spec, on mismatch the peer may have proposed a different version
/// (e.g. AMQP 1.0.0 vs SASL 1.0.0). We currently support exactly the
/// versions encoded by
/// <see cref="Framing.AmqpFrameCodec.AmqpProtocolHeader"/> and
/// <see cref="Framing.AmqpFrameCodec.SaslProtocolHeader"/>; any other
/// response is fatal and surfaces the peer's bytes for diagnostics.
/// </remarks>
internal sealed class AmqpProtocolHeaderMismatchException : Exception
{
    public AmqpProtocolHeaderMismatchException(ReadOnlyMemory<byte> sent, ReadOnlyMemory<byte> received)
        : base($"Peer responded with unsupported protocol header. Sent: {Format(sent.Span)}, Received: {Format(received.Span)}.")
    {
        SentHeader = sent.ToArray();
        ReceivedHeader = received.ToArray();
    }

    /// <summary>The 8-byte header we sent (AMQP or SASL).</summary>
    public byte[] SentHeader { get; }

    /// <summary>The 8-byte header the peer sent back.</summary>
    public byte[] ReceivedHeader { get; }

    private static string Format(ReadOnlySpan<byte> header)
    {
        // "AMQP 1.0.0" style — protocol id (byte 4), then major/minor/revision.
        if (header.Length != 8)
            return Convert.ToHexString(header);

        var marker = header[..4];
        var isAmqp = marker.SequenceEqual("AMQP"u8);
        var label = isAmqp ? "AMQP" : Convert.ToHexString(marker);
        return $"{label} id={header[4]} {header[5]}.{header[6]}.{header[7]}";
    }
}
