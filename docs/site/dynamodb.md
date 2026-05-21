# dynamodb

## BatchGetItem

- **Status:** 🟡 partial
- **Azure equivalent:** `Azure Cosmos DB (Core SQL API)`

### Sub-features

| Name | Status | Notes | Gap | Workaround |
|---|---|---|---|---|
| Multi-table fan-out | ✅ implemented | Each table's keys are fanned out via per-item Cosmos GETs. Bounded parallelism (16 concurrent calls) keeps a single request from saturating the proxy. |  |  |
| Per-item miss semantics | ✅ implemented | Missing items are omitted from `Responses` (matching DynamoDB), not surfaced as errors. |  |  |
| Throttling → UnprocessedKeys | ✅ implemented | Cosmos 429 on any individual GET drops that key into `UnprocessedKeys` so SDK retry loops re-issue only the throttled subset. The rest of the batch still returns 200. |  |  |
| ProjectionExpression (per table) | 🟡 partial | Top-level attribute names + `#alias` honoured. Nested paths (`a.b`, `a[0]`) rejected. |  |  |
| ExpressionAttributeNames (per table) | ✅ implemented |  |  |  |
| ConsistentRead (per table) | ✅ implemented | Sets `x-ms-consistency-level: Strong` on every Cosmos GET for that table; account-level consistency cap still applies. |  |  |
| 100-item-per-call cap | ✅ implemented | Requests over 100 keys (across all tables) rejected with ValidationException, matching the DynamoDB hard limit. |  |  |
| Duplicate-key rejection | ✅ implemented | Same (table, pk, id) repeated in a single call → ValidationException, matching DynamoDB. |  |  |
| Legacy AttributesToGet | ⛔ unsupported | Rejected with ValidationException — use ProjectionExpression. |  |  |
| ReturnConsumedCapacity | ⛔ unsupported | Silently ignored; response omits ConsumedCapacity. |  |  |

### Behaviour differences

- 16 MB total response size cap (DynamoDB) not enforced — bounded only by the underlying Cosmos response sizes.
- Hard error on any single item (non-429, non-404) fails the whole batch with a single error response — DynamoDB has the same all-or-nothing semantics for non-throttle failures.
- Cosmos 429 maps to `UnprocessedKeys` rather than `ProvisionedThroughputExceededException`; matches DDB SDK retry behaviour.
- Only validated against scripted Cosmos REST fakes; not yet exercised against real Azure Cosmos.

### References

- <https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_BatchGetItem.html>

## BatchWriteItem

- **Status:** 🟡 partial
- **Azure equivalent:** `Azure Cosmos DB (Core SQL API)`

### Sub-features

| Name | Status | Notes | Gap | Workaround |
|---|---|---|---|---|
| PutRequest fan-out | ✅ implemented | Each PutRequest issues a Cosmos POST with `x-ms-documentdb-is-upsert: true`, matching the existing PutItem fast-path. Item is stored verbatim under the `item` envelope for round-trip fidelity. |  |  |
| DeleteRequest fan-out | ✅ implemented | Each DeleteRequest routes to a Cosmos DELETE on the (pk, id) derived from the key. Deletes of missing items are successful no-ops — matches DynamoDB idempotency. |  |  |
| Bounded parallelism | ✅ implemented | Up to 10 concurrent Cosmos writes per batch (SemaphoreSlim-gated). |  |  |
| 25-item-per-call cap | ✅ implemented | Requests over 25 writes (across all tables) rejected with ValidationException, matching the DynamoDB hard limit. |  |  |
| Item shape validation (Put) | ✅ implemented | Every attribute in PutRequest.Item must be a single-property typed AttributeValue (same validator as PutItem). Malformed entries rejected with ValidationException before any Cosmos write. |  |  |
| Duplicate-key rejection | ✅ implemented | Two writes targeting the same (table, pk, id) in a single call are rejected with ValidationException — matches DynamoDB. |  |  |
| Throttling → UnprocessedItems | ✅ implemented | Cosmos 429 on any individual write surfaces the original PutRequest/DeleteRequest envelope in `UnprocessedItems`, preserving ordering within the table. Hard errors (5xx, 4xx other than 429/404) fail the whole batch. |  |  |
| ReturnConsumedCapacity / ReturnItemCollectionMetrics | ⛔ unsupported | Silently ignored; responses omit ConsumedCapacity and ItemCollectionMetrics. |  |  |

