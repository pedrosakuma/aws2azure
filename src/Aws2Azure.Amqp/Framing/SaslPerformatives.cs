using Aws2Azure.Amqp.Codec;

namespace Aws2Azure.Amqp.Framing;

/// <summary>
/// AMQP 1.0 SASL sub-protocol performatives (§5.3.3), descriptors
/// 0x40–0x44. Same codec shape as the transport performatives in
/// <see cref="Performatives"/>: each is a <c>readonly record struct</c>
/// with a <c>Descriptor</c> constant and static <c>Write</c>/<c>Read</c>
/// methods. SASL frames carry exactly one of these performatives in
/// their body (see <see cref="AmqpFrameType.Sasl"/>).
/// </summary>
internal static class SaslPerformatives
{
}

// =====================================================================
// sasl-mechanisms (0x40) — server -> client list of offered SASL mechanisms
// =====================================================================

internal readonly record struct SaslMechanisms
{
    public const ulong Descriptor = PerformativeDescriptor.SaslMechanisms;

    /// <summary>
    /// REQUIRED. List of SASL mechanism names the server offers
    /// (e.g. <c>ANONYMOUS</c>, <c>PLAIN</c>, <c>EXTERNAL</c>,
    /// <c>MSSBCBS</c>). Must contain at least one entry per spec.
    /// </summary>
    public string[] Mechanisms { get; init; }

    public static void Write(Span<byte> destination, in SaslMechanisms value, out int written)
    {
        Span<byte> scratch = stackalloc byte[Performatives.ScratchSize];
        Span<int> offsets = stackalloc int[2];
        int o = 0;
        int len;

        offsets[0] = o;
        AmqpMultipleSymbol.Write(scratch[o..], value.Mechanisms ?? Array.Empty<string>(), out len);
        o += len;

        offsets[1] = o;
        written = PerformativeCodec.WritePerformative(destination, Descriptor, scratch[..o], offsets, 1);
    }

    public static void Read(ReadOnlyMemory<byte> source, out SaslMechanisms value, out int consumed)
    {
        var span = source.Span;
        var view = PerformativeCodec.ReadPerformativeFields(
            span, Descriptor, out _, out consumed);
        if (view.Count < 1)
        {
            throw new InvalidDataException("sasl-mechanisms.server-mechanisms is required.");
        }
        var mechanisms = AmqpMultipleSymbol.Read(view.Elements);
        value = new SaslMechanisms { Mechanisms = mechanisms };
    }
}

// =====================================================================
// sasl-init (0x41) — client -> server mechanism selection
// =====================================================================

internal readonly record struct SaslInit
{
    public const ulong Descriptor = PerformativeDescriptor.SaslInit;

    /// <summary>REQUIRED. The mechanism the client selected (symbol).</summary>
    public string Mechanism { get; init; }
    /// <summary>Optional initial response (binary). Empty &#8801; absent.</summary>
    public ReadOnlyMemory<byte> InitialResponse { get; init; }
    /// <summary>Optional hostname (string).</summary>
    public string? Hostname { get; init; }

    public static void Write(Span<byte> destination, in SaslInit value, out int written)
    {
        Span<byte> scratch = stackalloc byte[Performatives.ScratchSize];
        Span<int> offsets = stackalloc int[4];
        int o = 0;
        int len;

        offsets[0] = o;
        AmqpVariableWriter.WriteSymbol(scratch[o..], value.Mechanism ?? string.Empty, out len); o += len;

        offsets[1] = o;
        PerformativeCodec.WriteBinaryOrNull(scratch[o..], value.InitialResponse.Span, !value.InitialResponse.IsEmpty, out len);
        o += len;

        offsets[2] = o;
        PerformativeCodec.WriteStringOrNull(scratch[o..], value.Hostname, out len); o += len;

        offsets[3] = o;
        written = PerformativeCodec.WritePerformative(destination, Descriptor, scratch[..o], offsets, 3);
    }

    public static void Read(ReadOnlyMemory<byte> source, out SaslInit value, out int consumed)
    {
        var span = source.Span;
        var view = PerformativeCodec.ReadPerformativeFields(
            span, Descriptor, out var elementsOffset, out consumed);
        var els = view.Elements;
        int o = 0;
        int len;

        if (view.Count < 1) throw new InvalidDataException("sasl-init.mechanism is required.");
        var mechanism = AmqpVariableReader.ReadSymbol(els[o..], out len); o += len;

        ReadOnlyMemory<byte> initialResponse = ReadOnlyMemory<byte>.Empty;
        if (view.Count >= 2 && !PerformativeCodec.TryConsumeNull(els, ref o))
        {
            var binLen = AmqpValueScanner.Measure(els[o..]);
            var binPayloadStart = els[o] == AmqpFormatCode.Binary8 ? 2 : 5;
            initialResponse = source.Slice(elementsOffset + o + binPayloadStart, binLen - binPayloadStart);
            o += binLen;
        }

        string? hostname = null;
        if (view.Count >= 3 && !PerformativeCodec.TryConsumeNull(els, ref o))
        {
            hostname = AmqpVariableReader.ReadString(els[o..], out len); o += len;
        }

        value = new SaslInit
        {
            Mechanism = mechanism,
            InitialResponse = initialResponse,
            Hostname = hostname,
        };
    }
}

// =====================================================================
// sasl-challenge (0x42) — server -> client challenge bytes
// =====================================================================

