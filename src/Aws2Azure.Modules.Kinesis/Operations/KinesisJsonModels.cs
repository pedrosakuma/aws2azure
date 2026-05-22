using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Aws2Azure.Modules.Kinesis.Operations;

internal sealed class DescribeStreamRequest
{
    public string? StreamName { get; set; }
    public string? ExclusiveStartShardId { get; set; }
    public int? Limit { get; set; }
    public string? StreamARN { get; set; }
}

internal sealed class DescribeStreamResponse
{
    public DescribeStreamResponseBody StreamDescription { get; set; } = new();
}

internal sealed class DescribeStreamResponseBody
{
    public string StreamName { get; set; } = string.Empty;
    public string StreamARN { get; set; } = string.Empty;
    public string StreamStatus { get; set; } = string.Empty;
    public double StreamCreationTimestamp { get; set; }
    public int RetentionPeriodHours { get; set; }
    public string EncryptionType { get; set; } = string.Empty;
    public EnhancedMonitoringDescription[] EnhancedMonitoring { get; set; } = [];
    public KinesisShardDescription[] Shards { get; set; } = [];
    public bool HasMoreShards { get; set; }
}

internal sealed class DescribeStreamSummaryRequest
{
    public string? StreamName { get; set; }
    public string? StreamARN { get; set; }
}

internal sealed class DescribeStreamSummaryResponse
{
    public DescribeStreamSummaryResponseBody StreamDescriptionSummary { get; set; } = new();
}

internal sealed class DescribeStreamSummaryResponseBody
{
    public string StreamName { get; set; } = string.Empty;
    public string StreamARN { get; set; } = string.Empty;
    public string StreamStatus { get; set; } = string.Empty;
    public double StreamCreationTimestamp { get; set; }
    public int RetentionPeriodHours { get; set; }
    public EnhancedMonitoringDescription[] EnhancedMonitoring { get; set; } = [];
    public string EncryptionType { get; set; } = string.Empty;
    public int OpenShardCount { get; set; }
    public int ConsumerCount { get; set; }
}

internal sealed class ListShardsRequest
{
    public string? StreamName { get; set; }
    public string? NextToken { get; set; }
    public string? ExclusiveStartShardId { get; set; }
    public int? MaxResults { get; set; }
    public double? StreamCreationTimestamp { get; set; }
    public ShardFilterRequest? ShardFilter { get; set; }
}

internal sealed class ShardFilterRequest
{
    public string? Type { get; set; }
}

internal sealed class PutRecordRequest
{
    public string? StreamName { get; set; }
    public string? Data { get; set; }
    public string? PartitionKey { get; set; }
    public string? ExplicitHashKey { get; set; }
    public string? SequenceNumberForOrdering { get; set; }
    public string? StreamARN { get; set; }
}

internal sealed class PutRecordResponse
{
    public string ShardId { get; set; } = string.Empty;
    public string SequenceNumber { get; set; } = string.Empty;
    public string EncryptionType { get; set; } = string.Empty;
}

internal sealed class PutRecordsRequest
{
    public string? StreamName { get; set; }
    public string? StreamARN { get; set; }
    public List<PutRecordsRequestEntry>? Records { get; set; }
}

internal sealed class PutRecordsRequestEntry
{
    public string? Data { get; set; }
    public string? PartitionKey { get; set; }
    public string? ExplicitHashKey { get; set; }
}

internal sealed class PutRecordsResponse
{
    public int FailedRecordCount { get; set; }
    public PutRecordsResultEntry[] Records { get; set; } = [];
    public string EncryptionType { get; set; } = string.Empty;
}

internal sealed class GetShardIteratorRequest
{
    public string? StreamName { get; set; }
    public string? StreamARN { get; set; }
    public string? ShardId { get; set; }
    public string? ShardIteratorType { get; set; }
    public string? StartingSequenceNumber { get; set; }
    public double? Timestamp { get; set; }
}

internal sealed class GetShardIteratorResponse
{
    public string ShardIterator { get; set; } = string.Empty;
}

internal sealed class GetRecordsRequest
{
    public string? ShardIterator { get; set; }
    public int? Limit { get; set; }
    public string? StreamARN { get; set; }
}

internal sealed class GetRecordsResponse
{
    public KinesisRecord[] Records { get; set; } = [];
    public string NextShardIterator { get; set; } = string.Empty;
    public long MillisBehindLatest { get; set; }
    public KinesisChildShard[] ChildShards { get; set; } = [];
}

internal sealed class KinesisRecord
{
    public string SequenceNumber { get; set; } = string.Empty;
    public string Data { get; set; } = string.Empty;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PartitionKey { get; set; }

    public double ApproximateArrivalTimestamp { get; set; }
}

internal sealed class KinesisChildShard
{
}

internal sealed class PutRecordsResultEntry
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ShardId { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SequenceNumber { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorCode { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorMessage { get; set; }
}

internal sealed class ListShardsResponse
{
    public KinesisShardDescription[] Shards { get; set; } = [];

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NextToken { get; set; }
}

internal sealed class KinesisShardDescription
{
    public string ShardId { get; set; } = string.Empty;
    public HashKeyRangeDescription HashKeyRange { get; set; } = new();
    public SequenceNumberRangeDescription SequenceNumberRange { get; set; } = new();
}

internal sealed class HashKeyRangeDescription
{
    public string StartingHashKey { get; set; } = string.Empty;
    public string EndingHashKey { get; set; } = string.Empty;
}

internal sealed class SequenceNumberRangeDescription
{
    public string StartingSequenceNumber { get; set; } = string.Empty;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EndingSequenceNumber { get; set; }
}

internal sealed class EnhancedMonitoringDescription
{
    public string[] ShardLevelMetrics { get; set; } = [];
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(DescribeStreamRequest))]
[JsonSerializable(typeof(DescribeStreamResponse))]
[JsonSerializable(typeof(DescribeStreamSummaryRequest))]
[JsonSerializable(typeof(DescribeStreamSummaryResponse))]
[JsonSerializable(typeof(PutRecordRequest))]
[JsonSerializable(typeof(PutRecordResponse))]
[JsonSerializable(typeof(PutRecordsRequest))]
[JsonSerializable(typeof(PutRecordsResponse))]
[JsonSerializable(typeof(GetShardIteratorRequest))]
[JsonSerializable(typeof(GetShardIteratorResponse))]
[JsonSerializable(typeof(GetRecordsRequest))]
[JsonSerializable(typeof(GetRecordsResponse))]
[JsonSerializable(typeof(ListShardsRequest))]
[JsonSerializable(typeof(ListShardsResponse))]
internal sealed partial class KinesisJsonSerializerContext : JsonSerializerContext
{
}