### Behaviour differences

- 16 MB request body cap (DynamoDB) not enforced — bounded only by Kestrel limits.
- Per-item 400 KB cap not enforced — bounded only by Cosmos document size limits.
- Cosmos 429 maps to `UnprocessedItems` rather than `ProvisionedThroughputExceededException`; matches DDB SDK retry behaviour.
- Order is preserved within a table when echoing into `UnprocessedItems`, but Cosmos calls execute in parallel — no guarantee that writes within a table commit in the order they were submitted.
- Only validated against scripted Cosmos REST fakes; not yet exercised against real Azure Cosmos.

### References

- <https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_BatchWriteItem.html>

## CreateTable

- **Status:** ✅ implemented
- **Azure equivalent:** `Azure Cosmos DB (Core SQL API) — POST /dbs/{db}/colls`

### Sub-features

| Name | Status | Notes | Gap | Workaround |
|---|---|---|---|---|
| HASH key | ✅ implemented |  |  |  |
| HASH + RANGE composite key | ✅ implemented |  |  |  |
| PAY_PER_REQUEST + PROVISIONED billing mode (informational) | ✅ implemented |  |  |  |
| AttributeDefinitions round-trip via sidecar metadata | ✅ implemented |  |  |  |
| GlobalSecondaryIndexes | ⛔ unsupported |  |  |  |
| LocalSecondaryIndexes | ⛔ unsupported |  |  |  |
| StreamSpecification | ⛔ unsupported |  |  |  |
| SSESpecification | ⛔ unsupported |  |  |  |
| Tags | ⛔ unsupported |  |  |  |

### Behaviour differences

- Cosmos containers use a fixed /pk partition path. Composite tables synthesise pk = '<HASH>#<RANGE>'.
- ProvisionedThroughput / BillingMode values are accepted but not enforced; throughput is governed by the Cosmos account/database, not per-table.
- TableStatus is always returned as ACTIVE since Cosmos container creation is synchronous.
- On metadata-sidecar persist failure the container is best-effort deleted to avoid orphan containers.
- Tested against scripted Cosmos handlers in unit tests; not yet exercised against real Azure Cosmos DB.

### References

- <https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_CreateTable.html>
- <https://learn.microsoft.com/rest/api/cosmos-db/create-a-collection>

## DeleteItem

- **Status:** 🟡 partial
- **Azure equivalent:** `Azure Cosmos DB (Core SQL API)`

### Sub-features

| Name | Status | Notes | Gap | Workaround |
|---|---|---|---|---|
| HASH-only key tables | ✅ implemented |  |  |  |
| HASH+RANGE composite key tables | ✅ implemented |  |  |  |
| Idempotent delete (missing item returns success) | ✅ implemented | Cosmos 404 → DynamoDB 200 empty, matching DynamoDB semantics. |  |  |
| ConditionExpression / Expected / ConditionalOperator | ✅ implemented | Conditional path performs GET → evaluate → DELETE(If-Match) with retry on 412/Conflict/404. If the condition evaluates true against a missing item, the operation returns success as a no-op. Failure returns HTTP 400 ConditionalCheckFailedException with optional Item when ReturnValuesOnConditionCheckFailure=ALL_OLD. |  |  |
| ExpressionAttributeNames / ExpressionAttributeValues | ⛔ unsupported |  |  |  |
| ReturnValues | 🟡 partial | Only NONE accepted; ALL_OLD rejected with ValidationException. |  |  |
| ReturnConsumedCapacity / ReturnItemCollectionMetrics | ⛔ unsupported |  |  |  |

### Behaviour differences

