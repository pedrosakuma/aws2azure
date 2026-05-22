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
- Retention and creation metadata come from the Service Bus management REST API (or an emulator-focused static partition-count override when configured); verified against Event Hubs emulator only, not yet against a live Event Hubs namespace.
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
- Retention and creation metadata come from the Service Bus management REST API (or an emulator-focused static partition-count override when configured); verified against Event Hubs emulator only, not yet against a live Event Hubs namespace.
- Stream lifecycle (CreateStream / DeleteStream / IncreaseStreamRetentionPeriod) is out of scope — Event Hubs entities are provisioned out-of-band via ARM.

### References

- <https://docs.aws.amazon.com/kinesis/latest/APIReference/API_DescribeStreamSummary.html>
- <https://learn.microsoft.com/en-us/rest/api/eventhub/>

## GetRecords

- **Status:** 🟡 partial
- **Azure equivalent:** `Azure Event Hubs (AMQP 1.0 data plane)`

### Sub-features

| Name | Status | Notes | Gap | Workaround |
|---|---|---|---|---|
| Event Hubs AMQP partition receive | ✅ implemented | Consumes Event Hubs AMQP receive links against ConsumerGroups/{group}/Partitions/{id}. |  |  |
| Core iterator types | ✅ implemented | Supports TRIM_HORIZON, LATEST, AT_TIMESTAMP, AT_SEQUENCE_NUMBER, and AFTER_SEQUENCE_NUMBER iterators via stateless proxy-issued tokens. |  |  |

### Behaviour differences

- Returned SequenceNumber values are Event Hubs-assigned x-opt-sequence-number annotations, which differ from the synthetic sequence numbers returned by PutRecord/PutRecords.
- NextShardIterator uses the proxy's opaque token and internally prefers Event Hubs offsets (offset:<value>) to resume reads; callers must treat the token as opaque.
- MillisBehindLatest is best-effort only and is derived from the last returned record's enqueue timestamp versus the proxy clock.
- ChildShards is always an empty array because Event Hubs partitions are fixed and the proxy does not model Kinesis split/merge lineage.
- AT_TIMESTAMP is translated to an Event Hubs enqueue-time selector that is exclusive (>) rather than AWS's inclusive semantics, so a record at the exact timestamp boundary may be skipped.
- AT_SEQUENCE_NUMBER and AFTER_SEQUENCE_NUMBER are best-effort only: the proxy derives an Event Hubs enqueue-time position from aws2azure's synthetic PutRecord sequence number ((unixMs << 20) | counter). AT_SEQUENCE_NUMBER subtracts 1 ms before applying Event Hubs' exclusive selector so boundary records are included at millisecond granularity; records from the same millisecond may also be returned, which preserves AWS's inclusive boundary intent. If parsing fails the read falls back to the start of the shard.
- Verified against Event Hubs emulator; not yet validated against production Azure Event Hubs.

### References

- <https://docs.aws.amazon.com/kinesis/latest/APIReference/API_GetRecords.html>

## GetShardIterator

- **Status:** 🟡 partial
- **Azure equivalent:** `Azure Event Hubs (AMQP 1.0 data plane)`

### Sub-features

| Name | Status | Notes | Gap | Workaround |
|---|---|---|---|---|
| Stateless HMAC-signed iterator tokens | ✅ implemented | The proxy issues opaque shard iterators signed with the configured shard-iterator signing key (or the process-local fallback) and enforces a 5-minute TTL. |  |  |
| Core iterator types | ✅ implemented | Supports TRIM_HORIZON, LATEST, AT_TIMESTAMP, AT_SEQUENCE_NUMBER, and AFTER_SEQUENCE_NUMBER request shapes. |  |  |

### Behaviour differences

- Iterators are proxy-issued opaque tokens rather than broker cursors; they remain valid for 5 minutes and require the proxy's configured shard-iterator signing key (or the process-local fallback key after restartless reuse).
- AT_SEQUENCE_NUMBER and AFTER_SEQUENCE_NUMBER are best-effort only: the proxy interprets aws2azure's synthetic PutRecord sequence number as (unixMs << 20) | counter and derives an Event Hubs enqueue-time position from unixMs. If parsing fails the follow-up read falls back to the start of the shard.
- AT_TIMESTAMP positions are stored as ISO-8601 UTC in the opaque token; Timestamp values outside DateTimeOffset's supported Unix-millisecond range are rejected with ValidationException instead of surfacing an internal error.
- Verified against Event Hubs emulator; not yet validated against production Azure Event Hubs.

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
- Verified against Event Hubs emulator only, not yet against a live Event Hubs namespace.
- Stream lifecycle (CreateStream / DeleteStream / IncreaseStreamRetentionPeriod) is out of scope — Event Hubs entities are provisioned out-of-band via ARM.

### References

- <https://docs.aws.amazon.com/kinesis/latest/APIReference/API_ListShards.html>
- <https://learn.microsoft.com/en-us/rest/api/eventhub/>

## PutRecord

- **Status:** 🟡 partial
- **Azure equivalent:** `Azure Event Hubs (AMQP 1.0 data plane)`

### Behaviour differences

- SequenceNumber is synthetic and proxy-generated from a per-process monotonic counter; it is not the Event Hubs broker-assigned sequence number or offset.
- ShardId is derived client-side by hashing PartitionKey with MD5 and routing to {eventHub}/Partitions/{id}. This matches Event Hubs' historical partitioning algorithm, but the broker may diverge if Azure changes its internal hashing in the future.
- ExplicitHashKey and SequenceNumberForOrdering are accepted for wire compatibility but ignored.
- EncryptionType is always reported as NONE.
- Verified against Event Hubs emulator; not yet validated against production Azure Event Hubs.

### References

- <https://docs.aws.amazon.com/kinesis/latest/APIReference/API_PutRecord.html>

## PutRecords

- **Status:** 🟡 partial
- **Azure equivalent:** `Azure Event Hubs (AMQP 1.0 data plane)`

### Behaviour differences

- Sequence numbers are synthetic proxy-generated values, not Azure Event Hubs offsets.
- ShardId values are derived client-side by hashing PartitionKey with MD5 and mapping the result modulo the Event Hubs partition count.
- Batch sends are still grouped per partition, but broker dispositions are tracked per message; records accepted before a later reject remain successful in the PutRecords response so callers do not retry already-committed messages.
- ExplicitHashKey is ignored; partition routing always follows the PartitionKey hash.
- Verified against Event Hubs emulator; not yet validated against production Azure Event Hubs.

### References

- <https://docs.aws.amazon.com/kinesis/latest/APIReference/API_PutRecords.html>

