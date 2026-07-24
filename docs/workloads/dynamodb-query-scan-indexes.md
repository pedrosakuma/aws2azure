# DynamoDB Query, Scan, and secondary-index profile

This version 1 profile covers `Query`, `Scan`, and `DescribeTable` for base
tables plus the documented GSI and LSI subsets. It remains `candidate` until a
reviewed, production-shaped Cosmos load/SLO and rollback artifact is committed.

## Required configuration and topology

- Set `DynamoDb.EnableGlobalSecondaryIndexQueries=true` before using GSI Query
  or Scan. The flag remains default-off because GSI reads fan out across the
  base Cosmos container and read live base documents rather than a separately
  materialized eventually consistent index.
- Set `DynamoDb.EnableLocalSecondaryIndexNumericOrdering=true` when exact
  ordering or range filtering is required for high-precision numeric LSI sort
  keys. The write path always emits the derived order key for new writes, but
  the read flag stays default-off to avoid silently hiding older documents.
- Provision Cosmos RU/s for the expected Query/Scan fan-out. DynamoDB RCU/WCU,
  `ConsumedCapacity`, adaptive capacity, and per-index throughput are not
  reproduced.

## Certified Query contract

- Base-table Query supports hash equality and the documented `S`, `B`, and `N`
  range predicates with ascending/descending order and opaque continuation
  tokens.
- LSI Query is partition-scoped. GSI Query is cross-partition and uses a
  client-side ordered merge for composite indexes. Continuation ties can move
  under concurrent writes; do not mutate a result set while treating pagination
  as a stable snapshot.
- Filter and projection expressions use the operation gap document's supported
  subset. GSI projection boundaries are enforced even though the proxy reads a
  full base document.
- Secondary-index string ordering is certified only for data constrained to the
  documented Cosmos/DynamoDB collation-compatible range. Validate any non-ASCII
  or supplementary-plane key corpus before migration.
- Exact high-precision numeric GSI ordering is always derived-field based.
  Exact high-precision numeric LSI ordering requires the LSI flag above.
- Ordered secondary-index Query on a binary sort key is unsupported and fails
  with `ValidationException`; base-table binary sort-key ordering remains
  supported through the key codec.

## Pagination, Scan, and membership

GSI and LSI membership is sparse: every declared index key must be present.
Index Scan remains unordered and applies the declared projection. Base and
indexed pagination uses an aws2azure continuation sentinel inside
`LastEvaluatedKey`; callers must return it unchanged.

For a pushed `FilterExpression`, `Count` is post-filter. A complete unbounded
base Scan/Query recovers `ScannedCount`, but paginated or limited pushed-filter
pages can report a lower `ScannedCount` because Cosmos applies the pushed
predicate before the proxy sees the page boundary. Index reads have the
additional operation-specific `ScannedCount` caveats documented in the gap
files.

## Numeric-index backfill

Before enabling exact numeric LSI ordering on a table containing older items:

1. Stop or fence writers for the affected table.
2. Scan the base table and identify every item carrying the numeric index sort
   attribute.
3. Rewrite each item through `PutItem` or `UpdateItem`; this adds the hidden
   `_a2a$ord$<attribute>` field on every write path.
4. Compare the expected sparse-index membership with Query/Scan results.
5. Enable `DynamoDb.EnableLocalSecondaryIndexNumericOrdering=true` and repeat
   ascending, descending, range, and pagination checks before releasing traffic.

There is no automatic backfill. With exact ordering enabled, a pre-existing item
without the derived field is deliberately excluded rather than silently
mis-ordered.

## Performance and qualification boundary

The registered regression scenarios are `dynamodb.Query (pushable filter)`,
`dynamodb.Scan (pushable filter)`, `dynamodb.Query LSI numeric (ordered)`, and
`dynamodb.Query LSI numeric (selective)` in
`tests/Aws2Azure.PerfTests/DynamoDb/DynamoDbPerfTests.cs`. Normal `run-perf`
results are emulator-bound regression gates. The real-Azure LSI A/B workflow
measures Cosmos behavior, but neither source is a production workload SLO.

GA requires a separate production-shaped Cosmos campaign covering
representative indexed Query/Scan load, throttling, restart, and rollback. The
existing basic-CRUD qualification does not qualify this read profile.
