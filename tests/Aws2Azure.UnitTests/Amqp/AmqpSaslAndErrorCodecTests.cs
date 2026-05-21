using Aws2Azure.Amqp.Codec;
using Aws2Azure.Amqp.Framing;

namespace Aws2Azure.UnitTests.Amqp;

/// <summary>
/// SASL sub-protocol performative + error + delivery-state codec
/// tests (Phase 2.5 Slice 3c). Covers the five SASL performatives,
/// the <c>error</c> composite (descriptor 0x1D) with its classifier,
/// the four terminal delivery-state outcomes, and the shared
/// <c>multiple&lt;symbol&gt;</c> helper.
/// </summary>
public sealed class AmqpSaslAndErrorCodecTests
{
    // Test methods that take internal-typed parameters (Theory + InlineData)
    // need to be `internal` so their accessibility matches; xUnit still
    // discovers them via reflection at runtime.
    // ---------- multiple<symbol> ---------------------------------------

    [Fact]
    public void MultipleSymbol_round_trips_array_form()
    {
        var symbols = new[] { "ANONYMOUS", "PLAIN", "EXTERNAL", "MSSBCBS" };
        Span<byte> buf = stackalloc byte[128];
        AmqpMultipleSymbol.Write(buf, symbols, out var written);
        var decoded = AmqpMultipleSymbol.Read(buf[..written]);
        Assert.Equal(symbols, decoded);
    }

    [Fact]
    public void MultipleSymbol_reads_single_symbol_as_one_element_array()
    {
        Span<byte> buf = stackalloc byte[16];
        AmqpVariableWriter.WriteSymbol(buf, "ANONYMOUS", out var written);
        var decoded = AmqpMultipleSymbol.Read(buf[..written]);
        Assert.Single(decoded);
        Assert.Equal("ANONYMOUS", decoded[0]);
    }

    [Fact]
    public void MultipleSymbol_reads_null_as_empty_array()
    {
        ReadOnlySpan<byte> nul = stackalloc byte[] { AmqpFormatCode.Null };
        Assert.Empty(AmqpMultipleSymbol.Read(nul));
    }

    [Fact]
    public void MultipleSymbol_rejects_non_symbol_array_element_constructor()
    {
        // Array of string instead of symbol — spec violation for capabilities.
        Span<byte> buf = stackalloc byte[16];
        ReadOnlySpan<byte> stringConstructor = stackalloc byte[] { AmqpFormatCode.String8Utf8 };
        AmqpCompoundWriter.WriteArray(buf, stringConstructor, ReadOnlySpan<byte>.Empty, 0, out var written);
        var slice = buf[..written].ToArray();
        Assert.Throws<InvalidDataException>(() => AmqpMultipleSymbol.Read(slice));
    }

    [Fact]
    public void MultipleSymbol_writer_rejects_non_ascii_symbol()
    {
        var bad = new[] { "valid", "v\u00e1l\u00eddo" };
        Assert.Throws<ArgumentException>(() =>
        {
            Span<byte> buf = stackalloc byte[64];
            AmqpMultipleSymbol.Write(buf, bad, out _);
        });
    }

    // ---------- sasl-mechanisms ----------------------------------------

    [Fact]
    public void SaslMechanisms_round_trips_three_mechanisms()
    {
        var mech = new SaslMechanisms { Mechanisms = new[] { "ANONYMOUS", "PLAIN", "MSSBCBS" } };
        var buf = new byte[64];
        SaslMechanisms.Write(buf, mech, out var written);

        SaslMechanisms.Read(buf.AsMemory(0, written), out var decoded, out var consumed);
        Assert.Equal(written, consumed);
        Assert.Equal(mech.Mechanisms, decoded.Mechanisms);
    }

