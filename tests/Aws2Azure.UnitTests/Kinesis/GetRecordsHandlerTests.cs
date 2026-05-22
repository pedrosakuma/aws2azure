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

namespace Aws2Azure.UnitTests.Kinesis;

public sealed class GetRecordsHandlerTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task HandleAsync_maps_messages_and_advances_iterator_past_last_offset()
    {
        var codecFactory = NewCodecFactory();
        var receiver = new FakeReceiver();
        receiver.Messages.Add(NewMessage("alpha", "pk-1", offset: "100", sequenceNumber: 11, enqueuedTime: FixedNow.AddSeconds(-3)));
        receiver.Messages.Add(NewMessage("beta", "pk-2", offset: "101", sequenceNumber: 12, enqueuedTime: FixedNow.AddSeconds(-2)));
        receiver.Messages.Add(NewMessage("gamma", null, offset: "102", sequenceNumber: 13, enqueuedTime: FixedNow.AddSeconds(-1)));
        var context = NewContext();

        await GetRecordsHandler.HandleAsync(
            context,
            NewParseResult(BuildRequestBody(NewEncodedToken(codecFactory, new ShardIteratorToken("orders", "shardId-000000000001", ShardIteratorType.TrimHorizon, null, FixedNow.ToUnixTimeSeconds())))),
            NewCredentials(),
            NewMetadataCache(),
            receiver,
            codecFactory,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        using var document = JsonDocument.Parse(ReadBody(context));
        var records = document.RootElement.GetProperty("Records");
        Assert.Equal(3, records.GetArrayLength());
        Assert.Equal("11", records[0].GetProperty("SequenceNumber").GetString());
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("alpha")), records[0].GetProperty("Data").GetString());
        Assert.Equal("pk-1", records[0].GetProperty("PartitionKey").GetString());
        Assert.True(records[0].GetProperty("ApproximateArrivalTimestamp").GetDouble() > 0);
        Assert.Equal(10000, receiver.MaxMessages);
        var nextToken = DecodeToken(codecFactory, document.RootElement.GetProperty("NextShardIterator").GetString()!);
        Assert.Equal(ShardIteratorType.AfterSequenceNumber, nextToken.Type);
        Assert.Equal("offset:102", nextToken.Position);
        Assert.Equal(FixedNow.ToUnixTimeSeconds(), nextToken.IssuedAtUnixSeconds);
        Assert.Equal(1000L, document.RootElement.GetProperty("MillisBehindLatest").GetInt64());
        Assert.Equal(0, document.RootElement.GetProperty("ChildShards").GetArrayLength());
    }

    [Fact]
    public async Task HandleAsync_returns_same_iterator_with_refreshed_timestamp_when_empty()
    {
        var codecFactory = NewCodecFactory();
        var original = new ShardIteratorToken("orders", "shardId-000000000001", ShardIteratorType.AtTimestamp, "2026-01-01T11:59:30.0000000Z", FixedNow.AddSeconds(-30).ToUnixTimeSeconds());
        var context = NewContext();

        await GetRecordsHandler.HandleAsync(
            context,
            NewParseResult(BuildRequestBody(NewEncodedToken(codecFactory, original))),
            NewCredentials(),
            NewMetadataCache(),
            new FakeReceiver(),
            codecFactory,
            CancellationToken.None);

        using var document = JsonDocument.Parse(ReadBody(context));
        var nextToken = DecodeToken(codecFactory, document.RootElement.GetProperty("NextShardIterator").GetString()!);
        Assert.Equal(original.Stream, nextToken.Stream);
        Assert.Equal(original.Shard, nextToken.Shard);
        Assert.Equal(original.Type, nextToken.Type);
        Assert.Equal(original.Position, nextToken.Position);
        Assert.Equal(FixedNow.ToUnixTimeSeconds(), nextToken.IssuedAtUnixSeconds);
    }

    [Fact]
    public async Task HandleAsync_maps_tampered_iterators_to_invalid_argument()
    {
        var codecFactory = NewCodecFactory();
        var encoded = NewEncodedToken(codecFactory, new ShardIteratorToken("orders", "shardId-000000000001", ShardIteratorType.Latest, null, FixedNow.ToUnixTimeSeconds()));
        var tampered = encoded[..^1] + (encoded[^1] == 'A' ? 'B' : 'A');
        var context = NewContext();

        await GetRecordsHandler.HandleAsync(
            context,
            NewParseResult(BuildRequestBody(tampered)),
            NewCredentials(),
            NewMetadataCache(),
            new FakeReceiver(),
            codecFactory,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Contains("InvalidArgumentException", ReadBody(context));
    }

    [Fact]
    public async Task HandleAsync_maps_expired_iterators_to_expired_iterator_exception()
    {
        var codecFactory = NewCodecFactory();
        var expired = NewEncodedToken(codecFactory, new ShardIteratorToken("orders", "shardId-000000000001", ShardIteratorType.Latest, null, FixedNow.AddSeconds(-301).ToUnixTimeSeconds()));
        var context = NewContext();

        await GetRecordsHandler.HandleAsync(
            context,
            NewParseResult(BuildRequestBody(expired)),
            NewCredentials(),
            NewMetadataCache(),
            new FakeReceiver(),
            codecFactory,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Contains("ExpiredIteratorException", ReadBody(context));
    }

    [Fact]
    public async Task HandleAsync_caps_requested_limit_to_ten_thousand()
    {
        var codecFactory = NewCodecFactory();
        var receiver = new FakeReceiver();
        var context = NewContext();

        await GetRecordsHandler.HandleAsync(
            context,
            NewParseResult(BuildRequestBody(NewEncodedToken(codecFactory, new ShardIteratorToken("orders", "shardId-000000000001", ShardIteratorType.TrimHorizon, null, FixedNow.ToUnixTimeSeconds())), 20000)),
            NewCredentials(),
            NewMetadataCache(),
            receiver,
            codecFactory,
            CancellationToken.None);

        Assert.Equal(10000, receiver.MaxMessages);
    }

    [Fact]
    public async Task HandleAsync_translates_at_timestamp_iterators_to_enqueued_time_positions()
    {
        var codecFactory = NewCodecFactory();
        var receiver = new FakeReceiver();
        var context = NewContext();
        var token = new ShardIteratorToken("orders", "shardId-000000000001", ShardIteratorType.AtTimestamp, "2026-01-01T11:58:00.0000000Z", FixedNow.ToUnixTimeSeconds());

        await GetRecordsHandler.HandleAsync(
            context,
            NewParseResult(BuildRequestBody(NewEncodedToken(codecFactory, token))),
            NewCredentials(),
            NewMetadataCache(),
            receiver,
            codecFactory,
            CancellationToken.None);

        var position = Assert.IsType<EventHubsReceivePosition.FromEnqueuedTime>(receiver.Position);
        Assert.Equal(DateTimeOffset.Parse("2026-01-01T11:58:00.0000000Z"), position.Value);
    }

    [Fact]
    public async Task HandleAsync_translates_latest_iterators_to_from_latest()
    {
        var codecFactory = NewCodecFactory();
        var receiver = new FakeReceiver();

        await GetRecordsHandler.HandleAsync(
            NewContext(),
            NewParseResult(BuildRequestBody(NewEncodedToken(codecFactory, new ShardIteratorToken("orders", "shardId-000000000001", ShardIteratorType.Latest, null, FixedNow.ToUnixTimeSeconds())))),
            NewCredentials(),
            NewMetadataCache(),
            receiver,
            codecFactory,
            CancellationToken.None);

        Assert.IsType<EventHubsReceivePosition.FromLatest>(receiver.Position);
    }

    [Fact]
    public async Task HandleAsync_translates_trim_horizon_iterators_to_from_start()
    {
        var codecFactory = NewCodecFactory();
        var receiver = new FakeReceiver();

        await GetRecordsHandler.HandleAsync(
            NewContext(),
            NewParseResult(BuildRequestBody(NewEncodedToken(codecFactory, new ShardIteratorToken("orders", "shardId-000000000001", ShardIteratorType.TrimHorizon, null, FixedNow.ToUnixTimeSeconds())))),
            NewCredentials(),
            NewMetadataCache(),
            receiver,
            codecFactory,
            CancellationToken.None);

        Assert.IsType<EventHubsReceivePosition.FromStart>(receiver.Position);
    }

    [Fact]
    public async Task HandleAsync_translates_synthetic_sequence_numbers_to_enqueued_time_positions()
    {
        var codecFactory = NewCodecFactory();
        var receiver = new FakeReceiver();
        var synthetic = ((1_735_689_600_123L << 20) | 7L).ToString();

        await GetRecordsHandler.HandleAsync(
            NewContext(),
            NewParseResult(BuildRequestBody(NewEncodedToken(codecFactory, new ShardIteratorToken("orders", "shardId-000000000001", ShardIteratorType.AtSequenceNumber, synthetic, FixedNow.ToUnixTimeSeconds())))),
            NewCredentials(),
            NewMetadataCache(),
            receiver,
            codecFactory,
            CancellationToken.None);

        var position = Assert.IsType<EventHubsReceivePosition.FromEnqueuedTime>(receiver.Position);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1_735_689_600_123L).AddMilliseconds(-1), position.Value);
    }

    [Fact]
    public async Task HandleAsync_returns_boundary_record_for_at_sequence_number_iterators()
    {
        var boundaryTime = DateTimeOffset.FromUnixTimeMilliseconds(1_735_689_600_123L);
        var synthetic = ((boundaryTime.ToUnixTimeMilliseconds() << 20) | 7L).ToString();
        var receiver = new BoundaryAwareReceiver(NewMessage("boundary", "pk-1", offset: "555", sequenceNumber: 42, enqueuedTime: boundaryTime));
        var codecFactory = NewCodecFactory();
        var context = NewContext();

        await GetRecordsHandler.HandleAsync(
            context,
            NewParseResult(BuildRequestBody(NewEncodedToken(codecFactory, new ShardIteratorToken("orders", "shardId-000000000001", ShardIteratorType.AtSequenceNumber, synthetic, FixedNow.ToUnixTimeSeconds())))),
            NewCredentials(),
            NewMetadataCache(),
            receiver,
            codecFactory,
            CancellationToken.None);

        using var document = JsonDocument.Parse(ReadBody(context));
        Assert.Equal(1, document.RootElement.GetProperty("Records").GetArrayLength());
    }

    [Fact]
    public async Task HandleAsync_skips_boundary_record_for_after_sequence_number_iterators()
    {
        var boundaryTime = DateTimeOffset.FromUnixTimeMilliseconds(1_735_689_600_123L);
        var synthetic = ((boundaryTime.ToUnixTimeMilliseconds() << 20) | 7L).ToString();
        var receiver = new BoundaryAwareReceiver(NewMessage("boundary", "pk-1", offset: "555", sequenceNumber: 42, enqueuedTime: boundaryTime));
        var codecFactory = NewCodecFactory();
        var context = NewContext();

        await GetRecordsHandler.HandleAsync(
            context,
            NewParseResult(BuildRequestBody(NewEncodedToken(codecFactory, new ShardIteratorToken("orders", "shardId-000000000001", ShardIteratorType.AfterSequenceNumber, synthetic, FixedNow.ToUnixTimeSeconds())))),
            NewCredentials(),
            NewMetadataCache(),
            receiver,
            codecFactory,
            CancellationToken.None);

        using var document = JsonDocument.Parse(ReadBody(context));
        Assert.Equal(0, document.RootElement.GetProperty("Records").GetArrayLength());
    }

    [Fact]
    public async Task HandleAsync_propagates_partition_metadata_and_supports_offset_iterators()
    {
        var codecFactory = NewCodecFactory();
        var receiver = new FakeReceiver();
        receiver.Messages.Add(NewMessage("payload", "pk-9", offset: "999", sequenceNumber: 777, enqueuedTime: FixedNow.AddSeconds(-5)));
        var context = NewContext();
        var token = new ShardIteratorToken("orders", "shardId-000000000001", ShardIteratorType.AfterSequenceNumber, "offset:555", FixedNow.ToUnixTimeSeconds());

        await GetRecordsHandler.HandleAsync(
            context,
            NewParseResult(BuildRequestBody(NewEncodedToken(codecFactory, token))),
            NewCredentials(),
            NewMetadataCache(),
            receiver,
            codecFactory,
            CancellationToken.None);

        var position = Assert.IsType<EventHubsReceivePosition.FromOffsetExclusive>(receiver.Position);
        Assert.Equal("555", position.Value);
        using var document = JsonDocument.Parse(ReadBody(context));
        var record = document.RootElement.GetProperty("Records")[0];
        Assert.Equal("777", record.GetProperty("SequenceNumber").GetString());
        Assert.Equal("pk-9", record.GetProperty("PartitionKey").GetString());
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("payload")), record.GetProperty("Data").GetString());
    }

    private static EventHubsReceivedMessage NewMessage(string body, string? partitionKey, string? offset, long sequenceNumber, DateTimeOffset enqueuedTime)
    {
        var annotations = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["x-opt-sequence-number"] = sequenceNumber,
            ["x-opt-enqueued-time"] = enqueuedTime,
        };
        if (offset is not null)
        {
            annotations["x-opt-offset"] = offset;
        }

        if (partitionKey is not null)
        {
            annotations["x-opt-partition-key"] = partitionKey;
        }

        return new EventHubsReceivedMessage(Encoding.UTF8.GetBytes(body), annotations, offset, sequenceNumber, enqueuedTime, partitionKey);
    }

    private static EventHubsCredentials NewCredentials() => new()
    {
        Namespace = "myns",
        SasKeyName = "Root",
        SasKey = "secret",
        ShardIteratorSigningKey = Convert.ToBase64String(Encoding.UTF8.GetBytes("0123456789abcdef0123456789abcdef")),
        Streams = new Dictionary<string, KinesisStreamSettings>
        {
            ["orders"] = new KinesisStreamSettings { EventHubName = "orders-eh", ConsumerGroup = "cg-1" },
        },
    };

    private static FakeMetadataCache NewMetadataCache()
        => new((_, namespaceFqdn, eventHubName, _) =>
        {
            Assert.Equal("myns.servicebus.windows.net", namespaceFqdn);
            Assert.Equal("orders-eh", eventHubName);
            return ValueTask.FromResult(new EventHubDescription(3, ["0", "1", "2"], 7, FixedNow));
        });

    private static ShardIteratorTokenCodecFactory NewCodecFactory()
        => new(NullLogger<ShardIteratorTokenCodecFactory>.Instance, new ManualTimeProvider(FixedNow));

    private static string BuildRequestBody(string shardIterator, int? limit = null)
        => limit.HasValue
            ? "{\"ShardIterator\":\"" + shardIterator + "\",\"Limit\":" + limit.Value + "}"
            : "{\"ShardIterator\":\"" + shardIterator + "\"}";

    private static string NewEncodedToken(ShardIteratorTokenCodecFactory codecFactory, ShardIteratorToken token)
        => codecFactory.Create(NewCredentials()).Encode(token);

    private static ShardIteratorToken DecodeToken(ShardIteratorTokenCodecFactory codecFactory, string encoded)
    {
        var codec = codecFactory.Create(NewCredentials());
        Assert.True(codec.TryDecode(encoded, out var token, out var error));
        Assert.Equal(ShardIteratorVerifyError.None, error);
        return token;
    }

    private static KinesisParseResult NewParseResult(string body)
        => new(KinesisOperation.GetRecords, "Kinesis_20131202.GetRecords", Encoding.UTF8.GetBytes(body), null);

    private static DefaultHttpContext NewContext()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static string ReadBody(HttpContext context)
    {
        context.Response.Body.Position = 0;
        return new StreamReader(context.Response.Body, Encoding.UTF8).ReadToEnd();
    }

    private sealed class FakeReceiver : IEventHubsAmqpReceiver
    {
        public List<EventHubsReceivedMessage> Messages { get; } = [];
        public EventHubsReceivePosition? Position { get; private set; }
        public int MaxMessages { get; private set; }

        public Task<EventHubsReceiveResult> ReceiveAsync(
            EventHubsCredentials credentials,
            string namespaceFqdn,
            string entityPath,
            string consumerGroup,
            int partitionId,
            EventHubsReceivePosition position,
            int maxMessages,
            TimeSpan quiescentTimeout,
            CancellationToken cancellationToken)
        {
            Assert.Equal("myns.servicebus.windows.net", namespaceFqdn);
            Assert.Equal("orders-eh", entityPath);
            Assert.Equal("cg-1", consumerGroup);
            Assert.Equal(1, partitionId);
            Assert.Equal(TimeSpan.FromMilliseconds(500), quiescentTimeout);
            Position = position;
            MaxMessages = maxMessages;
            return Task.FromResult(new EventHubsReceiveResult(Messages));
        }
    }

    private sealed class BoundaryAwareReceiver(EventHubsReceivedMessage message) : IEventHubsAmqpReceiver
    {
        private readonly EventHubsReceivedMessage _message = message;

        public Task<EventHubsReceiveResult> ReceiveAsync(
            EventHubsCredentials credentials,
            string namespaceFqdn,
            string entityPath,
            string consumerGroup,
            int partitionId,
            EventHubsReceivePosition position,
            int maxMessages,
            TimeSpan quiescentTimeout,
            CancellationToken cancellationToken)
        {
            var messages = position switch
            {
                EventHubsReceivePosition.FromEnqueuedTime fromEnqueuedTime when _message.EnqueuedTime > fromEnqueuedTime.Value => [_message],
                _ => Array.Empty<EventHubsReceivedMessage>(),
            };

            return Task.FromResult(new EventHubsReceiveResult(messages));
        }
    }

    private sealed class FakeMetadataCache(Func<EventHubsCredentials, string, string, CancellationToken, ValueTask<EventHubDescription>> handler)
        : IEventHubMetadataCache
    {
        public ValueTask<EventHubDescription> GetEventHubAsync(EventHubsCredentials credentials, string namespaceFqdn, string eventHubName, CancellationToken cancellationToken)
            => handler(credentials, namespaceFqdn, eventHubName, cancellationToken);
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private readonly DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
