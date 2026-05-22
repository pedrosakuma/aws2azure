# kinesis

## DescribeStream

- **Status:** ⚪ stub
- **Azure equivalent:** `Azure Event Hubs runtime + management API`

### Behaviour differences

- Phase 4 Slice 1 scaffolds routing + AWS-JSON-1.1 parsing + EventHubs credential gating only; the operation returns HTTP 501 InternalFailure until its dedicated slice lands.
- Shard ids are synthesised from EH partition ids using a fixed 'shardId-<partitionId>' convention; AWS's 49-character base-36 shard ids are NOT preserved.
- Stream lifecycle (CreateStream / DeleteStream / IncreaseStreamRetentionPeriod) is out of scope — Event Hubs entities are provisioned out-of-band via ARM.

### References

- <https://docs.aws.amazon.com/kinesis/latest/APIReference/API_DescribeStream.html>

## DescribeStreamSummary

- **Status:** ⚪ stub
- **Azure equivalent:** `Azure Event Hubs runtime + management API`

### Behaviour differences

- Phase 4 Slice 1 scaffolds routing + AWS-JSON-1.1 parsing + EventHubs credential gating only; the operation returns HTTP 501 InternalFailure until its dedicated slice lands.
- Shard ids are synthesised from EH partition ids using a fixed 'shardId-<partitionId>' convention; AWS's 49-character base-36 shard ids are NOT preserved.
- Stream lifecycle (CreateStream / DeleteStream / IncreaseStreamRetentionPeriod) is out of scope — Event Hubs entities are provisioned out-of-band via ARM.

### References

- <https://docs.aws.amazon.com/kinesis/latest/APIReference/API_DescribeStreamSummary.html>

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

- **Status:** ⚪ stub
- **Azure equivalent:** `Azure Event Hubs runtime + management API`

### Behaviour differences

- Phase 4 Slice 1 scaffolds routing + AWS-JSON-1.1 parsing + EventHubs credential gating only; the operation returns HTTP 501 InternalFailure until its dedicated slice lands.
- Shard ids are synthesised from EH partition ids using a fixed 'shardId-<partitionId>' convention; AWS's 49-character base-36 shard ids are NOT preserved.
- Stream lifecycle (CreateStream / DeleteStream / IncreaseStreamRetentionPeriod) is out of scope — Event Hubs entities are provisioned out-of-band via ARM.

### References

- <https://docs.aws.amazon.com/kinesis/latest/APIReference/API_ListShards.html>

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

