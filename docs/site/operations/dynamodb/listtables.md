# dynamodb / ListTables {#operation-dynamodb-listtables}

[← dynamodb operation index](../../dynamodb.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:dynamodb:listtables`
- **Status:** ✅ implemented
- **Azure equivalent:** `Azure Cosmos DB (Core SQL API) — GET /dbs/{db}/colls`
- **Real-Azure verified:** ✅ 2026-08-20 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/00000000000) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/00000000000)

## Sub-features

### Limit (1..100) {#sub-feature-limit--1100}

- **Capability ID:** `sub-feature:dynamodb:listtables:limit--1100`
- **Status:** ✅ implemented

### ExclusiveStartTableName cursor {#sub-feature-exclusivestarttablename-cursor}

- **Capability ID:** `sub-feature:dynamodb:listtables:exclusivestarttablename-cursor`
- **Status:** ✅ implemented

### LastEvaluatedTableName pagination {#sub-feature-lastevaluatedtablename-pagination}

- **Capability ID:** `sub-feature:dynamodb:listtables:lastevaluatedtablename-pagination`
- **Status:** ✅ implemented

## Behaviour differences

- Container names are sorted ordinally (case-sensitive). DynamoDB pagination is also ordinal so the cursor semantics match.
- All containers in the configured database are surfaced, including sidecar-less ones. Operators using a shared database for non-DynamoDB workloads will see those container ids too.
- Pagination is server-side: the proxy fetches all containers once and slices in-memory. For databases with thousands of containers this should be split across Cosmos result pages — tracked as a follow-up.
- Real-Azure verification covers Limit, ExclusiveStartTableName, and LastEvaluatedTableName pagination against a live Cosmos DB database shared with other containers (see DynamoDbRealAzureConformanceTests.ListTables_paginates_with_limit_and_exclusive_start_over_real_cosmos).

## References

- <https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_ListTables.html>
- <https://learn.microsoft.com/rest/api/cosmos-db/list-collections>

