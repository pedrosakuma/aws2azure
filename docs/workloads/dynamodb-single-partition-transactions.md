# DynamoDB single-partition transaction profile

This version 1 profile covers `TransactGetItems` and `TransactWriteItems` only
when every item belongs to one table and one logical partition. The first
complete compatible protected-main runtime is now recorded as a rollback-only
bootstrap, so compatibility is no longer blocked. The profile remains
non-qualified: the new snapshot/write sub-features have no committed fresh
seals, the qualification artifact is intentionally empty, and the bootstrap is
not promotion eligible.

## Required topology and configuration

- Set `DynamoDb.UseStoredProcedures` to `Preferred` or `Required`.
- Co-locate every transaction item under one partition-key value in one table.
- Do not send duplicate table/key targets in either operation.
- Use a single-write Cosmos account, or set the binding's
  `target.preferredRegions[0]` to the intended transaction authority. Configure
  that same first entry on every replica and binding targeting the same data.
  Later entries remain available for ordinary routing but are never transaction
  fallbacks. If the authority is not reported writable, transactions fail
  retryably rather than running in a later region.
- Keep transactions within Cosmos stored-procedure execution and response
  budgets. The exact serialized stored-procedure parameter body must be at most
  2 MiB, below DynamoDB's 4 MiB aggregate transaction limit. A bounded pooled
  writer stops serialization at overflow, and token fingerprints are hashed
  incrementally without retaining a second canonical transaction body. Larger
  requests fail with `ValidationException` before stored-procedure
  provisioning/execution. A rejected or failed request never falls back to
  partial REST calls.
- Keep each transactional Put's base item at or below 400 KiB. On tables with
  LSIs, the base item plus every corresponding projected LSI entry must also fit
  in 400 KiB. KEYS_ONLY, INCLUDE, ALL, sparse-index membership, and repeated
  base/index key names follow DynamoDB projection sizing.

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

`TransactWriteItems` invokes `atomicTransactWrite_v5` for `Put`, `Delete`, and
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

Every transactional item, key, and expression value receives shape validation
before table metadata or stored-procedure I/O. Empty sets, malformed
binary/base64, invalid or out-of-range numbers (including set members), duplicate
set members, and invalid AttributeValue shapes are rejected. After metadata is
loaded, present GSI/LSI key attributes must match their declared S/N/B type, be
non-empty, and fit DynamoDB's 2,048-byte partition key or 1,024-byte sort key
limit before LSI sizing or stored-procedure I/O; absent sparse-index keys remain
valid. Empty strings remain allowed in non-empty string sets, matching current
DynamoDB policy. Condition expressions are limited to 4 KiB encoded UTF-8, 300
operators, an AST depth of 300, and parser nesting of 64; placeholder identifiers
are limited to 255 encoded bytes. Every declared
`ExpressionAttributeNames` and `ExpressionAttributeValues` placeholder must be
consumed by the condition.

Cancellation reasons are exact and positional. Legacy `Expected` /
`ConditionalOperator`, `ReturnValuesOnConditionCheckFailure`, non-`NONE`
capacity/collection metrics are rejected rather than ignored.

`ClientRequestToken` accepts DynamoDB's 1–36 character range. The v5 script
commits a proxy-reserved token record in the same logical partition and Cosmos
transaction as the user writes. A canonical semantic fingerprint resolves
expression aliases, normalizes numbers and base64, sorts map properties and set
members, and preserves transaction operation order. Equivalent retries,
including retries after a discarded response or process restart, replay the
original success or condition-cancellation reasons without applying writes
again. A changed request in the active window returns
`IdempotentParameterMismatchException`. The script samples Cosmos server time
immediately before the atomic token upsert/completion, so reads and condition
work do not shorten the 10-minute post-completion window. Records carry
created/expiry timestamps, native ttl, and a bounded partition-local cleanup
fallback. Preflight/script validation failures are not cached. Because Cosmos
cannot atomically coordinate two logical partitions, this contract is scoped to
the profile's supported single-partition boundary; do not reuse one token for a
different partition.

