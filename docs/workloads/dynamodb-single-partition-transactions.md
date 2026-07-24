# DynamoDB single-partition transaction profile

This version 1 profile covers `TransactGetItems` and `TransactWriteItems` only
when every item belongs to one table and one logical partition. Its initial
verdict is `conditional`: the implementation and discoverable real-Azure tests
exist, but the new snapshot/write sub-features have no fresh seals and the
qualification artifact is intentionally empty.

## Required topology and configuration

- Set `DynamoDb.UseStoredProcedures` to `Preferred` or `Required`.
- Co-locate every transaction item under one partition-key value in one table.
- Do not send duplicate table/key targets in either operation.
- Keep transactions within Cosmos stored-procedure execution and response
  budgets; a rejected or failed request never falls back to partial REST calls.

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
`NULL`, or a number that round-trips exactly through JavaScript. Numeric
conditions are limited to equality/not-equal and `IN`; stored high-precision
number envelopes cannot be ordered faithfully by the script. Maps, lists,
sets, binary, unsafe numbers, nested/list-index/dotted paths, path-to-path
comparisons, `contains`, and `size` fail before execution. Missing attributes do
not satisfy `<>`, while differing DynamoDB types do.

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

The real-Azure source suite covers atomic rollback, snapshot coherence,
condition/cancellation behavior, contention, scope and token rejection, process
restart, and adjacent-runtime rollback. No new seal is committed without an
actual workflow run containing those tests.

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
load, rollback, and SLO evidence.
