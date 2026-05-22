using System.Buffers.Binary;
using Aws2Azure.Amqp.Codec;

namespace Aws2Azure.Amqp.Framing;

/// <summary>
/// AMQP 1.0 <c>message-annotations</c> message section (§3.2.3,
/// descriptor 0x72) — a described map whose keys are <see cref="string"/>
/// symbols (per spec, may also be ulong; this profile rejects the ulong
/// variant since no SB key uses it) and whose values are AMQP primitive
/// types. Service Bus piggy-backs all of its message metadata on this
/// section using <c>x-opt-*</c> keys, so for the SQS translation the
/// proxy needs to peel the following well-known values out:
/// <list type="bullet">
///   <item><c>x-opt-sequence-number</c> (long) →
///   <see cref="SequenceNumber"/>.</item>
///   <item><c>x-opt-locked-until</c> (timestamp) →
///   <see cref="LockedUntil"/>.</item>
///   <item><c>x-opt-enqueued-time</c> (timestamp) →
///   <see cref="EnqueuedTime"/>.</item>
///   <item><c>x-opt-scheduled-enqueue-time</c> (timestamp) →
///   <see cref="ScheduledEnqueueTime"/>.</item>
///   <item><c>x-opt-partition-key</c> (string) →
///   <see cref="PartitionKey"/>.</item>
///   <item><c>x-opt-via-partition-key</c> (string) →
///   <see cref="ViaPartitionKey"/>.</item>
///   <item><c>x-opt-deadletter-source</c> (string) →
///   <see cref="DeadLetterSource"/>.</item>
///   <item><c>x-opt-message-state</c> (int) →
///   <see cref="MessageState"/>.</item>
/// </list>
/// Read-only: the proxy never authors annotations.
/// <para>
/// Unknown keys and values with unsupported primitive types are skipped
/// silently — matching the codec's tolerance policy elsewhere — rather
/// than throwing. A future revision can add a raw-bag <c>Other</c>
/// surface if diagnostics need it.
/// </para>
/// </summary>
internal sealed class AmqpMessageAnnotations
{
    public const ulong Descriptor = MessageSectionDescriptor.MessageAnnotations;

    // Known SB annotation keys (cached to avoid per-message allocation
    // when generating diagnostic strings later). Kept as plain consts so
    // the AOT-trimmer can fold them at publish time.
    public const string KeySequenceNumber        = "x-opt-sequence-number";
    public const string KeyLockedUntil           = "x-opt-locked-until";
    public const string KeyEnqueuedTime          = "x-opt-enqueued-time";
    public const string KeyScheduledEnqueueTime  = "x-opt-scheduled-enqueue-time";
    public const string KeyPartitionKey          = "x-opt-partition-key";
    public const string KeyViaPartitionKey       = "x-opt-via-partition-key";
    public const string KeyDeadLetterSource      = "x-opt-deadletter-source";
    public const string KeyMessageState          = "x-opt-message-state";

    public long? SequenceNumber       { get; init; }
    public DateTimeOffset? LockedUntil          { get; init; }
    public DateTimeOffset? EnqueuedTime         { get; init; }
    public DateTimeOffset? ScheduledEnqueueTime { get; init; }
    public string? PartitionKey                 { get; init; }
    public string? ViaPartitionKey              { get; init; }
    public string? DeadLetterSource             { get; init; }
    public int? MessageState                    { get; init; }

