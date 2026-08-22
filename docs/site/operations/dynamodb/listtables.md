# dynamodb / ListTables {#operation-dynamodb-listtables}

[← dynamodb operation index](../../dynamodb.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:dynamodb:listtables`
- **Status:** ✅ implemented
- **Azure equivalent:** `Azure Cosmos DB (Core SQL API) — GET /dbs/{db}/colls`
- **Real-Azure verified:** ✅ 2026-08-20 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/32359911854) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/32359911854)

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
- Invalid master-key credentials do NOT surface as a clean, non-retryable AccessDeniedException the way real AWS DynamoDB does for an unauthorized access key. Confirmed against real Azure (issue #838, 2026-08-21, ephemeral serverless Cosmos DB account, Strong consistency): Cosmos DB's Gateway answers a syntactically valid but cryptographically wrong master-key Authorization header on GET /dbs/{db}/colls with a plain HTTP 500 (`{"code":"InternalServerError","message":"Unknown server error occurred when processing this request."}`, no x-ms-substatus) rather than 401/403. The proxy faithfully relays this as DynamoDB InternalServerError/500 (see CosmosOpsShared.MapCosmosStatus), which the AWS SDK treats as retryable — unlike real AWS, an invalid-credential ListTables call is retried once under MaxErrorRetry=1 before the client sees the exception. There is no signal in Cosmos's response distinguishing "bad key" from a genuine transient error here, so the proxy cannot special-case this into AccessDeniedException without brittle English-message matching. See RealAzureInvalidCredentialConformanceTests.DynamoDb_invalid_primary_key_surfaces_as_retryable_internal_server_error.

## References

- <https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_ListTables.html>
- <https://learn.microsoft.com/rest/api/cosmos-db/list-collections>

