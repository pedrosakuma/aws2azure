# kinesis

## DescribeStream

- **Status:** 🟡 partial
- **Azure equivalent:** `Azure Event Hubs Service Bus management REST API`

### Sub-features

| Name | Status | Notes | Gap | Workaround |
|---|---|---|---|---|
| StreamName and synthetic StreamARN | ✅ implemented | Accepts either StreamName or the synthetic aws2azure StreamARN and resolves the backing Event Hub from stream overrides or the stream name. |  |  |
| ExclusiveStartShardId + Limit pagination | ✅ implemented | Paginates the Event Hubs partition list and sets HasMoreShards when more mapped shards remain. |  |  |

### Behaviour differences

- Kinesis shards map 1:1 to Event Hubs partitions; shard ids are synthesised as shardId-<partitionId.PadLeft(12,'0')>.
- HashKeyRange values are a uniform even split of the 128-bit Kinesis hash space; Event Hubs does not expose AWS-compatible hash-key assignments.
- SequenceNumberRange.StartingSequenceNumber is always '0' and open shards omit EndingSequenceNumber because Event Hubs partitions do not surface native Kinesis sequence numbers.
- Retention and creation metadata come from the Service Bus management REST API; this slice has only been verified with tests, not yet against a live Event Hubs namespace.
- Stream lifecycle (CreateStream / DeleteStream / IncreaseStreamRetentionPeriod) is out of scope — Event Hubs entities are provisioned out-of-band via ARM.

### References

- <https://docs.aws.amazon.com/kinesis/latest/APIReference/API_DescribeStream.html>
- <https://learn.microsoft.com/en-us/rest/api/eventhub/>

## DescribeStreamSummary

- **Status:** 🟡 partial
- **Azure equivalent:** `Azure Event Hubs Service Bus management REST API`

### Sub-features

| Name | Status | Notes | Gap | Workaround |
|---|---|---|---|---|
| StreamName and synthetic StreamARN | ✅ implemented | Accepts either StreamName or the synthetic aws2azure StreamARN and resolves the backing Event Hub from stream overrides or the stream name. |  |  |

### Behaviour differences

- OpenShardCount is the Event Hub partition count; Event Hubs does not expose a separate open/closed shard lifecycle.
- EnhancedMonitoring is always the empty [{ShardLevelMetrics: []}] shape and ConsumerCount is always 0 because Event Hubs does not expose Kinesis-compatible consumer metadata here.
- Retention and creation metadata come from the Service Bus management REST API; this slice has only been verified with tests, not yet against a live Event Hubs namespace.
- Stream lifecycle (CreateStream / DeleteStream / IncreaseStreamRetentionPeriod) is out of scope — Event Hubs entities are provisioned out-of-band via ARM.

### References

- <https://docs.aws.amazon.com/kinesis/latest/APIReference/API_DescribeStreamSummary.html>
- <https://learn.microsoft.com/en-us/rest/api/eventhub/>

## GetRecords

- **Status:** ⚪ stub
- **Azure equivalent:** `Azure Event Hubs (AMQP 1.0 data plane)`

### Behaviour differences

- Phase 4 Slice 1 scaffolds routing + AWS-JSON-1.1 parsing + EventHubs credential gating only; the operation returns HTTP 501 InternalFailure until its dedicated slice lands.
- Kinesis shards map 1:1 to Event Hubs partitions. Partition keys are hashed (MD5) into the shard index on AWS; the proxy will assign EH partitions deterministically from the same partition key but cannot guarantee identical shard ids without explicit stream-to-partition-count parity.
- Sequence numbers are not the same as EH offsets; the proxy will surface EH offsets (or an opaque equivalent) where AWS surfaces sequence numbers.

### References

- <https://docs.aws.amazon.com/kinesis/latest/APIReference/API_GetRecords.html>

## GetShardIterator

- **Status:** ⚪ stub
- **Azure equivalent:** `Azure Event Hubs (AMQP 1.0 data plane)`

### Behaviour differences

