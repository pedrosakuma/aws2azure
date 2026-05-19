using System.Buffers;
using Aws2Azure.Amqp.Framing;
using Aws2Azure.Amqp.Sasl;
using Aws2Azure.Amqp.Transport;
using Aws2Azure.UnitTests.Amqp.Transport;

namespace Aws2Azure.UnitTests.Amqp.Sasl;

/// <summary>
/// Tests the SASL ANONYMOUS state machine. The "server" side here is
/// implemented manually inside each test using the same primitives the
/// client uses, exercising the full wire path.
/// </summary>
public sealed class SaslAnonymousNegotiatorTests
{
    [Fact]
    public async Task Happy_path_completes_full_negotiation()
    {
        var (client, server) = PipePairTransport.CreatePair();
        await using var _ = client; await using var __ = server;

        var clientTask = SaslAnonymousNegotiator.NegotiateAsync(client, trace: "unit-test");
        var serverTask = RunServerHappyPath(server, expectedTrace: "unit-test");

        await Task.WhenAll(clientTask.AsTask(), serverTask);
    }

    [Fact]
    public async Task Empty_trace_sends_empty_initial_response()
    {
        var (client, server) = PipePairTransport.CreatePair();
        await using var _ = client; await using var __ = server;

        var clientTask = SaslAnonymousNegotiator.NegotiateAsync(client);
        var serverTask = RunServerHappyPath(server, expectedTrace: null);

        await Task.WhenAll(clientTask.AsTask(), serverTask);
    }

    [Fact]
    public async Task Server_without_anonymous_throws()
    {
        var (client, server) = PipePairTransport.CreatePair();
        await using var _ = client; await using var __ = server;

        var clientTask = SaslAnonymousNegotiator.NegotiateAsync(client);
        var serverTask = Task.Run(async () =>
        {
            await ProtocolHeaderHandshake.PerformAsync(server, AmqpFrameCodec.SaslProtocolHeader.ToArray());
            await SendMechanismsAsync(server, "PLAIN", "EXTERNAL");
        });

        var ex = await Assert.ThrowsAsync<AmqpSaslAuthenticationException>(() => clientTask.AsTask());
        Assert.Contains("ANONYMOUS", ex.Message);
        await serverTask;
    }

    [Fact]
    public async Task Outcome_auth_failed_throws_with_code()
    {
        var (client, server) = PipePairTransport.CreatePair();
        await using var _ = client; await using var __ = server;

        var clientTask = SaslAnonymousNegotiator.NegotiateAsync(client);
        var serverTask = Task.Run(async () =>
        {
            await ProtocolHeaderHandshake.PerformAsync(server, AmqpFrameCodec.SaslProtocolHeader.ToArray());
            await SendMechanismsAsync(server, "ANONYMOUS");
            using var _init = await AmqpFrameIO.ReadFrameAsync(server);
            await SendOutcomeAsync(server, AmqpSaslOutcomeCode.Auth);
        });

        var ex = await Assert.ThrowsAsync<AmqpSaslAuthenticationException>(() => clientTask.AsTask());
        Assert.Equal((byte)AmqpSaslOutcomeCode.Auth, ex.OutcomeCode);
        await serverTask;
    }

    [Fact]
    public async Task Unexpected_challenge_for_anonymous_throws()
    {
        var (client, server) = PipePairTransport.CreatePair();
        await using var _ = client; await using var __ = server;

        var clientTask = SaslAnonymousNegotiator.NegotiateAsync(client);
        var serverTask = Task.Run(async () =>
        {
            await ProtocolHeaderHandshake.PerformAsync(server, AmqpFrameCodec.SaslProtocolHeader.ToArray());
            await SendMechanismsAsync(server, "ANONYMOUS");
            using var _init = await AmqpFrameIO.ReadFrameAsync(server);
            await SendChallengeAsync(server, new byte[] { 1, 2, 3 });
        });

        var ex = await Assert.ThrowsAsync<AmqpSaslAuthenticationException>(() => clientTask.AsTask());
        Assert.Contains("challenge", ex.Message, StringComparison.OrdinalIgnoreCase);
        await serverTask;
    }

