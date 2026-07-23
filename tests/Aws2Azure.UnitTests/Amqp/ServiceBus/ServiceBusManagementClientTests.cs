using System.Buffers;
using System.Buffers.Binary;
using Aws2Azure.Amqp.Codec;
using Aws2Azure.Amqp.Connection;
using Aws2Azure.Amqp.Framing;
using Aws2Azure.Amqp.ServiceBus;
using Aws2Azure.Amqp.Transport;
using Aws2Azure.UnitTests.Amqp.Transport;
using static Aws2Azure.UnitTests.Amqp.Connection.AmqpTestBroker;

namespace Aws2Azure.UnitTests.Amqp.ServiceBus;

/// <summary>
/// Tests for <see cref="ServiceBusManagementClient"/>: encoder for
/// renew-lock requests, decoder for the timestamp-array response, and
/// the broker error path that surfaces as
/// <see cref="ServiceBusManagementException"/>. The tests stand up a
/// minimal in-process AMQP broker that handles the paired sender +
/// receiver attach against <c>$management</c>, parses one request
/// (recording the lock-tokens) and replies with a synthesised
/// expirations array.
/// </summary>
public sealed class ServiceBusManagementClientTests
{
    private static AmqpConnectionSettings DefaultConnSettings() => new()
    {
        ContainerId = "test-client",
        Hostname = "ns.servicebus.windows.net",
        IdleTimeout = TimeSpan.Zero,
    };

    [Theory]
    [InlineData(1)]
    [InlineData(20)]   // regression: previously overflowed pairBytes (256B stack buffer).
    [InlineData(128)]  // batch cap.
    public async Task RenewLockAsync_round_trips_lock_tokens_to_expirations(int count)
    {
        var (clientTransport, serverTransport) = PipePairTransport.CreatePair();
        await using var server = serverTransport;
        var conn = new AmqpConnection(clientTransport, DefaultConnSettings());

        var expectedTokens = new Guid[count];
        for (int i = 0; i < count; i++) expectedTokens[i] = Guid.NewGuid();
        var brokerExpiry = DateTimeOffset.UtcNow.AddSeconds(60);
        // Round to ms so the over-the-wire timestamp round-trips exactly.
        brokerExpiry = DateTimeOffset.FromUnixTimeMilliseconds(brokerExpiry.ToUnixTimeMilliseconds());

        Guid[]? receivedTokens = null;
        string? receivedOperation = null;
        var serverTask = Task.Run(async () => receivedTokens = await ManagementBrokerSimulator.RunFullAsync(
            server,
            renewExpiry: brokerExpiry,
            statusCode: 200,
            statusDescription: "OK",
            errorCondition: null,
            captureOperation: op => receivedOperation = op));

        await conn.OpenAsync();
        var session = await conn.BeginSessionAsync();
        await using var mgmt = await ServiceBusManagementClient.OpenAsync(session);

        var expirations = await mgmt.RenewLocksAsync(expectedTokens).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(count, expirations.Length);
        for (int i = 0; i < count; i++)
            Assert.Equal(brokerExpiry, expirations[i]);

        await mgmt.DisposeAsync();
        await session.CloseAsync();
        await conn.CloseAsync();
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("com.microsoft:renew-lock", receivedOperation);
        Assert.NotNull(receivedTokens);
        Assert.Equal(expectedTokens, receivedTokens);
    }

