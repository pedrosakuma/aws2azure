using System.Text;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Modules.Kinesis.EventHubsAmqp;
using Aws2Azure.Modules.Kinesis.EventHubsRest;
using Aws2Azure.Modules.Kinesis.Operations;
using Aws2Azure.Modules.Kinesis.ShardIterators;
using Aws2Azure.Modules.Kinesis.WireProtocol;
using Aws2Azure.TestSupport.Kinesis;
using Microsoft.Extensions.Logging.Abstractions;
using static Aws2Azure.TestSupport.Http.TestHttpContext;

namespace Aws2Azure.IntegrationTests.OperationalQualification;

internal static class KinesisConsumerDeterministicQualification
{
    private static readonly DateTimeOffset FixedNow =
        new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    public static async Task VerifyIteratorExpiryAsync()
    {
        var factory = Factory();
        var token = factory.Create(Credentials()).Encode(new ShardIteratorToken(
            "orders",
            "shardId-000000000001",
            ShardIteratorType.Latest,
            null,
            FixedNow.AddSeconds(-301).ToUnixTimeSeconds(),
            "expired"));
        var context = CreateContext();

        await GetRecordsHandler.HandleAsync(
            context,
            Parse(token),
            Credentials(),
            Metadata(),
            new EmptyReceiver(),
            factory,
            CancellationToken.None).ConfigureAwait(false);

        if (context.Response.StatusCode != 400
            || !ReadBody(context).Contains("ExpiredIteratorException", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Expired Kinesis iterator did not map to ExpiredIteratorException.");
        }
    }

    public static async Task VerifyCancellationAsync()
    {
        var factory = Factory();
        var token = factory.Create(Credentials()).Encode(new ShardIteratorToken(
            "orders",
            "shardId-000000000001",
            ShardIteratorType.TrimHorizon,
            null,
            FixedNow.ToUnixTimeSeconds(),
            "cancelled"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        try
        {
            await GetRecordsHandler.HandleAsync(
                CreateContext(),
                Parse(token),
                Credentials(),
                Metadata(),
                new CancellingReceiver(),
                factory,
                cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        throw new InvalidDataException(
            "Kinesis GetRecords cancellation did not propagate.");
    }

    public static async Task VerifyRetryBoundariesAsync()
    {
        await VerifyFailureAsync(
            EventHubsAmqpFailureKind.Throttled,
            400,
            "ProvisionedThroughputExceededException").ConfigureAwait(false);
        await VerifyFailureAsync(
            EventHubsAmqpFailureKind.Transient,
            500,
            "InternalFailureException").ConfigureAwait(false);
    }

    private static async Task VerifyFailureAsync(
        EventHubsAmqpFailureKind kind,
        int expectedStatus,
        string expectedCode)
    {
        var factory = Factory();
        var token = factory.Create(Credentials()).Encode(new ShardIteratorToken(
            "orders",
            "shardId-000000000001",
            ShardIteratorType.TrimHorizon,
            null,
            FixedNow.ToUnixTimeSeconds(),
            "retry-boundary"));
        var context = CreateContext();

        await GetRecordsHandler.HandleAsync(
            context,
            Parse(token),
            Credentials(),
            Metadata(),
            new FailingReceiver(kind),
            factory,
            CancellationToken.None).ConfigureAwait(false);

        if (context.Response.StatusCode != expectedStatus
            || !ReadBody(context).Contains(expectedCode, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Kinesis retry boundary '{kind}' did not map to {expectedCode}.");
        }
    }

    private static EventHubsCredentials Credentials() => new()
    {
        Namespace = "myns",
        SasKeyName = "Root",
        SasKey = "secret",
        ShardIteratorSigningKey = Convert.ToBase64String(
            Encoding.UTF8.GetBytes("0123456789abcdef0123456789abcdef")),
        Streams = new Dictionary<string, KinesisStreamSettings>
        {
            ["orders"] = new()
            {
                EventHubName = "orders-eh",
                ConsumerGroup = "consumer",
            },
        },
    };

    private static FakeMetadataCache Metadata() => new(
        (_, _, _, _) => ValueTask.FromResult(
            new EventHubDescription(3, ["0", "1", "2"], 7, FixedNow)));

    private static ShardIteratorTokenCodecFactory Factory() => new(
        NullLogger<ShardIteratorTokenCodecFactory>.Instance,
        new FixedTimeProvider(FixedNow));

    private static KinesisParseResult Parse(string token) => new(
        KinesisOperation.GetRecords,
        "Kinesis_20131202.GetRecords",
        Encoding.UTF8.GetBytes("{\"ShardIterator\":\"" + token + "\"}"),
        null);

    private sealed class EmptyReceiver : IEventHubsAmqpReceiver
    {
        public Task<EventHubsReceiveResult> ReceiveAsync(
            EventHubsCredentials credentials,
            string namespaceFqdn,
            string entityPath,
            string consumerGroup,
            int partitionId,
            string iteratorId,
            EventHubsReceivePosition position,
            int maxMessages,
            TimeSpan quiescentTimeout,
            CancellationToken cancellationToken)
            => Task.FromResult(new EventHubsReceiveResult([]));
    }

    private sealed class CancellingReceiver : IEventHubsAmqpReceiver
    {
        public Task<EventHubsReceiveResult> ReceiveAsync(
            EventHubsCredentials credentials,
            string namespaceFqdn,
            string entityPath,
            string consumerGroup,
            int partitionId,
            string iteratorId,
            EventHubsReceivePosition position,
            int maxMessages,
            TimeSpan quiescentTimeout,
            CancellationToken cancellationToken)
            => Task.FromCanceled<EventHubsReceiveResult>(cancellationToken);
    }

    private sealed class FailingReceiver(EventHubsAmqpFailureKind kind)
        : IEventHubsAmqpReceiver
    {
        public Task<EventHubsReceiveResult> ReceiveAsync(
            EventHubsCredentials credentials,
            string namespaceFqdn,
            string entityPath,
            string consumerGroup,
            int partitionId,
            string iteratorId,
            EventHubsReceivePosition position,
            int maxMessages,
            TimeSpan quiescentTimeout,
            CancellationToken cancellationToken)
            => Task.FromException<EventHubsReceiveResult>(
                new EventHubsAmqpException(
                    "planned deterministic failure",
                    new InvalidOperationException("planned"),
                    kind));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
