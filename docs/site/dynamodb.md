# dynamodb

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

- **Status:** ⚪ stub
- **Azure equivalent:** `Azure Cosmos DB (Core SQL API)`

### Behaviour differences

- DynamoDB JSON type system mapped onto Cosmos JSON; consistency model differs

### References

- <https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_Query.html>

## Scan

- **Status:** ⚪ stub
- **Azure equivalent:** `Azure Cosmos DB (Core SQL API)`

### Behaviour differences

- DynamoDB JSON type system mapped onto Cosmos JSON; consistency model differs

### References

- <https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_Scan.html>

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

