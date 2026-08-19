# kinesis / DescribeStreamSummary {#operation-kinesis-describestreamsummary}

[← kinesis operation index](../../kinesis.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:kinesis:describestreamsummary`
- **Status:** 🟡 partial
- **Disposition:** 🔵 by design
- **Azure equivalent:** `Azure Event Hubs Service Bus management REST API`
- **Real-Azure verified:** ✅ 2026-07-22 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/29929438303) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/29929438303)

## Sub-features

### StreamName and synthetic StreamARN {#sub-feature-streamname-and-synthetic-streamarn}

- **Capability ID:** `sub-feature:kinesis:describestreamsummary:streamname-and-synthetic-streamarn`
- **Status:** ✅ implemented

Accepts either StreamName or the synthetic aws2azure StreamARN and resolves the backing Event Hub from stream overrides or the stream name.

## Behaviour differences

- OpenShardCount is the Event Hub partition count; Event Hubs does not expose a separate open/closed shard lifecycle.
- EnhancedMonitoring is always the empty [{ShardLevelMetrics: []}] shape and ConsumerCount is always 0 because Event Hubs does not expose Kinesis-compatible consumer metadata here.
- Retention, creation metadata, and OpenShardCount are verified against a live two-partition Event Hubs namespace; emulator-focused runs may instead use a configured static partition count.
- Stream lifecycle (CreateStream / DeleteStream / IncreaseStreamRetentionPeriod) is out of scope — Event Hubs entities are provisioned out-of-band via ARM.

## References

- <https://docs.aws.amazon.com/kinesis/latest/APIReference/API_DescribeStreamSummary.html>
- <https://learn.microsoft.com/en-us/rest/api/eventhub/>

