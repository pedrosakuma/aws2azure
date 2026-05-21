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

- **Status:** ⚪ stub
- **Azure equivalent:** `Azure Cosmos DB (Core SQL API)`

### Behaviour differences

- DynamoDB JSON type system mapped onto Cosmos JSON; consistency model differs

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

- **Status:** ⚪ stub
- **Azure equivalent:** `Azure Cosmos DB (Core SQL API)`

### Behaviour differences

- DynamoDB JSON type system mapped onto Cosmos JSON; consistency model differs

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

- **Status:** ⚪ stub
- **Azure equivalent:** `Azure Cosmos DB (Core SQL API)`

### Behaviour differences

- DynamoDB JSON type system mapped onto Cosmos JSON; consistency model differs

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

- **Status:** ⚪ stub
- **Azure equivalent:** `Azure Cosmos DB (Core SQL API)`

### Behaviour differences

- DynamoDB JSON type system mapped onto Cosmos JSON; consistency model differs

### References

- <https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_UpdateItem.html>