    /// <summary>
    /// Writes an <c>x-opt-*</c> annotation map. Only the writer-side
    /// keys SQS publishers need today are emitted:
    /// <see cref="ScheduledEnqueueTime"/> (FIFO + standard
    /// <c>DelaySeconds</c>) and <see cref="PartitionKey"/> (Service Bus
    /// throughput-aware partitioning). The remaining annotations are
    /// broker-authored on receive and intentionally not serialised
    /// here.
    /// </summary>
    public void Write(Span<byte> destination, out int written)
    {
        Span<byte> body = stackalloc byte[Performatives.ScratchSize];
        int o = 0;
        int pairCount = 0;
        int len;
        if (ScheduledEnqueueTime is { } sched)
        {
            AmqpVariableWriter.WriteSymbol(body[o..], KeyScheduledEnqueueTime, out len); o += len;
            AmqpPrimitiveWriter.WriteTimestamp(body[o..], sched.ToUnixTimeMilliseconds(), out len); o += len;
            pairCount++;
        }
        if (PartitionKey is { } pk)
        {
            AmqpVariableWriter.WriteSymbol(body[o..], KeyPartitionKey, out len); o += len;
            AmqpVariableWriter.WriteString(body[o..], pk, out len); o += len;
            pairCount++;
        }
        if (ViaPartitionKey is { } vpk)
        {
            AmqpVariableWriter.WriteSymbol(body[o..], KeyViaPartitionKey, out len); o += len;
            AmqpVariableWriter.WriteString(body[o..], vpk, out len); o += len;
            pairCount++;
        }

        Span<byte> mapBytes = stackalloc byte[Performatives.ScratchSize];
        AmqpCompoundWriter.WriteMap(mapBytes, body[..o], pairCount, out var mapLen);

        int w = 0;
        destination[w++] = AmqpFormatCode.Described;
        AmqpPrimitiveWriter.WriteULong(destination[w..], Descriptor, out var descLen); w += descLen;
        mapBytes[..mapLen].CopyTo(destination[w..]); w += mapLen;
        written = w;
    }

    /// <summary>
    /// True when the section is empty (no writable annotations set) and
    /// can be skipped during message serialisation.
    /// </summary>
    public bool IsEmptyForWrite =>
        ScheduledEnqueueTime is null && PartitionKey is null && ViaPartitionKey is null;

    public static AmqpMessageAnnotations Read(ReadOnlyMemory<byte> source, out int consumed)
    {
        var span = source.Span;
        var offset = AmqpCompoundReader.ReadDescribedHeader(span);
        var descriptor = AmqpPrimitiveReader.ReadULong(span[offset..], out var descLen);
        if (descriptor != Descriptor)
            throw new InvalidDataException(
                $"Expected message-annotations descriptor 0x{Descriptor:X16}, got 0x{descriptor:X16}.");
        offset += descLen;

        var view = AmqpCompoundReader.ReadMap(span[offset..], out var mapLen);
        consumed = offset + mapLen;

        long? sequenceNumber = null;
        DateTimeOffset? lockedUntil = null;
        DateTimeOffset? enqueuedTime = null;
        DateTimeOffset? scheduledEnqueueTime = null;
        string? partitionKey = null;
        string? viaPartitionKey = null;
        string? deadLetterSource = null;
        int? messageState = null;

        var els = view.Elements;
        var pairCount = view.Count / 2;
        int o = 0;
        for (int i = 0; i < pairCount; i++)
        {
            // Key: symbol (preferred) or string. Anything else → skip pair.
            string? key = ReadSymbolOrStringOrSkip(els, ref o);
            if (key is null)
            {
                SkipValue(els, ref o);
                continue;
            }

            // Dispatch on key; default = skip value.
            switch (key)
            {
                case KeySequenceNumber:
                    sequenceNumber = ReadLongOrSkip(els, ref o);
                    break;
                case KeyLockedUntil:
                    lockedUntil = ReadTimestampOrSkip(els, ref o);
                    break;
                case KeyEnqueuedTime:
                    enqueuedTime = ReadTimestampOrSkip(els, ref o);
                    break;
                case KeyScheduledEnqueueTime:
                    scheduledEnqueueTime = ReadTimestampOrSkip(els, ref o);
                    break;
                case KeyPartitionKey:
                    partitionKey = ReadStringOrSkip(els, ref o);
                    break;
                case KeyViaPartitionKey:
                    viaPartitionKey = ReadStringOrSkip(els, ref o);
                    break;
                case KeyDeadLetterSource:
                    deadLetterSource = ReadStringOrSkip(els, ref o);
                    break;
                case KeyMessageState:
                    messageState = ReadIntOrSkip(els, ref o);
                    break;
                default:
                    SkipValue(els, ref o);
                    break;
            }
        }

        return new AmqpMessageAnnotations
        {
            SequenceNumber = sequenceNumber,
            LockedUntil = lockedUntil,
            EnqueuedTime = enqueuedTime,
            ScheduledEnqueueTime = scheduledEnqueueTime,
            PartitionKey = partitionKey,
            ViaPartitionKey = viaPartitionKey,
            DeadLetterSource = deadLetterSource,
            MessageState = messageState,
        };
    }