    [Fact]
    public async Task RenewLockAsync_throws_on_non_success_status()
    {
        var (clientTransport, serverTransport) = PipePairTransport.CreatePair();
        await using var server = serverTransport;
        var conn = new AmqpConnection(clientTransport, DefaultConnSettings());

        var serverTask = Task.Run(async () => await ManagementBrokerSimulator.RunFullAsync(
            server,
            renewExpiry: DateTimeOffset.UtcNow,
            statusCode: 410,
            statusDescription: "MessageLockLost",
            errorCondition: "com.microsoft:message-lock-lost",
            captureOperation: _ => { }));

        await conn.OpenAsync();
        var session = await conn.BeginSessionAsync();
        await using var mgmt = await ServiceBusManagementClient.OpenAsync(session);

        var ex = await Assert.ThrowsAsync<ServiceBusManagementException>(
            async () => await mgmt.RenewLockAsync(Guid.NewGuid()).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(410, ex.StatusCode);
        Assert.Equal("MessageLockLost", ex.Description);
        Assert.Equal("com.microsoft:message-lock-lost", ex.ErrorCondition);
        Assert.Equal("com.microsoft:renew-lock", ex.Operation);

        await mgmt.DisposeAsync();
        await session.CloseAsync();
        await conn.CloseAsync();
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task RenewSessionLockAsync_round_trips_session_id_to_expiration()
    {
        var (clientTransport, serverTransport) = PipePairTransport.CreatePair();
        await using var server = serverTransport;
        var conn = new AmqpConnection(clientTransport, DefaultConnSettings());

        const string SessionId = "order-group-42";
        var brokerExpiry = DateTimeOffset.UtcNow.AddSeconds(30);
        brokerExpiry = DateTimeOffset.FromUnixTimeMilliseconds(brokerExpiry.ToUnixTimeMilliseconds());

        string? receivedOperation = null;
        string? associatedLinkName = null;
        object? serverTimeout = null;
        string? trackingId = null;
        var serverTask = Task.Run(async () => await ManagementBrokerSimulator.RunFullAsync(
            server,
            renewExpiry: brokerExpiry,
            statusCode: 200,
            statusDescription: "OK",
            errorCondition: null,
            captureOperation: op => receivedOperation = op,
            captureRequest: request =>
            {
                associatedLinkName = request.ApplicationProperties?["associated-link-name"] as string;
                serverTimeout = request.ApplicationProperties?["com.microsoft:server-timeout"];
                trackingId = request.ApplicationProperties?["com.microsoft:tracking-id"] as string;
            }));

        await conn.OpenAsync();
        var session = await conn.BeginSessionAsync();
        await using var mgmt = await ServiceBusManagementClient.OpenAsync(session);

        var expiration = await mgmt
            .RenewSessionLockAsync(SessionId, "receiver-link-42")
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(brokerExpiry, expiration);

        await mgmt.DisposeAsync();
        await session.CloseAsync();
        await conn.CloseAsync();
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("com.microsoft:renew-session-lock", receivedOperation);
        Assert.Equal("receiver-link-42", associatedLinkName);
        Assert.IsType<uint>(serverTimeout);
        Assert.False(string.IsNullOrWhiteSpace(trackingId));
    }

    [Fact]
    public async Task RenewSessionLockAsync_throws_on_session_lock_lost()
    {
        var (clientTransport, serverTransport) = PipePairTransport.CreatePair();
        await using var server = serverTransport;
        var conn = new AmqpConnection(clientTransport, DefaultConnSettings());

        var serverTask = Task.Run(async () => await ManagementBrokerSimulator.RunFullAsync(
            server,
            renewExpiry: DateTimeOffset.UtcNow,
            statusCode: 410,
            statusDescription: "SessionLockLost",
            errorCondition: "com.microsoft:session-lock-lost",
            captureOperation: _ => { }));

        await conn.OpenAsync();
        var session = await conn.BeginSessionAsync();
        await using var mgmt = await ServiceBusManagementClient.OpenAsync(session);

        var ex = await Assert.ThrowsAsync<ServiceBusManagementException>(
            async () => await mgmt
                .RenewSessionLockAsync("group-X", "receiver-link-X")
                .WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(410, ex.StatusCode);
        Assert.Equal("SessionLockLost", ex.Description);
        Assert.Equal("com.microsoft:session-lock-lost", ex.ErrorCondition);
        Assert.Equal("com.microsoft:renew-session-lock", ex.Operation);

        await mgmt.DisposeAsync();
        await session.CloseAsync();
        await conn.CloseAsync();
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

}
