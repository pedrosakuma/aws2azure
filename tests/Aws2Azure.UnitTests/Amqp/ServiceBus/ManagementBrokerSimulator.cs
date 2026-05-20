using System.Buffers;
using System.Buffers.Binary;
using Aws2Azure.Amqp.Codec;
using Aws2Azure.Amqp.Connection;
using Aws2Azure.Amqp.Framing;
using Aws2Azure.Amqp.ServiceBus;
using Aws2Azure.Amqp.Transport;
using static Aws2Azure.UnitTests.Amqp.Connection.AmqpTestBroker;

namespace Aws2Azure.UnitTests.Amqp.ServiceBus;

/// <summary>
/// Minimal in-process AMQP broker that handles the paired
/// sender + receiver attach against <c>$management</c>, decodes
/// <c>com.microsoft:renew-lock</c> requests, and replies with a
/// synthesised expirations array. Shared between
/// <see cref="ServiceBusManagementClientTests"/> and the SQS
/// <c>ChangeMessageVisibility</c> dispatcher tests.
/// </summary>
internal static class ManagementBrokerSimulator
{
    /// <summary>
    /// Drives the broker end of a connection that has *already*
    /// completed AMQP open + begin (e.g. the CMV harness establishes
    /// those through a separate path). Handles only the
    /// <c>$management</c> attach + a single renew-lock request +
    /// graceful detach/end/close.
    /// </summary>
    public static async Task<Guid[]?> RunAttachedAsync(
        IAmqpTransport server,
        ushort sessionChannel,
        DateTimeOffset renewExpiry,
        int statusCode,
        string? statusDescription,
        string? errorCondition,
        Action<string?> captureOperation)
    {
        // Client opens a sender on $management → broker mirrors as receiver
        // (peer handle 100), then grants credit.
        using (var f = await AmqpFrameIO.ReadFrameAsync(server))
        {
            AmqpAttach.Read(f.Body, out var a, out _);
            await SendPerfAsync(server, sessionChannel, new AmqpAttach
            {
                Name = a.Name, Handle = 100, Role = AmqpRole.Receiver, InitialDeliveryCount = 0,
            }, AmqpAttach.Write);
            await SendPerfAsync(server, sessionChannel, new AmqpFlow
            {
                NextIncomingId = 0, IncomingWindow = uint.MaxValue,
                NextOutgoingId = 0, OutgoingWindow = uint.MaxValue,
                Handle = 100, DeliveryCount = 0, LinkCredit = 100,
            }, AmqpFlow.Write);
        }
        // Receiver attach (response path) → peer handle 101.
        using (var f = await AmqpFrameIO.ReadFrameAsync(server))
        {
            AmqpAttach.Read(f.Body, out var a, out _);
            await SendPerfAsync(server, sessionChannel, new AmqpAttach
            {
                Name = a.Name, Handle = 101, Role = AmqpRole.Sender, InitialDeliveryCount = 0,
            }, AmqpAttach.Write);
        }
        // Initial flow from client.
        using (var _ = await AmqpFrameIO.ReadFrameAsync(server)) { }

        Guid[]? lockTokens = null;
        uint nextDelivery = 0;
        while (true)
        {
            using var f = await AmqpFrameIO.ReadFrameAsync(server);
            var kind = PerformativeCodec.PeekKind(f.Body.Span, out _);
            if (kind == PerformativeKind.Flow) continue;
            if (kind == PerformativeKind.Disposition) continue;
            if (kind == PerformativeKind.Detach) break;
            if (kind != PerformativeKind.Transfer) continue;

            AmqpTransfer.Read(f.Body, out var transfer, out var perfLen);
            var payload = f.Body.Slice(perfLen).ToArray();
            var requestMsg = AmqpMessage.Parse(payload);

            captureOperation(requestMsg.ApplicationProperties?["operation"] as string);
            lockTokens = ExtractLockTokens(requestMsg);

            var responseBody = EncodeRenewLockResponse(renewExpiry, lockTokens?.Length ?? 0);

            var appProps = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["statusCode"] = statusCode,
            };
            if (statusDescription is not null) appProps["statusDescription"] = statusDescription;
            if (errorCondition is not null) appProps["errorCondition"] = errorCondition;

            var response = new AmqpMessage
            {
                Properties = new AmqpProperties { CorrelationId = requestMsg.Properties.MessageId },
                ApplicationProperties = appProps,
                BodyValueBytes = responseBody,
            };

            using var pooled = response.EncodePooled();
            var respTransfer = new AmqpTransfer
            {
                Handle = 101u,
                DeliveryId = nextDelivery++,
                DeliveryTag = new byte[] { 0x01 },
                MessageFormat = 0,
                Settled = false,
            };
            var perfRented = ArrayPool<byte>.Shared.Rent(Performatives.ScratchSize);
            AmqpTransfer.Write(perfRented, in respTransfer, out var tlen);
            var frame = ArrayPool<byte>.Shared.Rent(tlen + pooled.Length);
            perfRented.AsSpan(0, tlen).CopyTo(frame);
            pooled.Memory.Span.CopyTo(frame.AsSpan(tlen));
            await AmqpFrameIO.WriteFrameAsync(server, AmqpFrameType.Amqp, sessionChannel, frame.AsMemory(0, tlen + pooled.Length));
            ArrayPool<byte>.Shared.Return(frame);
            ArrayPool<byte>.Shared.Return(perfRented);
        }