    [Fact]
    public void SaslMechanisms_rejects_wrong_descriptor()
    {
        var outcome = new SaslOutcome { Code = AmqpSaslOutcomeCode.Ok };
        var buf = new byte[32];
        SaslOutcome.Write(buf, outcome, out var written);
        Assert.Throws<InvalidDataException>(() =>
        {
            SaslMechanisms.Read(buf.AsMemory(0, written), out _, out _);
        });
    }

    // ---------- sasl-init ----------------------------------------------

    [Fact]
    public void SaslInit_round_trips_mechanism_only()
    {
        var init = new SaslInit { Mechanism = "ANONYMOUS" };
        var buf = new byte[64];
        SaslInit.Write(buf, init, out var written);

        SaslInit.Read(buf.AsMemory(0, written), out var decoded, out _);
        Assert.Equal("ANONYMOUS", decoded.Mechanism);
        Assert.True(decoded.InitialResponse.IsEmpty);
        Assert.Null(decoded.Hostname);
    }

    [Fact]
    public void SaslInit_round_trips_plain_with_initial_response()
    {
        // PLAIN initial-response: 0x00 + authzid + 0x00 + authcid + 0x00 + passwd.
        var initialResponse = new byte[] { 0x00, (byte)'u', 0x00, (byte)'p' };
        var init = new SaslInit
        {
            Mechanism = "PLAIN",
            InitialResponse = initialResponse,
            Hostname = "sb.example.com",
        };
        var buf = new byte[64];
        SaslInit.Write(buf, init, out var written);

        SaslInit.Read(buf.AsMemory(0, written), out var decoded, out _);
        Assert.Equal("PLAIN", decoded.Mechanism);
        Assert.True(decoded.InitialResponse.Span.SequenceEqual(initialResponse));
        Assert.Equal("sb.example.com", decoded.Hostname);
    }

    // ---------- sasl-challenge / response ------------------------------

    [Fact]
    public void SaslChallenge_and_response_round_trip()
    {
        var payload = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };

        var challenge = new SaslChallenge { Challenge = payload };
        var cbuf = new byte[32];
        SaslChallenge.Write(cbuf, challenge, out var cWritten);
        SaslChallenge.Read(cbuf.AsMemory(0, cWritten), out var cDecoded, out _);
        Assert.True(cDecoded.Challenge.Span.SequenceEqual(payload));

