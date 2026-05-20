using System.Buffers;
using System.Text;
using Aws2Azure.Amqp.Connection;
using Aws2Azure.Amqp.Framing;
using Aws2Azure.Amqp.Security;
using Aws2Azure.Amqp.Transport;
using Aws2Azure.UnitTests.Amqp.Transport;

namespace Aws2Azure.UnitTests.Amqp.Security;

/// <summary>
/// Integration-style tests for <see cref="CbsAuthenticator"/> against
/// an in-process AMQP broker stub that mimics Service Bus
/// <c>$cbs</c>: validates the put-token request shape and returns
/// a configurable status-code.
/// </summary>
public sealed class CbsAuthenticatorTests
{
    private const string KeyName = "RootManageSharedAccessKey";
    private const string KeyValue = "secret-key-value-12345";

    private static AmqpConnectionSettings DefaultConnSettings() => new()
    {
        ContainerId = "test-client",
        Hostname = "ns.servicebus.windows.net",
        IdleTimeout = TimeSpan.Zero,
    };

    [Fact]
    public async Task PutTokenAsync_succeeds_on_202()
    {
        var (clientTransport, serverTransport) = PipePairTransport.CreatePair();
        await using var server = serverTransport;
        var conn = new AmqpConnection(clientTransport, DefaultConnSettings());

        Dictionary<string, object?>? capturedAppProps = null;
        string? capturedBody = null;

        var serverTask = Task.Run(async () => await RunCbsBrokerAsync(
            server,
            status: 202,
            description: "Accepted",
            capture: (ap, body) => { capturedAppProps = ap; capturedBody = body; }));

        await conn.OpenAsync();
        var session = await conn.BeginSessionAsync();
        var provider = new ServiceBusSasTokenProvider(KeyName, KeyValue);
        await using var cbs = new CbsAuthenticator(session, provider);
        await cbs.OpenAsync();

        const string audience = "amqps://ns.servicebus.windows.net/queue1";
        var expiry = await cbs.PutTokenAsync(audience).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotNull(expiry);
        Assert.NotNull(capturedAppProps);
        Assert.Equal("put-token", capturedAppProps!["operation"]);
        Assert.Equal("servicebus.windows.net:sastoken", capturedAppProps["type"]);
        Assert.Equal(audience, capturedAppProps["name"]);
        Assert.NotNull(capturedBody);
        Assert.StartsWith("SharedAccessSignature sr=", capturedBody);
        Assert.Contains("skn=" + KeyName, capturedBody);
        // Body must be carried as an amqp-value string (CBS spec), not a
        // data section. Parse() routes amqp-value strings to BodyValueString
        // and leaves Body empty, so the broker side asserts that here.
        Assert.NotNull(capturedAppProps);

        await cbs.DisposeAsync();
        await session.CloseAsync();
        await conn.CloseAsync();
        await serverTask;
    }

