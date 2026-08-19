# kinesis / DescribeStream {#operation-kinesis-describestream}

[← kinesis operation index](../../kinesis.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:kinesis:describestream`
- **Status:** 🟡 partial
- **Disposition:** 🔵 by design
- **Azure equivalent:** `Azure Event Hubs Service Bus management REST API`
- **Real-Azure verified:** ✅ 2026-07-22 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/29929438303) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/29929438303)

## Sub-features

### StreamName and synthetic StreamARN {#sub-feature-streamname-and-synthetic-streamarn}

- **Capability ID:** `sub-feature:kinesis:describestream:streamname-and-synthetic-streamarn`
- **Status:** ✅ implemented

Accepts either StreamName or the synthetic aws2azure StreamARN and resolves the backing Event Hub from stream overrides or the stream name.

### ExclusiveStartShardId + Limit pagination {#sub-feature-exclusivestartshardid--limit-pagination}

- **Capability ID:** `sub-feature:kinesis:describestream:exclusivestartshardid--limit-pagination`
- **Status:** ✅ implemented

Paginates the Event Hubs partition list and sets HasMoreShards when more mapped shards remain.

## Behaviour differences

- Kinesis shards map 1:1 to Event Hubs partitions; shard ids are synthesised as shardId-<partitionId.PadLeft(12,'0')>. [conformance:field-value:StreamDescription.Shards[].ShardId]
- HashKeyRange values are a uniform even split of the 128-bit Kinesis hash space; Event Hubs does not expose AWS-compatible hash-key assignments.
- SequenceNumberRange.StartingSequenceNumber is always '0' and open shards omit EndingSequenceNumber because Event Hubs partitions do not surface native Kinesis sequence numbers.
- Retention, creation metadata, and the two-partition topology are verified against a live Event Hubs namespace; emulator-focused runs may instead use a configured static partition count.
- Stream lifecycle (CreateStream / DeleteStream / IncreaseStreamRetentionPeriod) is out of scope — Event Hubs entities are provisioned out-of-band via ARM.

## References

- <https://docs.aws.amazon.com/kinesis/latest/APIReference/API_DescribeStream.html>
- <https://learn.microsoft.com/en-us/rest/api/eventhub/>