    // --- value readers (skip on type mismatch, never throw) ----------------

    private static void SkipValue(ReadOnlySpan<byte> els, ref int o)
    {
        if (PerformativeCodec.TryConsumeNull(els, ref o)) return;
        o += AmqpValueScanner.Measure(els[o..]);
    }

    private static string? ReadSymbolOrStringOrSkip(ReadOnlySpan<byte> els, ref int o)
    {
        if (o >= els.Length) return null;
        var fc = els[o];
        switch (fc)
        {
            case AmqpFormatCode.Symbol8:
            case AmqpFormatCode.Symbol32:
            {
                var s = AmqpVariableReader.ReadSymbol(els[o..], out var len);
                o += len;
                return s;
            }
            case AmqpFormatCode.String8Utf8:
            case AmqpFormatCode.String32Utf8:
            {
                var s = AmqpVariableReader.ReadString(els[o..], out var len);
                o += len;
                return s;
            }
            default:
                o += AmqpValueScanner.Measure(els[o..]);
                return null;
        }
    }

    private static string? ReadStringOrSkip(ReadOnlySpan<byte> els, ref int o)
    {
        if (PerformativeCodec.TryConsumeNull(els, ref o)) return null;
        var fc = els[o];
        if (fc == AmqpFormatCode.String8Utf8 || fc == AmqpFormatCode.String32Utf8)
        {
            var s = AmqpVariableReader.ReadString(els[o..], out var len);
            o += len;
            return s;
        }
        o += AmqpValueScanner.Measure(els[o..]);
        return null;
    }

    private static long? ReadLongOrSkip(ReadOnlySpan<byte> els, ref int o)
    {
        if (PerformativeCodec.TryConsumeNull(els, ref o)) return null;
        var fc = els[o];
        if (fc == AmqpFormatCode.Long || fc == 0x55)
        {
            var v = AmqpPrimitiveReader.ReadLong(els[o..], out var len);
            o += len;
            return v;
        }
        o += AmqpValueScanner.Measure(els[o..]);
        return null;
    }

    private static int? ReadIntOrSkip(ReadOnlySpan<byte> els, ref int o)
    {
        if (PerformativeCodec.TryConsumeNull(els, ref o)) return null;
        var fc = els[o];
        if (fc == AmqpFormatCode.Int || fc == 0x54)
        {
            var v = AmqpPrimitiveReader.ReadInt(els[o..], out var len);
            o += len;
            return v;
        }
        o += AmqpValueScanner.Measure(els[o..]);
        return null;
    }

    private static DateTimeOffset? ReadTimestampOrSkip(ReadOnlySpan<byte> els, ref int o)
    {
        if (PerformativeCodec.TryConsumeNull(els, ref o)) return null;
        var fc = els[o];
        if (fc == AmqpFormatCode.TimestampMs)
        {
            var ms = AmqpPrimitiveReader.ReadTimestamp(els[o..], out var len);
            o += len;
            // AMQP timestamp is signed Int64 milliseconds since the
            // Unix epoch; DateTimeOffset.FromUnixTimeMilliseconds handles
            // the full negative range.
            return DateTimeOffset.FromUnixTimeMilliseconds(ms);
        }
        o += AmqpValueScanner.Measure(els[o..]);
        return null;
    }
}
