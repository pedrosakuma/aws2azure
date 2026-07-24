# DynamoDB single-partition transaction profile

This version 1 profile covers `TransactGetItems` and `TransactWriteItems` only
when every item belongs to one table and one logical partition. Its initial
verdict is `conditional`: the implementation and discoverable real-Azure tests
exist, but the new snapshot/write sub-features have no fresh seals, the
qualification artifact is intentionally empty, and no trusted prior release
implements the complete profile well enough to qualify rollback.

## Required topology and configuration

- Set `DynamoDb.UseStoredProcedures` to `Preferred` or `Required`.
- Co-locate every transaction item under one partition-key value in one table.
- Do not send duplicate table/key targets in either operation.
- Keep transactions within Cosmos stored-procedure execution and response
  budgets. The exact serialized stored-procedure parameter body must be at most
  2 MiB, below DynamoDB's 4 MiB aggregate transaction limit; larger requests
  fail with `ValidationException` before stored-procedure provisioning/execution.
  A rejected or failed request never falls back to partial REST calls.

Cross-table and cross-partition transactions fail with `ValidationException`
before item data is read. That is a permanent Cosmos transaction-scope boundary,
not an eventual roadmap promise.

## Certified read contract

`TransactGetItems` invokes `atomicTransactGet_v1`. All positions are queried
inside one read-only Cosmos stored-procedure transaction, producing a coherent
single-partition snapshot. Responses remain positionally aligned, missing items
emit `{}`, and each position applies its own projection expression. A malformed
or count-mismatched 2xx script response fails closed.

## Certified write and condition contract

`TransactWriteItems` invokes `atomicTransactWrite_v4` for `Put`, `Delete`, and
`ConditionCheck`. `Update` is unsupported. The accepted condition subset is:

- `AND`, `OR`, `NOT`;
- scalar comparisons with one path and one literal in either operand order,
  string `BETWEEN`, plus scalar `IN`;
- `attribute_exists`, `attribute_not_exists`, `begins_with`;
- `attribute_type` for `S`, `BOOL`, and `NULL`.

Paths must be one non-reserved top-level attribute and cannot name Cosmos system
fields (`_etag`, `_ts`, and peers). Values must be `S`, `BOOL`,
`NULL`, or a number whose canonical form the persisted codec stores as a bare
JSON number. Numeric conditions are limited to equality/not-equal and `IN`;
numbers stored in `_a2a:N` envelopes fail before execution. Ordered strings use
DynamoDB's UTF-8 byte lexicographic order rather than JavaScript UTF-16 order.
Maps, lists, sets, binary, enveloped numbers, nested/list-index/dotted paths,
path-to-path comparisons, `contains`, and `size` fail before execution. Missing
attributes do not satisfy `<>`, while differing DynamoDB types do.
Runtime operand-type errors for ordered operators, `BETWEEN`, and
`begins_with` are returned by the script as a structured validation result and
mapped to `ValidationException`; `NOT` cannot invert such an error into success.

Every transactional item, key, and expression value is validated before table
metadata or stored-procedure I/O. Empty sets, malformed binary/base64, invalid or
out-of-range numbers (including set members), duplicate set members, and invalid
AttributeValue shapes are rejected. Empty strings remain allowed in non-empty
string sets, matching current DynamoDB policy. Every declared
`ExpressionAttributeNames` and `ExpressionAttributeValues` placeholder must be
consumed by the condition.

Cancellation reasons are exact and positional. Legacy `Expected` /
`ConditionalOperator`, `ReturnValuesOnConditionCheckFailure`, non-`NONE`
capacity/collection metrics are rejected rather than ignored.

`ClientRequestToken` accepts DynamoDB's 1–36 character range. The v4 script
commits a proxy-reserved token record in the same logical partition and Cosmos
transaction as the user writes. A canonical semantic fingerprint resolves
expression aliases, normalizes numbers and base64, sorts map properties and set
members, and preserves transaction operation order. Equivalent retries,
including retries after a discarded response or process restart, replay the
original success or condition-cancellation reasons without applying writes
again. A changed request in the active window returns
`IdempotentParameterMismatchException`. Cosmos server time defines the exact
10-minute window; records carry created/expiry timestamps, native ttl, and a
bounded partition-local cleanup fallback. Preflight/script validation failures
are not cached. Because Cosmos cannot atomically coordinate two logical
partitions, this contract is scoped to the profile's supported single-partition
boundary; do not reuse one token for a different partition.

## Versioning and rollback

`atomicWrite_v2`, `atomicTransactWrite_v2`, and `atomicTransactWrite_v3` remain
byte-identical with their frozen hashes. Persisted-format inventory version 3
adds `atomicTransactWrite_v4`, the internal idempotency-record v1 format, and
retains `atomicTransactGet_v1`.
Provisioning accepts HTTP 409 only after reading and matching the exact stored
body, so an accidental same-id body conflict cannot be treated as available.
The availability cache is scoped to account/database/container and is cleared
on table lifecycle; an execution 404 evicts and reprovisions once. Transaction
write execution retains the no-automatic-retry transport option. Token-bearing
requests retry only Cosmos write-conflict statuses once; explicit caller retries
after any ambiguous response replay the durable outcome. Tokenless writes remain
non-retried.

The real-Azure source suite covers atomic write rollback, snapshot coherence,
condition/cancellation behavior, contention, scope, durable token replay,
mismatch/concurrency/cancellation behavior, process
restart, and an isolated same-ID conflicting-body probe that exercises the real
Cosmos 409/read/verify path and restores the exact v4 body. No new seal is
committed without an actual workflow run containing those tests.

There is deliberately no approved-runtime ledger for this profile. The
previously proposed v2 runtime performs independent transaction reads and does
not implement the profile's durable `ClientRequestToken` contract, so it is not
a valid bootstrap or rollback target. A sealed workflow run is candidate-only
and rollout-only: the adjacent-runtime rows skip with the recorded compatibility
blocker, workload generation must emit `inconclusive`, and the workflow must not
claim rollback success. Rollback qualification can begin only after a distinct,
trusted prior release implements this same profile.

## Performance and qualification boundary

The registered scenarios are
`dynamodb.TransactGetItems (10 items, single partition)` and
`dynamodb.TransactWriteItems (5 puts, single partition)` in
`tests/Aws2Azure.PerfTests/DynamoDb/DynamoDbPerfTests.cs`. They call
`AssertHealthy()` followed by `AssertNoRegression()` and currently use explicit
zero-threshold waivers pending reviewed measurements.

The Cosmos Linux emulator cannot execute stored procedures, so these scenarios
skip there and require real Azure. Any future throughput/latency claim must cite
the real-Azure harness run; emulator results from other DynamoDB scenarios do
not qualify this profile. GA additionally requires reviewed production-shaped
load, rollback, and SLO evidence. Until a compatible prior release exists,
operators must treat deployment as rollout-only with no qualified rollback.