    [Fact]
    public async Task PutTokenAsync_throws_on_non_2xx_status()
    {
        var (clientTransport, serverTransport) = PipePairTransport.CreatePair();
        await using var server = serverTransport;
        var conn = new AmqpConnection(clientTransport, DefaultConnSettings());

        var serverTask = Task.Run(async () => await RunCbsBrokerAsync(
            server, status: 401, description: "Unauthorized", capture: null));

        await conn.OpenAsync();
        var session = await conn.BeginSessionAsync();
        var provider = new ServiceBusSasTokenProvider(KeyName, KeyValue);
        await using var cbs = new CbsAuthenticator(session, provider);
        await cbs.OpenAsync();

        var ex = await Assert.ThrowsAsync<CbsAuthenticationException>(
            async () => await cbs.PutTokenAsync("amqps://ns.servicebus.windows.net/x")
                .WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(401, ex.StatusCode);
        Assert.Equal("Unauthorized", ex.StatusDescription);

        await cbs.DisposeAsync();
        await session.CloseAsync();
        await conn.CloseAsync();
        await serverTask;
    }

    // ---- broker simulator ------------------------------------------------

    private static async Task RunCbsBrokerAsync(
        IAmqpTransport server,
        int status,
        string description,
        Action<Dictionary<string, object?>, string>? capture)
    {
        await ConsumeOpenAsync(server);
        await ConsumeBeginAndReply(server, peerChannel: 2);

        // Sender attach.
        using (var f = await AmqpFrameIO.ReadFrameAsync(server))
        {
            AmqpAttach.Read(f.Body, out var a, out _);
            await SendPerfAsync(server, channel: 2, new AmqpAttach
            {
                Name = a.Name, Handle = 50, Role = AmqpRole.Receiver, InitialDeliveryCount = 0,
            }, AmqpAttach.Write);
            await SendPerfAsync(server, channel: 2, new AmqpFlow
            {
                NextIncomingId = 0, IncomingWindow = uint.MaxValue,
                NextOutgoingId = 0, OutgoingWindow = uint.MaxValue,
                Handle = 50, DeliveryCount = 0, LinkCredit = 100,
            }, AmqpFlow.Write);
        }
        // Receiver attach.
        using (var f = await AmqpFrameIO.ReadFrameAsync(server))
        {
            AmqpAttach.Read(f.Body, out var a, out _);
            await SendPerfAsync(server, channel: 2, new AmqpAttach
            {
                Name = a.Name, Handle = 51, Role = AmqpRole.Sender, InitialDeliveryCount = 0,
            }, AmqpAttach.Write);
        }
        // Initial flow.
        using (var _ = await AmqpFrameIO.ReadFrameAsync(server)) { }

        // Read put-token transfer.
        string requestMessageId = "";
        while (true)
        {
            using var f = await AmqpFrameIO.ReadFrameAsync(server);
            var kind = PerformativeCodec.PeekKind(f.Body.Span, out _);
            if (kind == PerformativeKind.Flow) continue;
            if (kind == PerformativeKind.Disposition) continue;
            Assert.Equal(PerformativeKind.Transfer, kind);
            AmqpTransfer.Read(f.Body, out _, out var perfLen);
            var payload = f.Body.Slice(perfLen).ToArray();
            var msg = AmqpMessage.Parse(payload);
            requestMessageId = msg.Properties.MessageId!;
        capture?.Invoke(msg.ApplicationProperties!, msg.BodyValueString ?? "");
            break;
        }

        // Send response with status-code.
        var response = new AmqpMessage
        {
            Properties = new AmqpProperties { CorrelationId = requestMessageId },
            ApplicationProperties = new Dictionary<string, object?>
            {
                ["status-code"] = status,
                ["status-description"] = description,
            },
            Body = Array.Empty<byte>(),
        };
        var rented = ArrayPool<byte>.Shared.Rent(Performatives.ScratchSize);
        response.Write(rented, out var respLen);
        var transfer = new AmqpTransfer
        {
            Handle = 51u,
            DeliveryId = 0u,
            DeliveryTag = new byte[] { 1 },
            MessageFormat = 0,
            Settled = false,
        };
        var perfRented = ArrayPool<byte>.Shared.Rent(Performatives.ScratchSize);
        AmqpTransfer.Write(perfRented, in transfer, out var tlen);
        var frame = ArrayPool<byte>.Shared.Rent(tlen + respLen);
        perfRented.AsSpan(0, tlen).CopyTo(frame);
        rented.AsSpan(0, respLen).CopyTo(frame.AsSpan(tlen));
        await AmqpFrameIO.WriteFrameAsync(server, AmqpFrameType.Amqp, 2, frame.AsMemory(0, tlen + respLen));
        ArrayPool<byte>.Shared.Return(frame);
        ArrayPool<byte>.Shared.Return(perfRented);
        ArrayPool<byte>.Shared.Return(rented);

        try
        {
            while (true)
            {
                using var f = await AmqpFrameIO.ReadFrameAsync(server);
                var kind = PerformativeCodec.PeekKind(f.Body.Span, out _);
                if (kind == PerformativeKind.Detach)
                {
                    AmqpDetach.Read(f.Body, out var d, out _);
                    var ourHandle = d.Handle == 0u ? 50u : 51u;
                    await SendPerfAsync(server, channel: 2, new AmqpDetach { Handle = ourHandle, Closed = true }, AmqpDetach.Write);
                }
                else if (kind == PerformativeKind.End)
                {
                    await SendPerfAsync(server, channel: 2, new AmqpEnd(), AmqpEnd.Write);
                }
                else if (kind == PerformativeKind.Close)
                {
                    await SendPerfAsync(server, channel: 0, new AmqpClose(), AmqpClose.Write);
                    break;
                }
            }
        }
        catch (IOException) { }
    }

    // ---- frame helpers ---------------------------------------------------

    private delegate void PerfWriter<T>(Span<byte> destination, in T value, out int written);

    private static async Task SendPerfAsync<T>(IAmqpTransport transport, ushort channel, T value, PerfWriter<T> writer)
    {
        var rented = ArrayPool<byte>.Shared.Rent(Performatives.ScratchSize);
        try
        {
            writer(rented, in value, out var n);
            await AmqpFrameIO.WriteFrameAsync(transport, AmqpFrameType.Amqp, channel, rented.AsMemory(0, n));
        }
        finally { ArrayPool<byte>.Shared.Return(rented); }
    }

    private static async Task ConsumeOpenAsync(IAmqpTransport server)
    {
        using (var _ = await AmqpFrameIO.ReadFrameAsync(server)) { }
        await SendPerfAsync(server, channel: 0, new AmqpOpen
        {
            ContainerId = "server",
            MaxFrameSize = 8192,
            ChannelMax = 0xFFFF,
        }, AmqpOpen.Write);
    }

    private static async Task ConsumeBeginAndReply(IAmqpTransport server, ushort peerChannel)
    {
        using var f = await AmqpFrameIO.ReadFrameAsync(server);
        AmqpBegin.Read(f.Body, out var begin, out _);
        await SendPerfAsync(server, peerChannel, new AmqpBegin
        {
            RemoteChannel = f.Header.Channel,
            NextOutgoingId = 0,
            IncomingWindow = begin.OutgoingWindow,
            OutgoingWindow = begin.IncomingWindow,
            HandleMax = 255,
        }, AmqpBegin.Write);
    }
}