internal readonly record struct SaslChallenge
{
    public const ulong Descriptor = PerformativeDescriptor.SaslChallenge;

    /// <summary>REQUIRED. Opaque challenge payload (binary). Empty allowed but field is mandatory.</summary>
    public ReadOnlyMemory<byte> Challenge { get; init; }

    public static void Write(Span<byte> destination, in SaslChallenge value, out int written)
    {
        Span<byte> scratch = stackalloc byte[Performatives.ScratchSize];
        Span<int> offsets = stackalloc int[2];
        int o = 0;
        int len;

        offsets[0] = o;
        AmqpVariableWriter.WriteBinary(scratch[o..], value.Challenge.Span, out len); o += len;

        offsets[1] = o;
        written = PerformativeCodec.WritePerformative(destination, Descriptor, scratch[..o], offsets, 1);
    }

    public static void Read(ReadOnlyMemory<byte> source, out SaslChallenge value, out int consumed)
    {
        var span = source.Span;
        var view = PerformativeCodec.ReadPerformativeFields(
            span, Descriptor, out var elementsOffset, out consumed);
        var els = view.Elements;
        if (view.Count < 1) throw new InvalidDataException("sasl-challenge.challenge is required.");
        var binLen = AmqpValueScanner.Measure(els);
        var binPayloadStart = els[0] == AmqpFormatCode.Binary8 ? 2 : 5;
        var challenge = source.Slice(elementsOffset + binPayloadStart, binLen - binPayloadStart);
        value = new SaslChallenge { Challenge = challenge };
    }
}

// =====================================================================
// sasl-response (0x43) — client -> server response bytes
// =====================================================================

internal readonly record struct SaslResponse
{
    public const ulong Descriptor = PerformativeDescriptor.SaslResponse;

    /// <summary>REQUIRED. Opaque response payload (binary). Empty allowed but field is mandatory.</summary>
    public ReadOnlyMemory<byte> Response { get; init; }

    public static void Write(Span<byte> destination, in SaslResponse value, out int written)
    {
        Span<byte> scratch = stackalloc byte[Performatives.ScratchSize];
        Span<int> offsets = stackalloc int[2];
        int o = 0;
        int len;

        offsets[0] = o;
        AmqpVariableWriter.WriteBinary(scratch[o..], value.Response.Span, out len); o += len;

        offsets[1] = o;
        written = PerformativeCodec.WritePerformative(destination, Descriptor, scratch[..o], offsets, 1);
    }

    public static void Read(ReadOnlyMemory<byte> source, out SaslResponse value, out int consumed)
    {
        var span = source.Span;
        var view = PerformativeCodec.ReadPerformativeFields(
            span, Descriptor, out var elementsOffset, out consumed);
        var els = view.Elements;
        if (view.Count < 1) throw new InvalidDataException("sasl-response.response is required.");
        var binLen = AmqpValueScanner.Measure(els);
        var binPayloadStart = els[0] == AmqpFormatCode.Binary8 ? 2 : 5;
        var response = source.Slice(elementsOffset + binPayloadStart, binLen - binPayloadStart);
        value = new SaslResponse { Response = response };
    }
}

// =====================================================================
// sasl-outcome (0x44) — server -> client final SASL result
// =====================================================================

internal readonly record struct SaslOutcome
{
    public const ulong Descriptor = PerformativeDescriptor.SaslOutcome;

    /// <summary>REQUIRED. SASL outcome code; <see cref="AmqpSaslOutcomeCode.Ok"/> means proceed to AMQP header exchange.</summary>
    public AmqpSaslOutcomeCode Code { get; init; }
    /// <summary>Optional additional data (binary). Empty &#8801; absent.</summary>
    public ReadOnlyMemory<byte> AdditionalData { get; init; }

    public static void Write(Span<byte> destination, in SaslOutcome value, out int written)
    {
        Span<byte> scratch = stackalloc byte[Performatives.ScratchSize];
        Span<int> offsets = stackalloc int[3];
        int o = 0;
        int len;

        offsets[0] = o;
        AmqpPrimitiveWriter.WriteUByte(scratch[o..], (byte)value.Code, out len); o += len;

        offsets[1] = o;
        PerformativeCodec.WriteBinaryOrNull(scratch[o..], value.AdditionalData.Span, !value.AdditionalData.IsEmpty, out len);
        o += len;

        offsets[2] = o;
        written = PerformativeCodec.WritePerformative(destination, Descriptor, scratch[..o], offsets, 2);
    }

    public static void Read(ReadOnlyMemory<byte> source, out SaslOutcome value, out int consumed)
    {
        var span = source.Span;
        var view = PerformativeCodec.ReadPerformativeFields(
            span, Descriptor, out var elementsOffset, out consumed);
        var els = view.Elements;
        int o = 0;
        int len;

        if (view.Count < 1) throw new InvalidDataException("sasl-outcome.code is required.");
        var code = (AmqpSaslOutcomeCode)AmqpPrimitiveReader.ReadUByte(els[o..], out len); o += len;

        ReadOnlyMemory<byte> additional = ReadOnlyMemory<byte>.Empty;
        if (view.Count >= 2 && !PerformativeCodec.TryConsumeNull(els, ref o))
        {
            var binLen = AmqpValueScanner.Measure(els[o..]);
            var binPayloadStart = els[o] == AmqpFormatCode.Binary8 ? 2 : 5;
            additional = source.Slice(elementsOffset + o + binPayloadStart, binLen - binPayloadStart);
            o += binLen;
        }

        value = new SaslOutcome { Code = code, AdditionalData = additional };
    }
}