        // Drain detach/end/close.
        try
        {
            while (true)
            {
                using var f = await AmqpFrameIO.ReadFrameAsync(server);
                var kind = PerformativeCodec.PeekKind(f.Body.Span, out _);
                if (kind == PerformativeKind.Detach)
                {
                    AmqpDetach.Read(f.Body, out var d, out _);
                    var ourHandle = d.Handle == 0u ? 100u : 101u;
                    await SendPerfAsync(server, sessionChannel, new AmqpDetach { Handle = ourHandle, Closed = true }, AmqpDetach.Write);
                }
                else if (kind == PerformativeKind.End)
                    await SendPerfAsync(server, sessionChannel, new AmqpEnd(), AmqpEnd.Write);
                else if (kind == PerformativeKind.Close)
                {
                    await SendPerfAsync(server, 0, new AmqpClose(), AmqpClose.Write);
                    break;
                }
            }
        }
        catch (IOException) { /* peer closed */ }
        return lockTokens;
    }

    /// <summary>
    /// Convenience wrapper that performs the AMQP open + begin handshake
    /// before delegating to <see cref="RunAttachedAsync"/>. Used by
    /// standalone management-client tests that own the whole transport.
    /// </summary>
    public static async Task<Guid[]?> RunFullAsync(
        IAmqpTransport server,
        DateTimeOffset renewExpiry,
        int statusCode,
        string? statusDescription,
        string? errorCondition,
        Action<string?> captureOperation,
        ushort sessionChannel = 3)
    {
        await ConsumeOpenAsync(server);
        await ConsumeBeginAndReply(server, peerChannel: sessionChannel);
        return await RunAttachedAsync(server, sessionChannel, renewExpiry,
            statusCode, statusDescription, errorCondition, captureOperation);
    }

    public static Guid[]? ExtractLockTokens(AmqpMessage msg)
    {
        if (msg.BodyValueBytes is not { } bodyMem || bodyMem.IsEmpty)
            return null;
        var span = bodyMem.Span;
        var map = AmqpCompoundReader.ReadMap(span, out _);
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
            if (key == "lock-tokens")
            {
                var arr = AmqpCompoundReader.ReadArray(els[o..], out var arrLen);
                o += arrLen;
                if (arr.ElementConstructor != AmqpFormatCode.Uuid)
                    throw new InvalidDataException("Expected UUID array for lock-tokens.");
                var result = new Guid[arr.Count];
                for (int j = 0; j < arr.Count; j++)
                    result[j] = new Guid(arr.ElementData.Slice(j * 16, 16), bigEndian: true);
                return result;
            }
            // Skip unknown.
            var valLen = AmqpValueScanner.Measure(els[o..]);
            o += valLen;
        }
        return null;
    }

    public static byte[] EncodeRenewLockResponse(DateTimeOffset expiry, int count)
    {
        var unixMs = expiry.ToUnixTimeMilliseconds();
        var elementData = new byte[count * 8];
        for (int i = 0; i < count; i++)
            BinaryPrimitives.WriteInt64BigEndian(elementData.AsSpan(i * 8), unixMs);

        Span<byte> arrayBytes = stackalloc byte[elementData.Length + 16];
        AmqpCompoundWriter.WriteArray(
            arrayBytes,
            elementConstructor: stackalloc byte[] { AmqpFormatCode.TimestampMs },
            elementData: elementData,
            count: count,
            out var arrLen);

        Span<byte> keyBytes = stackalloc byte[64];
        AmqpVariableWriter.WriteSymbol(keyBytes, "expirations", out var keyLen);

        var pair = new byte[keyLen + arrLen];
        keyBytes[..keyLen].CopyTo(pair);
        arrayBytes[..arrLen].CopyTo(pair.AsSpan(keyLen));

        var mapOut = new byte[pair.Length + 16];
        AmqpCompoundWriter.WriteMap(mapOut, pair, pairCount: 1, out var mapLen);
        Array.Resize(ref mapOut, mapLen);
        return mapOut;
    }
}
