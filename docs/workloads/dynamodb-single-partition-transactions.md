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

`TransactWriteItems` invokes `atomicTransactWrite_v3` for `Put`, `Delete`, and
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
capacity/collection metrics, and `ClientRequestToken` string values are rejected
rather than ignored. Durable idempotent replay is therefore an explicit gap.

## Versioning and rollback

`atomicWrite_v2` remains byte-identical. `atomicTransactWrite_v2` and its hash
remain in persisted-format inventory version 2 for the adjacent-runtime rollback
span; the candidate adds `atomicTransactWrite_v3` and `atomicTransactGet_v1`.
Provisioning accepts HTTP 409 only after reading and matching the exact stored
body, so an accidental same-id body conflict cannot be treated as available.
The availability cache is scoped to account/database/container and is cleared
on table lifecycle; an execution 404 evicts and reprovisions once. Transaction
write execution disables proxy-level automatic retries because a lost response
is ambiguous until durable `ClientRequestToken` deduplication exists.

The real-Azure source suite covers atomic write rollback, snapshot coherence,
condition/cancellation behavior, contention, scope and token rejection, process
restart, and an isolated same-ID conflicting-body probe that exercises the real
Cosmos 409/read/verify path and restores the exact v3 body. No new seal is
committed without an actual workflow run containing those tests.

There is deliberately no approved-runtime ledger for this profile. The
previously proposed v2 runtime performs independent transaction reads and does
not enforce the profile's `ClientRequestToken` rejection contract, so it is not
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