    [Fact]
    public async Task Wrong_header_from_peer_throws_protocol_mismatch()
    {
        var (client, server) = PipePairTransport.CreatePair();
        await using var _ = client; await using var __ = server;

        var clientTask = SaslAnonymousNegotiator.NegotiateAsync(client);
        // Server sends AMQP header instead of SASL header.
        var serverTask = ProtocolHeaderHandshake.PerformAsync(server, AmqpFrameCodec.AmqpProtocolHeader.ToArray());

        await Assert.ThrowsAsync<AmqpProtocolHeaderMismatchException>(() => clientTask.AsTask());
        await Assert.ThrowsAsync<AmqpProtocolHeaderMismatchException>(() => serverTask.AsTask());
    }

    [Fact]
    public async Task Null_transport_throws_ArgumentNullException()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            SaslAnonymousNegotiator.NegotiateAsync(null!).AsTask());
    }

    // ---- server-side helpers ----

    private static async Task RunServerHappyPath(IAmqpTransport server, string? expectedTrace)
    {
        await ProtocolHeaderHandshake.PerformAsync(server, AmqpFrameCodec.SaslProtocolHeader.ToArray());
        await SendMechanismsAsync(server, "ANONYMOUS", "PLAIN");

        using (var initFrame = await AmqpFrameIO.ReadFrameAsync(server))
        {
            Assert.Equal(AmqpFrameType.Sasl, initFrame.Header.Type);
            SaslInit.Read(initFrame.Body, out var init, out _);
            Assert.Equal("ANONYMOUS", init.Mechanism);
            if (expectedTrace is null)
                Assert.True(init.InitialResponse.IsEmpty);
            else
                Assert.Equal(expectedTrace, System.Text.Encoding.UTF8.GetString(init.InitialResponse.Span));
        }

        await SendOutcomeAsync(server, AmqpSaslOutcomeCode.Ok);
        await ProtocolHeaderHandshake.PerformAsync(server, AmqpFrameCodec.AmqpProtocolHeader.ToArray());
    }

    private static async Task SendMechanismsAsync(IAmqpTransport transport, params string[] mechanisms)
    {
        var perf = new SaslMechanisms { Mechanisms = mechanisms };
        var rented = ArrayPool<byte>.Shared.Rent(AmqpFrameIO.InitialMaxFrameSize);
        try
        {
            SaslMechanisms.Write(rented, in perf, out var written);
            await AmqpFrameIO.WriteFrameAsync(transport, AmqpFrameType.Sasl, 0, rented.AsMemory(0, written));
        }
        finally { ArrayPool<byte>.Shared.Return(rented); }
    }

    private static async Task SendOutcomeAsync(IAmqpTransport transport, AmqpSaslOutcomeCode code)
    {
        var perf = new SaslOutcome { Code = code };
        var rented = ArrayPool<byte>.Shared.Rent(AmqpFrameIO.InitialMaxFrameSize);
        try
        {
            SaslOutcome.Write(rented, in perf, out var written);
            await AmqpFrameIO.WriteFrameAsync(transport, AmqpFrameType.Sasl, 0, rented.AsMemory(0, written));
        }
        finally { ArrayPool<byte>.Shared.Return(rented); }
    }

    private static async Task SendChallengeAsync(IAmqpTransport transport, byte[] challenge)
    {
        var perf = new SaslChallenge { Challenge = challenge };
        var rented = ArrayPool<byte>.Shared.Rent(AmqpFrameIO.InitialMaxFrameSize);
        try
        {
            SaslChallenge.Write(rented, in perf, out var written);
            await AmqpFrameIO.WriteFrameAsync(transport, AmqpFrameType.Sasl, 0, rented.AsMemory(0, written));
        }
        finally { ArrayPool<byte>.Shared.Return(rented); }
    }
}
