using Aws2Azure.Amqp.Codec;

namespace Aws2Azure.Amqp.ServiceBus;

/// <summary>
/// Helpers for the Service Bus AMQP <c>com.microsoft:session-filter</c>
/// described type that rides on the receiver-link <c>source.filter</c>
/// map (slice 7 — session-bound receivers for FIFO / per-MessageGroupId
/// SQS queues).
/// <para>
/// On the wire the filter is a single-entry map keyed by the symbol
/// <c>com.microsoft:session-filter</c>; the value is a described type
/// using the same symbol as descriptor, wrapping either an AMQP string
/// (the session-id the receiver is binding to) or null (meaning "any
/// available session", which the broker will then echo back on the
/// attach response with the assigned session-id).
/// </para>
/// </summary>
internal static class ServiceBusSessionFilter
{
    /// <summary>Symbol both the map key and the descriptor use.</summary>
    public const string FilterSymbol = "com.microsoft:session-filter";

    /// <summary>
    /// Builds the encoded filter map for the receiver's
    /// <c>source.filter</c> field.
    /// <para>
    /// <paramref name="sessionId"/> may be <c>null</c> to request that
    /// the broker bind the link to any available session — the assigned
    /// session-id is then returned in the source's filter on the attach
    /// response and recovered via <see cref="TryDecode"/>.
    /// </para>
    /// </summary>
    public static ReadOnlyMemory<byte> Encode(string? sessionId)
    {
        // Worst-case sizing: key symbol header(2) + 28 bytes for the
        // symbol body + described constructor(1) + descriptor symbol
        // header(2) + 28 bytes + value string header(5) + value bytes
        // (worst-case 4 bytes per UTF-16 char) + padding.
        var valBytes = (sessionId?.Length ?? 0) * 4 + 16;
        var elementsCap = 2 + FilterSymbol.Length        // key sym8
                        + 1                              // described marker
                        + 2 + FilterSymbol.Length        // descriptor sym8
                        + 5 + valBytes;                  // value (string or null)
        Span<byte> elements = stackalloc byte[elementsCap];
        int o = 0;

        AmqpVariableWriter.WriteSymbol(elements[o..], FilterSymbol, out var keyLen);
        o += keyLen;

        // Described value: 0x00 + symbol descriptor + (string|null).
        elements[o++] = AmqpFormatCode.Described;
        AmqpVariableWriter.WriteSymbol(elements[o..], FilterSymbol, out var descLen);
        o += descLen;

        if (sessionId is null)
        {
            elements[o++] = AmqpFormatCode.Null;
        }
        else
        {
            AmqpVariableWriter.WriteString(elements[o..], sessionId, out var valLen);
            o += valLen;
        }

        // Map32 worst-case header = 9 bytes. Pad generously.
        var mapOut = new byte[o + 16];
        AmqpCompoundWriter.WriteMap(mapOut, elements[..o], pairCount: 1, out var mapLen);
        Array.Resize(ref mapOut, mapLen);
        return mapOut;
    }

    /// <summary>
    /// Decodes a filter map produced by <see cref="Encode"/> (typically
    /// the value echoed back by Service Bus on the attach response).
    /// Returns <c>true</c> when the map contains a
    /// <see cref="FilterSymbol"/> entry — even if the bound session is
    /// null/empty, since "any session, none yet assigned" is a valid
    /// state. <paramref name="sessionId"/> is the bound session-id when
    /// the entry's value is a non-null string; otherwise <c>null</c>.
    /// </summary>
    public static bool TryDecode(ReadOnlyMemory<byte> filter, out string? sessionId)
    {
        sessionId = null;
        if (filter.IsEmpty) return false;

        AmqpCompoundView map;
        try
        {
            map = AmqpCompoundReader.ReadMap(filter.Span, out _);
        }
        catch (InvalidDataException) { return false; }

        var els = map.Elements;
        var pairs = map.Count / 2;
        int o = 0;
        for (int i = 0; i < pairs; i++)
        {
            if (o >= els.Length) return false;
            string key;
            int kLen;
            var code = els[o];
            if (code == AmqpFormatCode.Symbol8 || code == AmqpFormatCode.Symbol32)
                key = AmqpVariableReader.ReadSymbol(els[o..], out kLen);
            else if (code == AmqpFormatCode.String8Utf8 || code == AmqpFormatCode.String32Utf8)
                key = AmqpVariableReader.ReadString(els[o..], out kLen);
            else
                return false;
            o += kLen;

            if (key != FilterSymbol)
            {
                // Skip unknown entry's value and continue.
                if (o >= els.Length) return false;
                o += AmqpValueScanner.Measure(els[o..]);
                continue;
            }

            if (o >= els.Length) return false;
            var vCode = els[o];
            if (vCode != AmqpFormatCode.Described)
            {
                // Tolerant path: some peers send the value verbatim
                // (string or null) without the described wrapper.
                if (vCode == AmqpFormatCode.Null) { sessionId = null; return true; }
                if (vCode == AmqpFormatCode.String8Utf8 || vCode == AmqpFormatCode.String32Utf8)
                {
                    sessionId = AmqpVariableReader.ReadString(els[o..], out _);
                    return true;
                }
                return false;
            }

            // Described: 0x00 + descriptor + body.
            o++;
            if (o >= els.Length) return false;
            // Skip descriptor (symbol or ulong code).
            o += AmqpValueScanner.Measure(els[o..]);
            if (o >= els.Length) return false;
            var bCode = els[o];
            if (bCode == AmqpFormatCode.Null)
            {
                sessionId = null;
            }
            else if (bCode == AmqpFormatCode.String8Utf8 || bCode == AmqpFormatCode.String32Utf8)
            {
                sessionId = AmqpVariableReader.ReadString(els[o..], out _);
            }
            else
            {
                return false;
            }
            return true;
        }
        return false;
    }
}
