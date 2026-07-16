using System.IO;
using System.Text;
using System.Text.Json;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Modules.Kinesis.EventHubsAmqp;
using Aws2Azure.Modules.Kinesis.EventHubsRest;
using Aws2Azure.Modules.Kinesis.Operations;
using Aws2Azure.Modules.Kinesis.ShardIterators;
using Aws2Azure.Modules.Kinesis.WireProtocol;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Aws2Azure.TestSupport.Kinesis;
using static Aws2Azure.TestSupport.Http.TestHttpContext;

namespace Aws2Azure.UnitTests.Kinesis;

public sealed class GetRecordsHandlerResponseLimitTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task HandleAsync_excludes_messages_that_would_push_response_past_ten_mebibytes()
    {
        var codecFactory = new ShardIteratorTokenCodecFactory(
            NullLogger<ShardIteratorTokenCodecFactory>.Instance,
            new ManualTimeProvider(FixedNow));
        var bodyA = new byte[6 * 1024 * 1024];
        var bodyB = new byte[5 * 1024 * 1024];
        var receiver = new FakeReceiver([
            new EventHubsReceivedMessage(bodyA, new Dictionary<string, object>(), "100", 1, FixedNow.AddSeconds(-2), null),
            new EventHubsReceivedMessage(bodyB, new Dictionary<string, object>(), "101", 2, FixedNow.AddSeconds(-1), null),
        ]);
        var context = CreateContext();

        await GetRecordsHandler.HandleAsync(
            context,
            new(KinesisOperation.GetRecords, "Kinesis_20131202.GetRecords", Encoding.UTF8.GetBytes("{\"ShardIterator\":\"" + codecFactory.Create(NewCredentials()).Encode(new ShardIteratorToken("orders", "shardId-000000000001", ShardIteratorType.TrimHorizon, null, FixedNow.ToUnixTimeSeconds())) + "\"}"), null),
            NewCredentials(),
            new FakeMetadataCache(),
            receiver,
            codecFactory,
            CancellationToken.None);

        using var document = JsonDocument.Parse(ReadBody(context));
        Assert.Equal(1, document.RootElement.GetProperty("Records").GetArrayLength());
        var nextToken = document.RootElement.GetProperty("NextShardIterator").GetString();
        Assert.NotNull(nextToken);
        var codec = codecFactory.Create(NewCredentials());
        Assert.True(codec.TryDecode(nextToken!, out var decoded, out var error));
        Assert.Equal(ShardIteratorVerifyError.None, error);
        Assert.Equal("offset:100", decoded.Position);
    }

    private static EventHubsCredentials NewCredentials() => new()
    {
        Namespace = "myns",
        SasKeyName = "Root",
        SasKey = "secret",
        ShardIteratorSigningKey = Convert.ToBase64String(Encoding.UTF8.GetBytes("0123456789abcdef0123456789abcdef")),
    };


    private sealed class FakeReceiver(IReadOnlyList<EventHubsReceivedMessage> messages) : IEventHubsAmqpReceiver
    {
        public Task<EventHubsReceiveResult> ReceiveAsync(EventHubsCredentials credentials, string namespaceFqdn, string entityPath, string consumerGroup, int partitionId, string iteratorId, EventHubsReceivePosition position, int maxMessages, TimeSpan quiescentTimeout, CancellationToken cancellationToken)
            => Task.FromResult(new EventHubsReceiveResult(messages));
    }


    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private readonly DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
