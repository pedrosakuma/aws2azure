# Kinesis single-consumer-per-shard profile

This profile covers `DescribeStream`, `DescribeStreamSummary`, `ListShards`,
`GetShardIterator`, and `GetRecords` for one polling loop per Event Hubs
partition and consumer group. Event Hubs partitions are exposed as fixed
Kinesis shards; provisioning, resharding, enhanced fan-out, and KCL lease
management remain explicit non-goals.

## Topology and cursor contract

- Provision the Event Hub partition count for peak load before deployment.
  `DescribeStream`, `DescribeStreamSummary`, and `ListShards` expose that fixed
  topology as synthetic `shardId-000000000000` identifiers.
- Run one ordered `GetRecords` loop per shard. Always replace the current token
  with `NextShardIterator`; do not issue concurrent calls on one iterator chain.
- Every `GetShardIterator` response has a distinct iterator identity. The proxy
  pools a separate AMQP receiver link for each identity, so live iterator chains
  advance independently even when they target the same partition.
- The certified topology is still one independently operated consumer per
  partition and consumer group. Use a distinct Event Hubs consumer group for
  each consumer that needs its own ownership, checkpoint, restart, and replay
  lifecycle.

## Positioning and restart behavior

`TRIM_HORIZON`, `LATEST`, and `AT_TIMESTAMP` are the preferred iterator types.
`LATEST` takes effect when the first `GetRecords` call opens the receiver, not
when `GetShardIterator` returns; prime the iterator before publishing when the
boundary matters. `AT_TIMESTAMP` uses Event Hubs enqueue time.

`AT_SEQUENCE_NUMBER` and `AFTER_SEQUENCE_NUMBER` accept aws2azure synthetic
producer sequence numbers. They recover the embedded millisecond timestamp and
therefore provide only millisecond-granularity positioning; records sharing the
boundary millisecond can be returned together.

Opaque iterator and pagination tokens expire after five minutes. Tokens issued
in the future are rejected as expired. Configure a stable
`shardIteratorSigningKey` so tokens remain verifiable across proxy replacement.
After restart or receiver-link eviction, continuation tokens recreate the link
from their embedded Event Hubs offset or enqueue-time position.

## Polling, cancellation, and retries

Empty `GetRecords` responses are normal and return a refreshed continuation
without advancing the embedded position. A successful non-empty read advances
past the last included Event Hubs offset. Cancellation propagates without
minting a continuation, and failed receives do not advance the iterator.

Use bounded AWS SDK retries for retryable throttling and transient failures.
`ProvisionedThroughputExceededException` is retryable; authentication and
invalid/expired iterator errors require caller action. After retry exhaustion,
resume with the last successfully returned `NextShardIterator`, never a token
from a failed call.

## Qualification boundaries

Qualification sources cover topology, pagination, iterator types, empty reads,
independent progression, expiry, cancellation, restart, retry boundaries,
representative load, and sealed-runtime rollback. The profile remains
`conditional` until issue #657 refreshes the missing real-Azure operation seals;
it then remains `candidate` until reviewed representative-load and rollback
evidence qualifies it.

Shared-consumer-group multi-owner replay, resharding, enhanced fan-out, and KCL
remain blocked by design and are not implied by this profile.
