# Kinesis basic record ingestion profile

This profile covers `PutRecord` and `PutRecords` against a provisioned Azure
Event Hub for stream writes. It does not cover reading records back
(`ListShards`, `GetShardIterator`, `GetRecords`) or consumer-side shard
positioning — that surface is tracked separately by the
[single-consumer-per-shard profile](https://github.com/pedrosakuma/aws2azure/issues/629).
`CreateStream`, resharding (`SplitShard`/`MergeShards`), enhanced fan-out
(`SubscribeToShard`), and the KCL DynamoDB lease/checkpoint model are explicit
non-goals and are not required by profile version 1.

## Required deployment contract

- Provision the target Event Hub's partition count for peak expected
  ingestion concurrency up front. Event Hubs partitions are fixed at the hub
  level: there is no `SplitShard`/`MergeShards` equivalent, so partition count
  is a topology decision made outside the proxy, not a runtime API call.
- `PartitionKey` is hashed client-side (MD5, modulo the Event Hub's partition
  count) to select the target partition and reported back as `ShardId`. This
  reproduces Event Hubs' historical partition-hashing behavior but is not a
  guarantee against a future Azure-side change; do not depend on exact
  partition assignment across a broker-side rehash.
- `ExplicitHashKey` and `SequenceNumberForOrdering` are accepted for wire
  compatibility but ignored — partition routing always follows the
  `PartitionKey` hash.
- Use bounded AWS SDK retries for the retryable throttling, timeout, and
  service-unavailable responses mapped from the Event Hubs AMQP data plane.
  Publishing is not implicitly idempotent; a retried `PutRecord`/`PutRecords`
  call can duplicate a record that was already committed, exactly as with
  native Kinesis.

## Authentication paths

Both of the profile's supported authentication paths against the Event Hubs
AMQP data plane are validated:

- **Shared Access Signature (SAS)** — the default binding: an Event Hubs
  namespace/entity SAS key resolved from the binding configuration.
- **Entra Workload Identity** — a federated-token credential resolved through
  Azure AD, exercised by
  [`KinesisRealAzureWorkloadIdentityTests`](../../tests/Aws2Azure.IntegrationTests/Kinesis/KinesisRealAzureWorkloadIdentityTests.cs)
  against a live Event Hub with no SAS key configured (issue #307).

Select one authentication mode per binding; the proxy does not fall back from
one to the other within a single binding.

## Behavioral qualification

`PutRecords` preserves per-entry success and failure: records accepted before
a later entry is rejected remain successful in the response so callers do not
retry already-committed records, matching the AWS batch response contract.
Rejected entries carry the native retryable/non-retryable Kinesis error code
so the AWS SDK's built-in retry policy applies unchanged. `SequenceNumber` is
synthetic — proxy-generated from a per-process monotonic counter — and is
never the Event Hubs broker-assigned offset; do not persist or compare it
across proxy restarts or instances.

Before adoption, exercise representative concurrent producer load, partition-
key routing stability, batch partial-failure handling, bounded throttling and
timeout retries, service-unavailable recovery, proxy restart, and the selected
Event Hub's provisioned throughput/partition capacity. The repository's
real-Azure conformance seals establish operation correctness for both
operations; the profile remains `candidate` until production-shaped SLO and
rollback qualification are reviewed and committed.

A restarted proxy loses any pooled, per-process AMQP link state; a `LATEST`
shard iterator obtained before a restart is not guaranteed to resume from
where it left off (see the "shared broker cursor per consumer group" design
gap). Client applications that must tolerate a proxy restart mid-consumption
should re-derive a durable iterator (`AT_TIMESTAMP` or the last-seen
`AFTER_SEQUENCE_NUMBER`) rather than reuse a `LATEST` iterator across a
reconnect.

## Scope boundaries and non-goals

- Resharding, enhanced fan-out, and KCL lease management have no in-scope
  translation and remain permanent non-goals, not temporary gaps.
- Event Hubs capacity is a fixed-partition, throughput-unit-provisioned
  topology configured outside the proxy; this profile does not claim
  DynamoDB-style auto-scaling or Kinesis-native on-demand shard control.
- A dedicated AWS-SDK bounded retry-exhaustion harness for the AMQP data path
  is not yet built (the four REST-backed modules share an HTTP-level fake
  backend that cannot intercept AMQP calls); retryable-error classification
  for throttling, timeout, and service-unavailable is instead covered
  deterministically at the AMQP sender seam. Closing this gap is tracked as
  follow-up work, not asserted as complete here.