        var response = new SaslResponse { Response = payload };
        var rbuf = new byte[32];
        SaslResponse.Write(rbuf, response, out var rWritten);
        SaslResponse.Read(rbuf.AsMemory(0, rWritten), out var rDecoded, out _);
        Assert.True(rDecoded.Response.Span.SequenceEqual(payload));
    }

    // ---------- sasl-outcome -------------------------------------------

    [Theory]
    [InlineData(AmqpSaslOutcomeCode.Ok)]
    [InlineData(AmqpSaslOutcomeCode.Auth)]
    [InlineData(AmqpSaslOutcomeCode.Sys)]
    [InlineData(AmqpSaslOutcomeCode.SysPerm)]
    [InlineData(AmqpSaslOutcomeCode.SysTemp)]
    internal void SaslOutcome_round_trips_each_code(AmqpSaslOutcomeCode code)
    {
        var outcome = new SaslOutcome { Code = code };
        var buf = new byte[16];
        SaslOutcome.Write(buf, outcome, out var written);

        SaslOutcome.Read(buf.AsMemory(0, written), out var decoded, out _);
        Assert.Equal(code, decoded.Code);
        Assert.True(decoded.AdditionalData.IsEmpty);
    }

    [Fact]
    public void SaslOutcome_round_trips_with_additional_data()
    {
        var extra = new byte[] { 0x01, 0x02, 0x03 };
        var outcome = new SaslOutcome { Code = AmqpSaslOutcomeCode.Auth, AdditionalData = extra };
        var buf = new byte[32];
        SaslOutcome.Write(buf, outcome, out var written);

        SaslOutcome.Read(buf.AsMemory(0, written), out var decoded, out _);
        Assert.Equal(AmqpSaslOutcomeCode.Auth, decoded.Code);
        Assert.True(decoded.AdditionalData.Span.SequenceEqual(extra));
    }

    // ---------- PeekKind dispatch (extended) ---------------------------

    [Fact]
    public void PeekKind_dispatches_sasl_descriptors()
    {
        var pairs = new (Action<byte[]> write, PerformativeKind kind, ulong descriptor)[]
        {
            (b => SaslMechanisms.Write(b, new SaslMechanisms { Mechanisms = new[] { "ANONYMOUS" } }, out _),
                PerformativeKind.SaslMechanisms, PerformativeDescriptor.SaslMechanisms),
            (b => SaslInit.Write(b, new SaslInit { Mechanism = "ANONYMOUS" }, out _),
                PerformativeKind.SaslInit, PerformativeDescriptor.SaslInit),
            (b => SaslChallenge.Write(b, new SaslChallenge { Challenge = new byte[] { 0x01 } }, out _),
                PerformativeKind.SaslChallenge, PerformativeDescriptor.SaslChallenge),
            (b => SaslResponse.Write(b, new SaslResponse { Response = new byte[] { 0x01 } }, out _),
                PerformativeKind.SaslResponse, PerformativeDescriptor.SaslResponse),
            (b => SaslOutcome.Write(b, new SaslOutcome { Code = AmqpSaslOutcomeCode.Ok }, out _),
                PerformativeKind.SaslOutcome, PerformativeDescriptor.SaslOutcome),
        };
        foreach (var (write, kind, descriptor) in pairs)
        {
            var buf = new byte[64];
            write(buf);
            var k = PerformativeCodec.PeekKind(buf, out var d);
            Assert.Equal(descriptor, d);
            Assert.Equal(kind, k);
        }
    }

    // ---------- AmqpError ----------------------------------------------

    [Fact]
    public void AmqpError_round_trips_condition_only()
    {
        var err = new AmqpError { Condition = AmqpErrorCondition.NotFound };
        var buf = new byte[64];
        AmqpError.Write(buf, err, out var written);

        AmqpError.Read(buf.AsMemory(0, written), out var decoded, out _);
        Assert.Equal(AmqpErrorCondition.NotFound, decoded.Condition);
        Assert.Null(decoded.Description);
        Assert.True(decoded.Info.IsEmpty);
    }

    [Fact]
    public void AmqpError_round_trips_with_description()
    {
        var err = new AmqpError
        {
            Condition = AmqpErrorCondition.ServerBusy,
            Description = "Throttled by server; retry in 5 seconds.",
        };
        var buf = new byte[128];
        AmqpError.Write(buf, err, out var written);

        AmqpError.Read(buf.AsMemory(0, written), out var decoded, out _);
        Assert.Equal(AmqpErrorCondition.ServerBusy, decoded.Condition);
        Assert.Equal(err.Description, decoded.Description);
    }

    [Fact]
    public void AmqpError_Kind_property_uses_classifier()
    {
        var err = new AmqpError { Condition = AmqpErrorCondition.SessionLockLost };
        Assert.Equal(AmqpErrorKind.LockLost, err.Kind);
    }

    // ---------- AmqpErrorClassifier ------------------------------------

    [Theory]
    [InlineData(AmqpErrorCondition.InternalError, AmqpErrorKind.Transient)]
    [InlineData(AmqpErrorCondition.ConnectionForced, AmqpErrorKind.Transient)]
    [InlineData(AmqpErrorCondition.Timeout, AmqpErrorKind.Transient)]
    [InlineData(AmqpErrorCondition.ServerBusy, AmqpErrorKind.Throttled)]
    [InlineData(AmqpErrorCondition.ResourceLimitExceeded, AmqpErrorKind.Throttled)]
    [InlineData(AmqpErrorCondition.UnauthorizedAccess, AmqpErrorKind.Auth)]
    [InlineData(AmqpErrorCondition.TokenExpired, AmqpErrorKind.Auth)]
    [InlineData(AmqpErrorCondition.MessageLockLost, AmqpErrorKind.LockLost)]
    [InlineData(AmqpErrorCondition.SessionLockLost, AmqpErrorKind.LockLost)]
    [InlineData(AmqpErrorCondition.ConnectionRedirect, AmqpErrorKind.Redirect)]
    [InlineData(AmqpErrorCondition.LinkRedirect, AmqpErrorKind.Redirect)]
    [InlineData(AmqpErrorCondition.NotFound, AmqpErrorKind.ServerFatal)]
    [InlineData(AmqpErrorCondition.DecodeError, AmqpErrorKind.ClientFatal)]
    [InlineData(AmqpErrorCondition.LinkMessageSizeExceeded, AmqpErrorKind.ClientFatal)]
    [InlineData("amqp:not-a-real-condition", AmqpErrorKind.Unknown)]
    [InlineData(null, AmqpErrorKind.Unknown)]
    internal void Classifier_maps_known_conditions(string? condition, AmqpErrorKind expected)
    {
        Assert.Equal(expected, AmqpErrorClassifier.Classify(condition));
    }

    // ---------- AmqpErrorClassifier (alloc-free byte overload) ---------

    /// <summary>
    /// Cross-check that every condition the string overload knows about
    /// is also classified identically by the <see cref="ReadOnlySpan{Byte}"/>
    /// overload (and shares the same UTF-8 bytes). Catches drift between
    /// <see cref="AmqpErrorCondition"/> and
    /// <see cref="AmqpErrorConditionU8"/>.
    /// </summary>
    [Theory]
    [InlineData(AmqpErrorCondition.InternalError)]
    [InlineData(AmqpErrorCondition.NotFound)]
    [InlineData(AmqpErrorCondition.UnauthorizedAccess)]
    [InlineData(AmqpErrorCondition.DecodeError)]
    [InlineData(AmqpErrorCondition.ResourceLimitExceeded)]
    [InlineData(AmqpErrorCondition.NotAllowed)]
    [InlineData(AmqpErrorCondition.InvalidField)]
    [InlineData(AmqpErrorCondition.NotImplemented)]
    [InlineData(AmqpErrorCondition.ResourceLocked)]
    [InlineData(AmqpErrorCondition.PreconditionFailed)]
    [InlineData(AmqpErrorCondition.ResourceDeleted)]
    [InlineData(AmqpErrorCondition.IllegalState)]
    [InlineData(AmqpErrorCondition.FrameSizeTooSmall)]
    [InlineData(AmqpErrorCondition.ConnectionForced)]
    [InlineData(AmqpErrorCondition.ConnectionFramingError)]
    [InlineData(AmqpErrorCondition.ConnectionRedirect)]
    [InlineData(AmqpErrorCondition.SessionWindowViolation)]
    [InlineData(AmqpErrorCondition.SessionErrantLink)]
    [InlineData(AmqpErrorCondition.SessionHandleInUse)]
    [InlineData(AmqpErrorCondition.SessionUnattachedHandle)]
    [InlineData(AmqpErrorCondition.LinkDetachForced)]
    [InlineData(AmqpErrorCondition.LinkTransferLimitExceeded)]
    [InlineData(AmqpErrorCondition.LinkMessageSizeExceeded)]
    [InlineData(AmqpErrorCondition.LinkRedirect)]
    [InlineData(AmqpErrorCondition.LinkStolen)]
    [InlineData(AmqpErrorCondition.ServerBusy)]
    [InlineData(AmqpErrorCondition.ArgumentError)]
    [InlineData(AmqpErrorCondition.ArgumentOutOfRange)]
    [InlineData(AmqpErrorCondition.Timeout)]
    [InlineData(AmqpErrorCondition.MessageLockLost)]
    [InlineData(AmqpErrorCondition.SessionLockLost)]
    [InlineData(AmqpErrorCondition.SessionCannotBeLocked)]
    [InlineData(AmqpErrorCondition.EntityDisabled)]
    [InlineData(AmqpErrorCondition.PublisherRevoked)]
    [InlineData(AmqpErrorCondition.PartitionNotOwned)]
    [InlineData(AmqpErrorCondition.StoreLockLost)]
    [InlineData(AmqpErrorCondition.TokenExpired)]
    public void Classifier_byte_overload_matches_string_overload(string condition)
    {
        var bytes = System.Text.Encoding.ASCII.GetBytes(condition);
        Assert.Equal(
            AmqpErrorClassifier.Classify(condition),
            AmqpErrorClassifier.Classify(bytes.AsSpan()));
    }

    [Fact]
    public void Classifier_byte_overload_returns_Unknown_for_unknown_or_empty()
    {
        Assert.Equal(AmqpErrorKind.Unknown,
            AmqpErrorClassifier.Classify(System.Text.Encoding.ASCII.GetBytes("amqp:not-a-real-condition").AsSpan()));
        Assert.Equal(AmqpErrorKind.Unknown,
            AmqpErrorClassifier.Classify(ReadOnlySpan<byte>.Empty));
    }

    // ---------- AmqpMultipleSymbol enumerator / ContainsSymbol ---------

    [Fact]
    public void MultipleSymbol_Enumerate_yields_each_symbol_without_allocating_strings()
    {
        var symbols = new[] { "ANONYMOUS", "PLAIN", "EXTERNAL", "MSSBCBS" };
        var buf = new byte[128];
        AmqpMultipleSymbol.Write(buf, symbols, out var written);

        var seen = new List<string>();
        foreach (var sym in AmqpMultipleSymbol.Enumerate(buf.AsSpan(0, written)))
        {
            seen.Add(System.Text.Encoding.ASCII.GetString(sym));
        }
        Assert.Equal(symbols, seen);
    }

    [Fact]
    public void MultipleSymbol_Enumerate_yields_single_symbol_form()
    {
        var buf = new byte[16];
        AmqpVariableWriter.WriteSymbol(buf, "PLAIN", out var written);

        var seen = new List<string>();
        foreach (var sym in AmqpMultipleSymbol.Enumerate(buf.AsSpan(0, written)))
        {
            seen.Add(System.Text.Encoding.ASCII.GetString(sym));
        }
        Assert.Single(seen);
        Assert.Equal("PLAIN", seen[0]);
    }

    [Fact]
    public void MultipleSymbol_Enumerate_null_yields_no_elements()
    {
        ReadOnlySpan<byte> nul = stackalloc byte[] { AmqpFormatCode.Null };
        var enumerator = AmqpMultipleSymbol.Enumerate(nul);
        Assert.False(enumerator.MoveNext());
    }

    [Fact]
    public void MultipleSymbol_ContainsSymbol_matches_array_form()
    {
        var symbols = new[] { "ANONYMOUS", "PLAIN", "MSSBCBS" };
        var buf = new byte[64];
        AmqpMultipleSymbol.Write(buf, symbols, out var written);
        var wire = buf.AsSpan(0, written);

        Assert.True(AmqpMultipleSymbol.ContainsSymbol(wire, "ANONYMOUS"u8));
        Assert.True(AmqpMultipleSymbol.ContainsSymbol(wire, "MSSBCBS"u8));
        Assert.False(AmqpMultipleSymbol.ContainsSymbol(wire, "EXTERNAL"u8));
        Assert.False(AmqpMultipleSymbol.ContainsSymbol(wire, "ANONYMOU"u8));   // prefix only
        Assert.False(AmqpMultipleSymbol.ContainsSymbol(wire, "ANONYMOUS!"u8)); // longer
    }

    [Fact]
    public void MultipleSymbol_ContainsSymbol_matches_single_symbol_form()
    {
        var buf = new byte[16];
        AmqpVariableWriter.WriteSymbol(buf, "PLAIN", out var written);
        Assert.True(AmqpMultipleSymbol.ContainsSymbol(buf.AsSpan(0, written), "PLAIN"u8));
        Assert.False(AmqpMultipleSymbol.ContainsSymbol(buf.AsSpan(0, written), "ANONYMOUS"u8));
    }

    [Fact]
    public void MultipleSymbol_ContainsSymbol_null_returns_false()
    {
        ReadOnlySpan<byte> nul = stackalloc byte[] { AmqpFormatCode.Null };
        Assert.False(AmqpMultipleSymbol.ContainsSymbol(nul, "PLAIN"u8));
    }

    // ---------- Delivery states ----------------------------------------

    [Fact]
    public void Accepted_encodes_as_described_list0()
    {
        var buf = new byte[16];
        Accepted.Write(buf, out var written);
        // 0x00 + smallulong(0x24) + list0 = 4 bytes.
        Assert.Equal(4, written);
        Assert.Equal(AmqpFormatCode.Described, buf[0]);
        Assert.Equal(AmqpFormatCode.List0, buf[3]);

        Accepted.Read(buf.AsMemory(0, written), out var consumed);
        Assert.Equal(written, consumed);
    }

    [Fact]
    public void Released_encodes_as_described_list0()
    {
        var buf = new byte[16];
        Released.Write(buf, out var written);
        Assert.Equal(4, written);
        Assert.Equal(AmqpFormatCode.List0, buf[3]);
        Released.Read(buf.AsMemory(0, written), out _);
    }

    [Fact]
    public void Rejected_round_trips_with_typed_error()
    {
        // Build the error blob via AmqpError.Write so we exercise the
        // typed -> opaque -> typed round trip the receive path will use.
        Span<byte> errorBlob = stackalloc byte[64];
        AmqpError.Write(errorBlob,
            new AmqpError { Condition = AmqpErrorCondition.ArgumentError, Description = "bad input" },
            out var errLen);

        var rejected = new Rejected { Error = errorBlob[..errLen].ToArray() };
        var buf = new byte[128];
        Rejected.Write(buf, rejected, out var written);

        Rejected.Read(buf.AsMemory(0, written), out var decoded, out _);
        Assert.False(decoded.Error.IsEmpty);

        AmqpError.Read(decoded.Error, out var decodedError, out _);
        Assert.Equal(AmqpErrorCondition.ArgumentError, decodedError.Condition);
        Assert.Equal("bad input", decodedError.Description);
        Assert.Equal(AmqpErrorKind.ClientFatal, decodedError.Kind);
    }

    [Fact]
    public void Modified_round_trips_delivery_failed_flag()
    {
        var modified = new Modified { DeliveryFailed = true, UndeliverableHere = false };
        var buf = new byte[32];
        Modified.Write(buf, modified, out var written);

        Modified.Read(buf.AsMemory(0, written), out var decoded, out _);
        Assert.True(decoded.DeliveryFailed);
        Assert.False(decoded.UndeliverableHere);
    }

    [Fact]
    public void DeliveryState_PeekKind_dispatches_each_terminal_outcome()
    {
        var cases = new (Action<byte[]> write, DeliveryStateKind kind, ulong descriptor)[]
        {
            (b => Accepted.Write(b, out _), DeliveryStateKind.Accepted, DeliveryStateDescriptor.Accepted),
            (b => Released.Write(b, out _), DeliveryStateKind.Released, DeliveryStateDescriptor.Released),
            (b => Rejected.Write(b, default, out _), DeliveryStateKind.Rejected, DeliveryStateDescriptor.Rejected),
            (b => Modified.Write(b, default, out _), DeliveryStateKind.Modified, DeliveryStateDescriptor.Modified),
        };
        foreach (var (write, kind, descriptor) in cases)
        {
            var buf = new byte[32];
            write(buf);
            var k = DeliveryState.PeekKind(buf, out var d);
            Assert.Equal(descriptor, d);
            Assert.Equal(kind, k);
        }
    }
}
