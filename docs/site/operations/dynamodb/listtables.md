# dynamodb / ListTables {#operation-dynamodb-listtables}

[← dynamodb operation index](../../dynamodb.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:dynamodb:listtables`
- **Status:** ✅ implemented
- **Azure equivalent:** `Azure Cosmos DB (Core SQL API) — GET /dbs/{db}/colls`
- **Real-Azure verified:** ✅ 2026-09-02 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/33638504498) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/33638504498)

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
- LastEvaluatedTableName is the last container name in the returned page and thus differs between two independently seeded catalogs; the offline Tier-3 diff compares real-AWS and real-Azure captures whose ephemeral table sets do not share names, so the echoed cursor value cannot match byte-for-byte. [conformance:list-tables-pagination::field-value:LastEvaluatedTableName]
- Real-Azure verification covers Limit, ExclusiveStartTableName, and LastEvaluatedTableName pagination against a live Cosmos DB database shared with other containers (see DynamoDbRealAzureConformanceTests.ListTables_paginates_with_limit_and_exclusive_start_over_real_cosmos).
- Invalid master-key credentials surface as a clean, non-retryable DynamoDB AccessDeniedException (HTTP 400), matching real AWS DynamoDB behavior for an unauthorized access key. Previously (issue #838, 2026-08-21, ephemeral serverless Cosmos DB account, Strong consistency) Cosmos DB's Gateway answered a syntactically valid but cryptographically wrong master-key Authorization header on GET /dbs/{db}/colls with a plain HTTP 500 instead of 401/403, which the proxy faithfully relayed as a retryable InternalServerError/500. Re-confirmed against real Azure on 2026-09-02 (https://github.com/pedrosakuma/aws2azure/actions/runs/33638504498) that Cosmos DB now returns the 401/403 the REST API reference always implied, which CosmosOpsShared.MapCosmosStatus already mapped to AccessDeniedException — no proxy code change was needed. See RealAzureInvalidCredentialConformanceTests.DynamoDb_invalid_primary_key_returns_native_non_retryable_error.

## References

- <https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_ListTables.html>
- <https://learn.microsoft.com/rest/api/cosmos-db/list-collections>