- Phase 4 Slice 1 scaffolds routing + AWS-JSON-1.1 parsing + EventHubs credential gating only; the operation returns HTTP 501 InternalFailure until its dedicated slice lands.
- Kinesis shards map 1:1 to Event Hubs partitions. Partition keys are hashed (MD5) into the shard index on AWS; the proxy will assign EH partitions deterministically from the same partition key but cannot guarantee identical shard ids without explicit stream-to-partition-count parity.
- Sequence numbers are not the same as EH offsets; the proxy will surface EH offsets (or an opaque equivalent) where AWS surfaces sequence numbers.

### References

- <https://docs.aws.amazon.com/kinesis/latest/APIReference/API_GetShardIterator.html>

## ListShards

- **Status:** 🟡 partial
- **Azure equivalent:** `Azure Event Hubs Service Bus management REST API`

### Sub-features

| Name | Status | Notes | Gap | Workaround |
|---|---|---|---|---|
| ExclusiveStartShardId + MaxResults pagination | ✅ implemented | Paginates the Event Hubs partition list and emits aws2azure NextToken cursors when more mapped shards remain. |  |  |
| HMAC-signed NextToken cursors | ✅ implemented | Uses the Event Hubs shard iterator signing key (or an ephemeral fallback) to sign 5-minute list-shards cursors. |  |  |
| AT_LATEST / FROM_TRIM_HORIZON shard filters | ✅ implemented | These filter types are accepted as no-ops because Event Hubs always exposes the full open-partition set. |  |  |

### Behaviour differences

- Kinesis shards map 1:1 to Event Hubs partitions; shard ids are synthesised as shardId-<partitionId.PadLeft(12,'0')>.
- HashKeyRange values are a uniform even split of the 128-bit Kinesis hash space; Event Hubs does not expose AWS-compatible hash-key assignments.
- NextToken is an aws2azure-specific cursor, not an AWS-issued token; it encodes stream name + last shard id and expires after 5 minutes.
- Shard filter types other than AT_LATEST and FROM_TRIM_HORIZON currently return ValidationException.
- This slice has only been verified with tests, not yet against a live Event Hubs namespace.
- Stream lifecycle (CreateStream / DeleteStream / IncreaseStreamRetentionPeriod) is out of scope — Event Hubs entities are provisioned out-of-band via ARM.

### References

- <https://docs.aws.amazon.com/kinesis/latest/APIReference/API_ListShards.html>
- <https://learn.microsoft.com/en-us/rest/api/eventhub/>

## PutRecord

- **Status:** ⚪ stub
- **Azure equivalent:** `Azure Event Hubs (AMQP 1.0 data plane)`

### Behaviour differences

- Phase 4 Slice 1 scaffolds routing + AWS-JSON-1.1 parsing + EventHubs credential gating only; the operation returns HTTP 501 InternalFailure until its dedicated slice lands.
- Kinesis shards map 1:1 to Event Hubs partitions. Partition keys are hashed (MD5) into the shard index on AWS; the proxy will assign EH partitions deterministically from the same partition key but cannot guarantee identical shard ids without explicit stream-to-partition-count parity.
- Sequence numbers are not the same as EH offsets; the proxy will surface EH offsets (or an opaque equivalent) where AWS surfaces sequence numbers.

### References

- <https://docs.aws.amazon.com/kinesis/latest/APIReference/API_PutRecord.html>

## PutRecords

- **Status:** ⚪ stub
- **Azure equivalent:** `Azure Event Hubs (AMQP 1.0 data plane)`

### Behaviour differences

- Phase 4 Slice 1 scaffolds routing + AWS-JSON-1.1 parsing + EventHubs credential gating only; the operation returns HTTP 501 InternalFailure until its dedicated slice lands.
- Kinesis shards map 1:1 to Event Hubs partitions. Partition keys are hashed (MD5) into the shard index on AWS; the proxy will assign EH partitions deterministically from the same partition key but cannot guarantee identical shard ids without explicit stream-to-partition-count parity.
- Sequence numbers are not the same as EH offsets; the proxy will surface EH offsets (or an opaque equivalent) where AWS surfaces sequence numbers.

### References

- <https://docs.aws.amazon.com/kinesis/latest/APIReference/API_PutRecords.html>

