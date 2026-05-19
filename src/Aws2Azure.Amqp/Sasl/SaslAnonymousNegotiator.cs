using System.Buffers;
using Aws2Azure.Amqp.Framing;
using Aws2Azure.Amqp.Transport;

namespace Aws2Azure.Amqp.Sasl;

/// <summary>
/// Drives the AMQP SASL sub-protocol (§5.3) for the ANONYMOUS mechanism
/// (RFC 4505). Service Bus accepts ANONYMOUS as the SASL layer and
/// expects authorization to happen later via the <c>$cbs</c> management
/// link with a SAS token (handled in Slice 5).
/// </summary>
internal static class SaslAnonymousNegotiator
{
    /// <summary>The SASL mechanism name we propose (RFC 4505).</summary>
    public const string Mechanism = "ANONYMOUS";

    /// <summary>
    /// Runs the full SASL negotiation on <paramref name="transport"/>:
    /// SASL header exchange → read <c>sasl-mechanisms</c> → send
    /// <c>sasl-init</c> with ANONYMOUS → read <c>sasl-outcome</c> →
    /// AMQP header exchange. On any failure the transport is left in an
    /// indeterminate state and the caller should dispose it.
    /// </summary>
    /// <param name="transport">The duplex transport (post-TCP/TLS handshake).</param>
    /// <param name="trace">
    /// Optional identity sent as the <c>initial-response</c> for
    /// ANONYMOUS (per RFC 4505: an arbitrary trace identifier, often the
    /// process name). Empty when null.
    /// </param>
    public static async ValueTask NegotiateAsync(
        IAmqpTransport transport,
        string? trace = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transport);

        // Step 1: SASL protocol header exchange (§5.3.2).
        await ProtocolHeaderHandshake.PerformAsync(
            transport, AmqpFrameCodec.SaslProtocolHeader.ToArray(), cancellationToken).ConfigureAwait(false);

        // Step 2: read sasl-mechanisms from server.
        SaslMechanisms mechanisms;
        using (var mechFrame = await AmqpFrameIO.ReadFrameAsync(transport, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            EnsureSaslFrame(mechFrame, "sasl-mechanisms");
            var kind = PerformativeCodec.PeekKind(mechFrame.Body.Span, out _);
            if (kind != PerformativeKind.SaslMechanisms)
                throw new AmqpSaslAuthenticationException($"Expected sasl-mechanisms from peer, got {kind}.");
            SaslMechanisms.Read(mechFrame.Body, out mechanisms, out _);
        }

        if (!ContainsAnonymous(mechanisms.Mechanisms))
        {
            throw new AmqpSaslAuthenticationException(
                $"Peer does not offer SASL ANONYMOUS. Offered: [{string.Join(", ", mechanisms.Mechanisms ?? Array.Empty<string>())}].");
        }

        // Step 3: send sasl-init.
        await SendSaslInitAsync(transport, trace, cancellationToken).ConfigureAwait(false);

        // Step 4: read sasl-outcome.
        using (var outcomeFrame = await AmqpFrameIO.ReadFrameAsync(transport, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            EnsureSaslFrame(outcomeFrame, "sasl-outcome");
            var kind = PerformativeCodec.PeekKind(outcomeFrame.Body.Span, out _);
            if (kind == PerformativeKind.SaslChallenge)
            {
                // ANONYMOUS is a single-round mechanism; a challenge here is a protocol violation.
                throw new AmqpSaslAuthenticationException(
                    "Peer sent sasl-challenge for ANONYMOUS (single-round mechanism per RFC 4505).");
            }
            if (kind != PerformativeKind.SaslOutcome)
                throw new AmqpSaslAuthenticationException($"Expected sasl-outcome from peer, got {kind}.");
            SaslOutcome.Read(outcomeFrame.Body, out var outcome, out _);
            if (outcome.Code != AmqpSaslOutcomeCode.Ok)
            {
                throw new AmqpSaslAuthenticationException(
                    $"SASL negotiation failed with outcome code {outcome.Code} (0x{(byte)outcome.Code:X2}).")
                { OutcomeCode = (byte)outcome.Code };
            }
        }

        // Step 5: AMQP protocol header exchange (§2.2).
        await ProtocolHeaderHandshake.PerformAsync(
            transport, AmqpFrameCodec.AmqpProtocolHeader.ToArray(), cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask SendSaslInitAsync(
        IAmqpTransport transport, string? trace, CancellationToken ct)
    {
        // ANONYMOUS initial-response: any UTF-8 trace identifier per RFC 4505.
        // Empty trace is permitted; that's what we send by default.
        var initialResponse = trace is null or "" ? ReadOnlyMemory<byte>.Empty
            : System.Text.Encoding.UTF8.GetBytes(trace);

        var init = new SaslInit
        {
            Mechanism = Mechanism,
            InitialResponse = initialResponse,
            Hostname = null,
        };

        var rented = ArrayPool<byte>.Shared.Rent(AmqpFrameIO.InitialMaxFrameSize);
        try
        {
            SaslInit.Write(rented, in init, out var bodyWritten);
            await AmqpFrameIO.WriteFrameAsync(
                transport, AmqpFrameType.Sasl, channel: 0,
                rented.AsMemory(0, bodyWritten), ct).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static void EnsureSaslFrame(RentedFrame frame, string expected)
    {
        if (frame.Header.Type != AmqpFrameType.Sasl)
            throw new AmqpSaslAuthenticationException(
                $"Expected SASL frame ({expected}), got type {frame.Header.Type}.");
    }

    private static bool ContainsAnonymous(string[]? mechanisms)
    {
        if (mechanisms is null) return false;
        for (var i = 0; i < mechanisms.Length; i++)
        {
            if (string.Equals(mechanisms[i], Mechanism, StringComparison.Ordinal))
                return true;
        }
        return false;
    }
}
