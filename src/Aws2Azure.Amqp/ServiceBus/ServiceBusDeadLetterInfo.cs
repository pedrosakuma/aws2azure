using System.Buffers;
using Aws2Azure.Amqp.Codec;
using Aws2Azure.Amqp.Connection;
using Aws2Azure.Amqp.Framing;
using Aws2Azure.Amqp.Transport;

namespace Aws2Azure.Amqp.ServiceBus;

/// <summary>
/// Helpers for the Service Bus dead-letter <c>error.info</c> map that
/// rides on an AMQP <c>rejected</c> outcome. Service Bus copies the
/// well-known string-keyed entries from this map onto the
/// dead-lettered message's application-properties so SDK clients can
/// read <c>DeadLetterReason</c> and <c>DeadLetterErrorDescription</c>
/// off the DLQ copy.
/// <para>
/// Encodes a <c>fields</c> map (symbol-keyed AMQP map) containing
/// up to two well-known SB entries. Empty / null inputs collapse to
/// an empty <see cref="ReadOnlyMemory{T}"/>, signalling
/// <see cref="AmqpError.Info"/> should be absent on the wire.
/// </para>
/// </summary>
internal static class ServiceBusDeadLetterInfo
{
    /// <summary>
    /// Service Bus rejects oversized dead-letter metadata with
    /// <c>InvalidValue</c>. Clamp both fields conservatively before
    /// encoding so long AWS-originated text cannot fail the settle.
    /// </summary>
    public const int MaxFieldLength = 4096;
    private const string DeadLetterCondition = "com.microsoft:dead-letter";
    private const int AmqpFrameHeaderSize = 8;

    /// <summary>Symbol key Service Bus reads for the reason.</summary>
    public const string ReasonKey = "DeadLetterReason";

    /// <summary>Symbol key Service Bus reads for the description.</summary>
    public const string DescriptionKey = "DeadLetterErrorDescription";

    /// <summary>
    /// Builds the encoded <c>fields</c> map. Returns
    /// <see cref="ReadOnlyMemory{T}.Empty"/> when both inputs are
    /// null/empty (caller should leave <see cref="AmqpError.Info"/>
    /// unset).
    /// </summary>
    public static ReadOnlyMemory<byte> Encode(string? reason, string? description)
    {
        reason = Truncate(reason);
        description = Truncate(description);

        var hasReason = !string.IsNullOrEmpty(reason);
        var hasDescription = !string.IsNullOrEmpty(description);
        var pairCount = (hasReason ? 1 : 0) + (hasDescription ? 1 : 0);
        if (pairCount == 0) return ReadOnlyMemory<byte>.Empty;

        // Worst-case sizing: each pair = symbol header(2) + key bytes + string header(5) + value UTF-8 bytes.
        // Keys are tiny ASCII (≤ 26 chars). Map header ≤ 9. Pad generously.
        var keyBytesMax = ReasonKey.Length + DescriptionKey.Length + 8;
        var valBytesMax = (reason?.Length ?? 0) * 4 + (description?.Length ?? 0) * 4 + 16;
        var elementsCap = keyBytesMax + valBytesMax;

        var elementsRent = ArrayPool<byte>.Shared.Rent(elementsCap);
        var elements = elementsRent.AsSpan(0, elementsCap);
        var elementsLen = 0;
        try
        {
            if (hasReason)
            {
                AmqpVariableWriter.WriteSymbol(elements[elementsLen..], ReasonKey, out var kLen);
                elementsLen += kLen;
                AmqpVariableWriter.WriteString(elements[elementsLen..], reason!, out var vLen);
                elementsLen += vLen;
            }
            if (hasDescription)
            {
                AmqpVariableWriter.WriteSymbol(elements[elementsLen..], DescriptionKey, out var kLen);
                elementsLen += kLen;
                AmqpVariableWriter.WriteString(elements[elementsLen..], description!, out var vLen);
                elementsLen += vLen;
            }

            // Map32 worst case: 9-byte header. Add slack.
            var mapOut = new byte[elementsLen + 16];
            AmqpCompoundWriter.WriteMap(mapOut, elements[..elementsLen], pairCount: pairCount, out var mapLen);
            Array.Resize(ref mapOut, mapLen);
            return mapOut;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(elementsRent);
        }
    }

    public static (string? Reason, string? Description) ClampToFrame(
        string? reason,
        string? description,
        int maxFrameSize)
    {
        reason = Truncate(reason);
        description = Truncate(description);

        var maxDispositionBodyBytes = Math.Max(0, maxFrameSize - AmqpFrameHeaderSize);
        if (FitsDispositionFrame(reason, description, maxDispositionBodyBytes))
        {
            return (reason, description);
        }

        description = TrimFieldToFit(reason, description, maxDispositionBodyBytes, trimDescription: true);
        if (FitsDispositionFrame(reason, description, maxDispositionBodyBytes))
        {
            return (reason, description);
        }

        reason = TrimFieldToFit(description, reason, maxDispositionBodyBytes, trimDescription: false);
        if (FitsDispositionFrame(reason, description, maxDispositionBodyBytes))
        {
            return (reason, description);
        }

        return (null, null);
    }