- Missing container (table deleted mid-op) is distinguished from missing item via Cosmos `x-ms-substatus: 1003` and surfaces as ResourceNotFoundException; missing items remain idempotent successes.
- Key values containing `/`, `\`, `?`, `#`, empty strings, or values longer than 255 chars are rejected with ValidationException.
- Cosmos 429 surfaced as DynamoDB ProvisionedThroughputExceededException — including 429 on metadata read.
- Only validated against scripted Cosmos REST + emulator; not yet exercised against real Azure Cosmos.

### References

- <https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_DeleteItem.html>

## DeleteTable

- **Status:** ✅ implemented
- **Azure equivalent:** `Azure Cosmos DB (Core SQL API) — DELETE /dbs/{db}/colls/{name}`

### Sub-features

| Name | Status | Notes | Gap | Workaround |
|---|---|---|---|---|
| Synchronous delete | ✅ implemented |  |  |  |
| TableDescription echoed (key schema, attrs) via sidecar metadata | ✅ implemented |  |  |  |

### Behaviour differences

- DynamoDB DeleteTable is asynchronous (returns DELETING). The proxy returns the same DELETING status for SDK parity even though the Cosmos delete is synchronous.
- On a non-existent table the proxy returns ResourceNotFoundException.
- Tested against scripted Cosmos handlers; not yet exercised against real Azure.

### References

- <https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_DeleteTable.html>
- <https://learn.microsoft.com/rest/api/cosmos-db/delete-a-collection>

## DescribeTable

- **Status:** ✅ implemented
- **Azure equivalent:** `Azure Cosmos DB (Core SQL API) — GET /dbs/{db}/colls/{name} + sidecar metadata`

### Sub-features

| Name | Status | Notes | Gap | Workaround |
|---|---|---|---|---|
| AttributeDefinitions / KeySchema round-trip | ✅ implemented |  |  |  |
| BillingModeSummary echo | ✅ implemented |  |  |  |
| TableArn synthesis (azure-region pseudo-arn) | ✅ implemented |  |  |  |
| ItemCount / TableSizeBytes (live metrics) | ⛔ unsupported |  |  |  |
| GSI/LSI description | ⛔ unsupported |  |  |  |

### Behaviour differences

- ItemCount and TableSizeBytes default to 0; populating them requires either Cosmos partition-key statistics or a stored aggregate, deferred to a later slice.
- TableArn is synthetic (region 'azure', account '000000000000'); real AWS arns carry the region + account id which are not meaningful in this deployment.
- Tables created out-of-band (no sidecar metadata) still describe but with empty attribute/key arrays.

### References

- <https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_DescribeTable.html>
- <https://learn.microsoft.com/rest/api/cosmos-db/get-a-collection>

## DescribeTimeToLive

- **Status:** ⚪ stub
- **Azure equivalent:** `Azure Cosmos DB container `defaultTtl` / per-item `ttl``

### Sub-features

| Name | Status | Notes | Gap | Workaround |
|---|---|---|---|---|
| Returns DISABLED | ✅ implemented | Returns `{TimeToLiveDescription: {TimeToLiveStatus: "DISABLED"}}` for every table without consulting Cosmos. SDK callers that probe TTL on every connection get a clean shape instead of a 501. |  |  |

### Behaviour differences

- Always reports DISABLED — even if a Cosmos container has `defaultTtl` configured out-of-band, this proxy will not surface that state.
- Pairs with `UpdateTimeToLive` which is currently `unsupported`; once item-level TTL translation lands, this op will be promoted to `partial`.

### References

- <https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_DescribeTimeToLive.html>

## GetItem

- **Status:** 🟡 partial
- **Azure equivalent:** `Azure Cosmos DB (Core SQL API)`

### Sub-features