Every transaction snapshot, write, and idempotency-record operation uses one
regional authority. Single-write accounts use their explicit writable location.
For multi-write accounts, `target.preferredRegions[0]` is the authority across
restarts and topology refreshes. A fresh process that discovers only a later
writable region fails retryably and never executes there or through the global
account endpoint. A 403/3, 408, 503, timeout, or transport failure likewise never
triggers regional replay. This opt-in trades transaction availability during an
authority-region outage for stable atomic/idempotency history. Treat a first-entry
change as a coordinated migration after outstanding 10-minute token windows and
replication converge. Ordinary non-transactional routing is unchanged.

## Versioning and rollback

`atomicWrite_v2` and `atomicTransactWrite_v2`/`v3`/`v4` remain byte-identical
with their frozen hashes. Persisted-format inventory version 4 adds
`atomicTransactWrite_v5`, retains the internal idempotency-record v1 format, and
retains `atomicTransactGet_v1`.
Provisioning accepts HTTP 409 only after reading and matching the exact stored
body, so an accidental same-id body conflict cannot be treated as available.
The availability cache is scoped to account/database/container and is cleared on
table lifecycle; an execution 404 evicts and reprovisions once on the same pinned
endpoint. Transaction write execution retains the no-automatic-retry transport
option. Token-bearing requests retry only Cosmos write-conflict statuses once on
that endpoint; explicit caller retries after any ambiguous response replay the
durable outcome. Tokenless writes remain non-retried.

The real-Azure source suite covers atomic write rollback, snapshot coherence,
condition/cancellation behavior, contention, scope, durable token replay,
mismatch/concurrency/cancellation behavior, process
restart, and an isolated same-ID conflicting-body probe that exercises the real
Cosmos 409/read/verify path and restores the exact v5 body. No new seal is
committed without an actual workflow run containing those tests.

The approved-runtime ledger records protected-main run `30184664479` only as
the first complete compatible rollback bootstrap. The previously proposed v2
runtime still performs independent transaction reads and lacks the durable
`ClientRequestToken` contract, so it remains ineligible. A later distinct
protected-main runtime can now run exact candidate-to-bootstrap transaction and
persisted-format rollback correctness against the same Cosmos container.
Passing that correctness run does not promote either runtime or qualify the
profile.

## Performance and qualification boundary

The registered scenarios are
`dynamodb.TransactGetItems (10 items, single partition)` and
`dynamodb.TransactWriteItems (5 puts, single partition)` in
`tests/Aws2Azure.PerfTests/DynamoDb/DynamoDbPerfTests.cs`. They call
`AssertHealthy()` followed by `AssertNoRegression()` and currently use explicit
zero-threshold waivers pending reviewed measurements.

The Cosmos Linux emulator cannot execute stored procedures, so these scenarios
skip there and require real Azure. A dedicated production-shaped real-Azure
producer now runs the complete policy scenario set, including one exact
candidate-to-bootstrap rollback proof, and can emit sealed load evidence for a
later distinct runtime. The provisional serverless calibration uses concurrency
4 for five-write/ten-read transaction iterations. The initial concurrency-8 run
[`30208156245`](https://github.com/pedrosakuma/aws2azure/actions/runs/30208156245)
was non-qualifying after a surfaced `TransactGetItems` 429; it supplied no
throughput floor and is not promotion evidence. The first c=4 qualifying run
[`30211018893`](https://github.com/pedrosakuma/aws2azure/actions/runs/30211018893)
also remained non-qualifying: the representative window completed, but the
subsequent 72-item snapshot correctness probe self-throttled its unpaced
writer. The load producer now records that follow-up as
`transaction-read-after-write`: it commits and reads back 12 complete 72-item
versions sequentially with pacing and no retries. It verifies that each
transactional read returns exactly the version most recently committed, without
claiming backend concurrency from client-side timing. Coherent snapshot
isolation remains established separately by the real-Azure
`transaction-snapshot` correctness scenario, which runs a continuously
committing writer during repeated full-set reads. Any 429 still fails the run
and blocks evidence. Any future throughput/latency claim must cite that
real-Azure harness run; emulator results from other DynamoDB scenarios do not
qualify this profile. GA additionally requires three reviewed load runs,
correctness, rollback, and SLO evidence. The throughput floor remains
unresolved, no operational qualification artifact is committed, and the
bootstrap must not be treated as candidate, approved, GA, or production
promotion evidence.