    private static string? Truncate(string? value)
        => string.IsNullOrEmpty(value) || value.Length <= MaxFieldLength
            ? value
            : value[..MaxFieldLength];

    private static string? TrimFieldToFit(
        string? otherField,
        string? fieldToTrim,
        int maxDispositionBodyBytes,
        bool trimDescription)
    {
        if (string.IsNullOrEmpty(fieldToTrim))
        {
            return fieldToTrim;
        }

        var low = 0;
        var high = fieldToTrim.Length;
        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            var candidate = fieldToTrim[..mid];
            var fits = trimDescription
                ? FitsDispositionFrame(otherField, candidate, maxDispositionBodyBytes)
                : FitsDispositionFrame(candidate, otherField, maxDispositionBodyBytes);
            if (fits)
            {
                low = mid;
            }
            else
            {
                high = mid - 1;
            }
        }

        return low == 0 ? null : fieldToTrim[..low];
    }

    private static bool FitsDispositionFrame(
        string? reason,
        string? description,
        int maxDispositionBodyBytes)
    {
        if (maxDispositionBodyBytes <= 0)
        {
            return false;
        }

        var info = Encode(reason, description);
        var errorBuffer = ArrayPool<byte>.Shared.Rent(checked(Performatives.ScratchSize + info.Length + 256));
        var rejectedBuffer = ArrayPool<byte>.Shared.Rent(checked(Performatives.ScratchSize + info.Length + 512));
        var dispositionBuffer = ArrayPool<byte>.Shared.Rent(checked(Performatives.ScratchSize + info.Length + 768));
        try
        {
            var error = new AmqpError
            {
                Condition = DeadLetterCondition,
                Description = null,
                Info = info,
            };
            AmqpError.Write(errorBuffer, in error, out var errorLength);

            var rejected = new Rejected
            {
                Error = errorBuffer.AsMemory(0, errorLength),
            };
            Rejected.Write(rejectedBuffer, in rejected, out var rejectedLength);

            var disposition = new AmqpDisposition
            {
                Role = AmqpRole.Receiver,
                First = uint.MaxValue,
                Settled = false,
                State = rejectedBuffer.AsMemory(0, rejectedLength),
            };
            AmqpDisposition.Write(dispositionBuffer, in disposition, out var dispositionLength);
            return dispositionLength <= maxDispositionBodyBytes;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(errorBuffer);
            ArrayPool<byte>.Shared.Return(rejectedBuffer);
            ArrayPool<byte>.Shared.Return(dispositionBuffer);
        }
    }

    /// <summary>
    /// Decodes a fields map produced by <see cref="Encode"/>. Returns
    /// <c>false</c> when <paramref name="info"/> is empty or not a
    /// recognisable map. Unknown keys are ignored.
    /// </summary>
    public static bool TryDecode(ReadOnlyMemory<byte> info, out string? reason, out string? description)
    {
        reason = null;
        description = null;
        if (info.IsEmpty) return false;

        AmqpCompoundView map;
        try
        {
            map = AmqpCompoundReader.ReadMap(info.Span, out _);
        }
        catch (InvalidDataException) { return false; }

        var els = map.Elements;
        var pairs = map.Count / 2;
        int o = 0;
        for (int i = 0; i < pairs; i++)
        {
            string key;
            int kLen;
            var code = els[o];
            if (code == AmqpFormatCode.Symbol8 || code == AmqpFormatCode.Symbol32)
                key = AmqpVariableReader.ReadSymbol(els[o..], out kLen);
            else
                key = AmqpVariableReader.ReadString(els[o..], out kLen);
            o += kLen;

            string value;
            var vCode = els[o];
            int vLen;
            if (vCode == AmqpFormatCode.String8Utf8 || vCode == AmqpFormatCode.String32Utf8)
                value = AmqpVariableReader.ReadString(els[o..], out vLen);
            else if (vCode == AmqpFormatCode.Symbol8 || vCode == AmqpFormatCode.Symbol32)
                value = AmqpVariableReader.ReadSymbol(els[o..], out vLen);
            else
            {
                vLen = AmqpValueScanner.Measure(els[o..]);
                value = string.Empty;
            }
            o += vLen;

            if (key == ReasonKey) reason = value;
            else if (key == DescriptionKey) description = value;
        }
        return reason is not null || description is not null;
    }
}