| Name | Status | Notes | Gap | Workaround |
|---|---|---|---|---|
| HASH-only key tables | ✅ implemented |  |  |  |
| HASH+RANGE composite key tables | ✅ implemented |  |  |  |
| Full wire-form round-trip on response Item | ✅ implemented |  |  |  |
| ConsistentRead | 🟡 partial | Mapped to Cosmos `x-ms-consistency-level: Strong` request header. Honoured only when the account's max consistency permits Strong; Session/weaker accounts silently downgrade. |  |  |
| ProjectionExpression / AttributesToGet | ⛔ unsupported | Rejected with ValidationException pending expression parser slice (#12). |  |  |
| ExpressionAttributeNames | ⛔ unsupported |  |  |  |
| ReturnConsumedCapacity | ⛔ unsupported |  |  |  |

### Behaviour differences

- Missing item yields 200 with no `Item` field (matches DynamoDB).
- Missing container (table deleted mid-op) is distinguished from missing item via Cosmos `x-ms-substatus: 1003` and surfaces as ResourceNotFoundException.
- Key values containing `/`, `\`, `?`, `#`, empty strings, or values longer than 255 chars are rejected with ValidationException.
- ConsistentRead effectiveness is account-dependent; document divergence per deployment.
- Cosmos 429 on metadata read surfaces as ProvisionedThroughputExceededException (not a fake ResourceNotFoundException).
- Only validated against scripted Cosmos REST + emulator; not yet exercised against real Azure Cosmos.

### References

- <https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_GetItem.html>

## ListTables

- **Status:** ✅ implemented
- **Azure equivalent:** `Azure Cosmos DB (Core SQL API) — GET /dbs/{db}/colls`

### Sub-features

| Name | Status | Notes | Gap | Workaround |
|---|---|---|---|---|
| Limit (1..100) | ✅ implemented |  |  |  |
| ExclusiveStartTableName cursor | ✅ implemented |  |  |  |
| LastEvaluatedTableName pagination | ✅ implemented |  |  |  |

### Behaviour differences

- Container names are sorted ordinally (case-sensitive). DynamoDB pagination is also ordinal so the cursor semantics match.
- All containers in the configured database are surfaced, including sidecar-less ones. Operators using a shared database for non-DynamoDB workloads will see those container ids too.
- Pagination is server-side: the proxy fetches all containers once and slices in-memory. For databases with thousands of containers this should be split across Cosmos result pages — tracked as a follow-up.

### References

- <https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_ListTables.html>
- <https://learn.microsoft.com/rest/api/cosmos-db/list-collections>

## ListTagsOfResource

- **Status:** ⚪ stub
- **Azure equivalent:** `Azure Cosmos DB account/resource tags (control plane)`

### Sub-features

| Name | Status | Notes | Gap | Workaround |
|---|---|---|---|---|
| Returns empty tag list | ✅ implemented | Returns `{Tags: []}` after validating ResourceArn. No pagination because there is nothing to page over. |  |  |

### Behaviour differences

- Always returns an empty tag list, even immediately after a `TagResource` call (tags are not persisted by the proxy).

### References

- <https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_ListTagsOfResource.html>

## PutItem

- **Status:** 🟡 partial
- **Azure equivalent:** `Azure Cosmos DB (Core SQL API)`

### Sub-features

| Name | Status | Notes | Gap | Workaround |
|---|---|---|---|---|
| HASH-only key tables | ✅ implemented |  |  |  |
| HASH+RANGE composite key tables | ✅ implemented |  |  |  |
| Full DynamoDB wire-form round-trip (S/N/B/BOOL/NULL/M/L/SS/NS/BS) | ✅ implemented | Item stored verbatim under Cosmos doc `item` envelope; numeric precision (N) preserved via raw JSON pass-through. |  |  |
| ConditionExpression / Expected / ConditionalOperator | ✅ implemented | Conditional path performs GET → evaluate → PUT(If-Match) or POST(If-None-Match: *) with up to 4 retries on Cosmos 412/409. Failure returns HTTP 400 ConditionalCheckFailedException with optional Item when ReturnValuesOnConditionCheckFailure=ALL_OLD. attribute_not_exists(pk) is the standard idiom for first-time create. |  |  |
| ExpressionAttributeNames / ExpressionAttributeValues | ⛔ unsupported |  |  |  |
| ReturnValues | 🟡 partial | Only NONE accepted; ALL_OLD/UPDATED_* rejected with ValidationException. |  |  |
| ReturnConsumedCapacity / ReturnItemCollectionMetrics | ⛔ unsupported | Silently ignored; response omits ConsumedCapacity / ItemCollectionMetrics. |  |  |

### Behaviour differences

- Item is persisted under a Cosmos envelope `{id, pk, _a2a:"item", item:{...}}`; the raw DynamoDB wire form lives under `item`.
- Sentinel id `__aws2azure_table_meta__` is reserved for the table-metadata sidecar and rejected at the API surface.
- Key values containing `/`, `\`, `?`, `#`, empty strings, or values longer than 255 chars are rejected with ValidationException pending an encoding scheme.
- Cosmos 429 (throttled) is surfaced to clients as DynamoDB ProvisionedThroughputExceededException — including 429 on the sidecar metadata read.
- Only validated against scripted Cosmos REST + emulator; not yet exercised against real Azure Cosmos.

### References

- <https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_PutItem.html>

## Query

- **Status:** 🟡 partial
- **Azure equivalent:** `Azure Cosmos DB (Core SQL API)`

### Sub-features

| Name | Status | Notes | Gap | Workaround |
|---|---|---|---|---|
| KeyConditionExpression on HASH-only tables | ✅ implemented |  |  |  |
| KeyConditionExpression on HASH+RANGE tables (= / < / <= / > / >= / BETWEEN / begins_with) | ✅ implemented | Translated to a partition-scoped Cosmos SQL query against `c.pk = <hash>` with a predicate on `c.id` (which holds the formatted RANGE value). |  |  |
| FilterExpression | ✅ implemented | Evaluated in-process after the Cosmos page returns, so ScannedCount reflects pre-filter rows and Count reflects post-filter rows — matching DynamoDB semantics. |  |  |
| ProjectionExpression | 🟡 partial | Top-level attributes and `#alias` references are honoured. Nested paths (`a.b`, `a[0]`) are not yet supported and are rejected with ValidationException. |  |  |
| ExpressionAttributeNames / ExpressionAttributeValues | ✅ implemented |  |  |  |
| Limit | ✅ implemented |  |  |  |
| ExclusiveStartKey / LastEvaluatedKey | ✅ implemented | Pagination round-trips the Cosmos `x-ms-continuation` token inside a sentinel attribute `__a2a_continuation` (typed-string `S`). Most AWS SDKs treat LastEvaluatedKey as opaque and pass it back verbatim, which is what the proxy requires. |  |  |
| ScanIndexForward | ✅ implemented | Maps to `ORDER BY c.id ASC\|DESC`; only emitted for composite-key tables (hash-only Query returns at most one item). |  |  |
| ConsistentRead | ✅ implemented | Forwards `x-ms-consistency-level: Strong` for the Cosmos query when true; account-level consistency cap still applies. |  |  |
| Select | 🟡 partial | ALL_ATTRIBUTES (default), SPECIFIC_ATTRIBUTES, and COUNT supported. ALL_PROJECTED_ATTRIBUTES requires IndexName and is rejected. |  |  |
| IndexName (GSI / LSI) | ⛔ unsupported | Querying secondary indexes is not yet supported; requests carrying IndexName are rejected with ValidationException. |  |  |
| Legacy KeyConditions / QueryFilter / ConditionalOperator | ⛔ unsupported | Legacy v1 parameters are rejected loudly with ValidationException — use KeyConditionExpression / FilterExpression. |  |  |
| ReturnConsumedCapacity | ⛔ unsupported | Silently ignored; response omits ConsumedCapacity. |  |  |

### Behaviour differences

- Sort-key ordering is the lexical order of the Cosmos document id (which is the formatted RANGE scalar string). Numeric sort keys are therefore not numerically ordered — zero-pad them or use a string sort key if order matters.
- Every Query is partition-scoped — there is no cross-partition fan-out — matching DynamoDB's single-partition guarantee.
- Cosmos 429 (throttled) is surfaced as DynamoDB ProvisionedThroughputExceededException.
- Only validated against scripted Cosmos REST fakes; not yet exercised against real Azure Cosmos.

### References

- <https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_Query.html>

## Scan

- **Status:** 🟡 partial
- **Azure equivalent:** `Azure Cosmos DB (Core SQL API)`

### Sub-features

| Name | Status | Notes | Gap | Workaround |
|---|---|---|---|---|
| Full-table scan | ✅ implemented | Translated to a cross-partition Cosmos SQL query (`x-ms-documentdb-query-enablecrosspartition: true`). Every Scan is an O(N) walk of the container — expensive in RU. |  |  |
| FilterExpression | ✅ implemented | Evaluated in-process after each Cosmos page returns, so ScannedCount reflects pre-filter rows and Count reflects post-filter rows — matching DynamoDB. |  |  |
| ProjectionExpression | 🟡 partial | Top-level attributes and `#alias` references are honoured. Nested paths (`a.b`, `a[0]`) are not yet supported and are rejected with ValidationException. |  |  |
| ExpressionAttributeNames / ExpressionAttributeValues | ✅ implemented |  |  |  |
| Limit | ✅ implemented | Caps the *scanned* (pre-filter) row count, matching DynamoDB. pageSize is sized to the remaining evaluation budget so the per-page continuation never skips rows. |  |  |
| ExclusiveStartKey / LastEvaluatedKey | ✅ implemented | Pagination round-trips the Cosmos `x-ms-continuation` token inside a sentinel attribute `__a2a_continuation` (typed-string `S`). Most AWS SDKs treat LastEvaluatedKey as opaque and pass it back verbatim. |  |  |
| ConsistentRead | ✅ implemented | Forwards `x-ms-consistency-level: Strong` for the Cosmos query when true; account-level consistency cap still applies. |  |  |
| Select | 🟡 partial | ALL_ATTRIBUTES (default), SPECIFIC_ATTRIBUTES, and COUNT supported. ALL_PROJECTED_ATTRIBUTES requires IndexName and is rejected. |  |  |
| IndexName (GSI / LSI) | ⛔ unsupported | Scanning secondary indexes is not yet supported; requests carrying IndexName are rejected with ValidationException. |  |  |
| Parallel scan (Segment / TotalSegments) | ⛔ unsupported | Rejected with ValidationException. Cosmos cross-partition queries fan out internally; explicit per-segment parallelism is deferred to a later slice. |  |  |
| Legacy ScanFilter / ConditionalOperator / AttributesToGet | ⛔ unsupported | Legacy v1 parameters are rejected loudly with ValidationException — use FilterExpression / ProjectionExpression. |  |  |
| ReturnConsumedCapacity | ⛔ unsupported | Silently ignored; response omits ConsumedCapacity. |  |  |

### Behaviour differences

- Scan order is **not** stable. Cosmos cross-partition query does not guarantee a deterministic walk across partitions; DynamoDB Scan is also unordered, but specific item ordering across pages may differ.
- Cosmos 429 (throttled) is surfaced as DynamoDB ProvisionedThroughputExceededException — expect this often on large scans.
- RU cost is significant for cross-partition scans; the proxy does no rate-limiting beyond what Cosmos imposes.
- Only validated against scripted Cosmos REST fakes; not yet exercised against real Azure Cosmos.

### References

- <https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_Scan.html>

## TagResource

- **Status:** ⚪ stub
- **Azure equivalent:** `Azure Cosmos DB account/resource tags (control plane)`

### Sub-features

| Name | Status | Notes | Gap | Workaround |
|---|---|---|---|---|
| Accept & discard tags | ✅ implemented | Returns an empty 200 after validating ResourceArn + non-empty Tags. Tags are not persisted anywhere and have no effect on Azure billing or routing. |  |  |

### Behaviour differences

- AWS SDK callers that tag tables on creation as a bookkeeping side-effect work; callers that rely on tag-based access control or cost allocation do not.
- Round-trip with `ListTagsOfResource` is not supported — listing always returns an empty array.

### References

- <https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_TagResource.html>

## TransactGetItems

- **Status:** 🟡 partial
- **Azure equivalent:** `Azure Cosmos DB (Core SQL API)`

### Sub-features

| Name | Status | Notes | Gap | Workaround |
|---|---|---|---|---|
| Per-item strong-consistency reads | ✅ implemented | Each Get fans out to a Cosmos GET with `x-ms-consistency-level: Strong`. Bounded parallelism (16) keeps the response sub-second on small batches. |  |  |
| 100-item-per-call cap | ✅ implemented | Requests over 100 items rejected with ValidationException. |  |  |
| Positional Responses alignment | ✅ implemented | Missing items emit an empty `{}` entry to preserve index alignment with TransactItems (matches DynamoDB). |  |  |
| ProjectionExpression / ExpressionAttributeNames (per item) | 🟡 partial | Top-level attribute names + `#alias` honoured. Nested paths rejected. |  |  |
| TransactionCanceledException on Cosmos error | ✅ implemented | Any non-2xx, non-404 from a fan-out call cancels the transaction with `TransactionCanceledException`. `CancellationReasons` is aligned positionally — `None` for successful items, the Cosmos-derived AWS code (e.g. `ProvisionedThroughputExceededException`, `InternalServerError`) for failed ones. |  |  |
| ReturnConsumedCapacity | ⛔ unsupported | Silently ignored; response omits ConsumedCapacity. |  |  |

### Behaviour differences

- Not a true cross-container ACID read — each fan-out call sees Cosmos' latest committed value independently. For items in the same logical partition this is functionally equivalent to DynamoDB; cross-partition or cross-container reads can in theory observe writes that committed mid-fan-out (DynamoDB internally serializes the entire transaction).
- Only validated against scripted Cosmos REST fakes; not yet exercised against real Azure Cosmos.

### References

- <https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_TransactGetItems.html>

## TransactWriteItems

- **Status:** ⛔ unsupported
- **Azure equivalent:** `Azure Cosmos DB (Core SQL API) — no cross-container ACID`

### Sub-features

| Name | Status | Notes | Gap | Workaround |
|---|---|---|---|---|
| ACID writes across items | ⛔ unsupported | Azure Cosmos DB only offers transactional batches within a single logical partition of a single container. DynamoDB TransactWriteItems supports up to 100 writes across multiple tables with full ACID guarantees — there is no faithful mapping. |  |  |
| ConditionCheck / ConditionExpression | ⛔ unsupported |  |  |  |

### Behaviour differences

- Every TransactWriteItems call returns `TransactionCanceledException` with an explanatory message. Callers must fall back to `BatchWriteItem` (no atomicity) or per-item `PutItem` / `UpdateItem` / `DeleteItem` with their own application-level coordination.

### References

- <https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_TransactWriteItems.html>

## UntagResource

- **Status:** ⚪ stub
- **Azure equivalent:** `Azure Cosmos DB account/resource tags (control plane)`

### Sub-features

| Name | Status | Notes | Gap | Workaround |
|---|---|---|---|---|
| Accept & no-op | ✅ implemented | Returns an empty 200 after validating ResourceArn + non-empty TagKeys. There is no persisted state to untag. |  |  |

### Behaviour differences

- Mirrors the `TagResource` stub: tags are never persisted, so removal is unconditionally a no-op.

### References

- <https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_UntagResource.html>

## UpdateItem

- **Status:** 🟡 partial
- **Azure equivalent:** `Azure Cosmos DB (Core SQL API)`

### Sub-features

| Name | Status | Notes | Gap | Workaround |
|---|---|---|---|---|
| UpdateExpression grammar (SET / REMOVE / ADD / DELETE) | ✅ implemented | Hand-rolled lexer + parser shared with the future Condition/Filter slice. |  |  |
| SET arithmetic (`a = a + :i`, `a = :x - :y`) | ✅ implemented | Decimal arithmetic preserves up to 28-29 significant digits; DynamoDB allows 38. Overflow surfaces as ValidationException. |  |  |
| SET functions `if_not_exists(path, fallback)` and `list_append(l1, l2)` | ✅ implemented |  |  |  |
| SET on nested paths (`addr.zip`, `items[0].name`) | ✅ implemented | Parent path must already exist as a map/list, matching DynamoDB. Creating a deeply-nested fresh structure requires top-level SET. |  |  |
| REMOVE on nested paths and missing attributes | ✅ implemented | REMOVE on a missing path is a no-op. |  |  |
| ADD on numeric attribute (create-if-missing + addition) | ✅ implemented |  |  |  |
| ADD / DELETE on string/number/binary sets (union / subtract) | ✅ implemented | Empty result set causes the attribute to be removed entirely, matching DynamoDB. |  |  |
| AttributeUpdates (legacy) PUT / DELETE / ADD | ✅ implemented | Normalised internally into the same UpdateExpression AST. |  |  |
| ExpressionAttributeNames / ExpressionAttributeValues (`#name`, `:value`) | ✅ implemented |  |  |  |
| Path overlap detection | ✅ implemented | Two paths in the same expression where one is a prefix of the other are rejected with ValidationException. |  |  |
| ReturnValues (NONE / ALL_OLD / UPDATED_OLD / ALL_NEW / UPDATED_NEW) | ✅ implemented | UPDATED_OLD/UPDATED_NEW project only the top-level attributes touched by the expression, matching AWS. |  |  |
| Create-if-missing (upsert) semantics | ✅ implemented | Atomic create with `If-None-Match: *` when the target item does not exist; concurrent create races surface as Cosmos 409 and are replayed by the optimistic-retry loop against the winner's state. |  |  |
| ConditionExpression / Expected / ConditionalOperator | ✅ implemented | Modern ConditionExpression and legacy Expected + ConditionalOperator both supported; mutual exclusion enforced with ValidationException. Evaluator covers comparisons, AND/OR/NOT, BETWEEN, IN, attribute_exists/not_exists/type, begins_with, contains, size(). Failure returns HTTP 400 ConditionalCheckFailedException; ReturnValuesOnConditionCheckFailure=ALL_OLD includes the prior item. |  |  |
| ReturnConsumedCapacity / ReturnItemCollectionMetrics | ⛔ unsupported | Silently ignored; response omits ConsumedCapacity / ItemCollectionMetrics. |  |  |

### Behaviour differences

- Atomicity is implemented as a GET → modify → PUT(If-Match) (or atomic-create with If-None-Match) loop with up to 4 retries on Cosmos 412/409. Sustained contention surfaces as InternalServerError after the retry budget is exhausted.
- Numeric arithmetic is performed with System.Decimal (28-29 significant digits) rather than DynamoDB's 38-digit precision. Operands exceeding the proxy's precision are rejected up front with ValidationException to avoid silent rounding; overflow also throws ValidationException.
- Key attributes referenced by the request are always reinforced into the resulting item — a REMOVE targeting the partition or sort key never deletes them in the stored doc.
- Cosmos 429 (throttled) is surfaced to clients as DynamoDB ProvisionedThroughputExceededException.
- Only validated against scripted Cosmos REST in unit tests; not yet exercised against real Azure Cosmos.

### References

- <https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_UpdateItem.html>
- <https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/Expressions.UpdateExpressions.html>

## UpdateTimeToLive

- **Status:** ⛔ unsupported
- **Azure equivalent:** `Azure Cosmos DB container `defaultTtl` / per-item `ttl``

### Sub-features

| Name | Status | Notes | Gap | Workaround |
|---|---|---|---|---|
| TTL enable/disable | ⛔ unsupported | Honouring DynamoDB TTL semantics requires translating the named attribute's epoch-seconds value into Cosmos' per-item `ttl` field on every PutItem / UpdateItem write. That translation is not yet implemented; accepting the call without translating would silently break the user's expiration contract. |  |  |

### Behaviour differences

- Returns `ValidationException` with an explanatory message. Operators who need TTL on Azure should configure Cosmos container `defaultTtl` directly out-of-band and not rely on this API.

### References

- <https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_UpdateTimeToLive.html>

