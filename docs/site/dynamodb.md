# dynamodb

## BatchGetItem

- **Status:** 🟡 partial
- **Azure equivalent:** `Azure Cosmos DB (Core SQL API)`

### Sub-features

| Name | Status | Real-Azure | Notes | Gap | Workaround |
|---|---|---|---|---|---|
| Multi-table fan-out | ✅ implemented | — | Each table's keys are grouped by Cosmos partition key. Keys that share a partition are served by a single `SELECT * FROM c WHERE c.id IN (...)` query (one round-trip per partition); a lone key keeps the cheap `GET /docs/{id}` point read. Bounded parallelism (16 concurrent calls) keeps a single multi-partition request from saturating the proxy. |  |  |
| Single-partition batching | ✅ implemented | — | issue #185 — a BatchGetItem whose keys all share a partition (e.g. 25 sort keys under one HASH) issues one IN-list Cosmos query instead of N point reads, draining `x-ms-continuation` as needed. Roughly an order of magnitude fewer round-trips for the common single-partition shape. |  |  |
| Per-item miss semantics | ✅ implemented | — | Missing items are omitted from `Responses` (matching DynamoDB), not surfaced as errors. In the batched-query path a requested key whose document is absent from the partition is simply left out of the result set. |  |  |
| Throttling → UnprocessedKeys | ✅ implemented | — | A Cosmos 429 on a point read drops that key into `UnprocessedKeys`; a 429 on a batched single-partition query drops the whole partition's keys into `UnprocessedKeys`. Either way SDK retry loops re-issue only the throttled subset and the rest of the batch still returns 200. |  |  |
| ProjectionExpression (per table) | ✅ implemented | ✅ | Top-level attribute names, `#alias` references, and nested document paths (`a.b`, `a[0]`, `a.b[1]`) honoured. Projected maps keep only referenced members; projected lists compact to referenced indices (ascending); non-existent/type-mismatched paths omitted; overlapping paths rejected with ValidationException. |  |  |
| ExpressionAttributeNames (per table) | ✅ implemented | — |  |  |  |
| ConsistentRead (per table) | ✅ implemented | — | Sets `x-ms-consistency-level: Strong` on every Cosmos read (point read or batched query) for that table; account-level consistency cap still applies. Opt-in startup probe (`DynamoDb.ConsistencyCheck` = Warn/Required, #204) flags accounts that cannot honor Strong at boot. |  |  |
| 100-item-per-call cap | ✅ implemented | — | Requests over 100 keys (across all tables) rejected with ValidationException, matching the DynamoDB hard limit. |  |  |
| Duplicate-key rejection | ✅ implemented | — | Same (table, pk, id) repeated in a single call → ValidationException, matching DynamoDB. |  |  |
| Legacy AttributesToGet | ⛔ unsupported | — | Rejected with ValidationException — use ProjectionExpression. |  |  |
| ReturnConsumedCapacity | ⛔ unsupported | — | Silently ignored; response omits ConsumedCapacity. |  |  |

### Behaviour differences

- Cosmos storage-metadata system fields (`_rid`/`_self`/`_etag`/`_ts`/`_attachments`/`_lsn`/`_metadata`) are stripped from response items and never surface as DynamoDB attributes (#203). Caveat: a user attribute literally named identically is also stripped on read; the durable fix is attribute namespacing.
- Key attribute values (S/B) are hex-encoded into the internal Cosmos `id`/partition-key (S → hex(UTF-8 bytes), B → hex(raw bytes), N → order-preserving numeric digit string), accepting Cosmos-forbidden characters (`/`, `\`, `?`, `#`) and fixing B byte-ordering. Effective raw key limit ~127 bytes; over-limit keys are rejected with ValidationException. **On-disk-format breaking change** vs earlier builds. See PutItem for the full rationale.
- 16 MB total response size cap (DynamoDB) not enforced — bounded only by the underlying Cosmos response sizes.
- Multi-region Cosmos accounts honor configured `cosmos.preferredRegions` for read locality and client-side failover: BatchGetItem point reads and batched partition queries route to the first available readable preferred region, then remaining readable regions, then the configured account endpoint. Failover is implemented for regional 503/408 and transport failures; emulator coverage is unavailable because the Cosmos emulator is single-region.
- Hard error on any single item (non-429, non-404) fails the whole batch with a single error response — DynamoDB has the same all-or-nothing semantics for non-throttle failures.
- Cosmos 429 maps to `UnprocessedKeys` rather than `ProvisionedThroughputExceededException`; matches DDB SDK retry behaviour. For a single-partition batched query, a 429 throttles the keys not yet returned (a first-page 429 throttles the whole partition group; items already fetched on earlier continuation pages stay in `Responses`).
- Cosmos binary JSON response bodies are supported only when explicitly enabled with `DynamoDb.CosmosBinaryResponses=true`; the proxy sends `x-ms-cosmos-supported-serialization-formats: CosmosBinary` on point reads and partition-batched queries, decodes `0x80` CosmosBinary bodies back to JSON before the normal DynamoDB response transform, and falls back to the unchanged text path whenever Cosmos returns text. Emulator-unverified: the Cosmos DB Linux emulator used by CI does not emit CosmosBinary bodies.
- Singleton-group point reads (`GET /docs/{id}`) build the AttributeValue map straight off a CosmosBinary body via `CosmosBinaryReader` (no binary→text decode + JsonDocument DOM), falling back to decode-to-text on an unsupported marker; observable on `aws2azure_dynamodb_read_decode_path_total{op="batchget",path=binary|fallback|text}`. The partition-batched IN query page still decodes to text (binary-direct multi-doc walk is a later increment).
- Only validated against scripted Cosmos REST fakes; not yet exercised against real Azure Cosmos.

### References

- <https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_BatchGetItem.html>

## BatchWriteItem

- **Status:** 🟡 partial
- **Azure equivalent:** `Azure Cosmos DB (Core SQL API)`

### Sub-features

| Name | Status | Real-Azure | Notes | Gap | Workaround |
|---|---|---|---|---|---|
| PutRequest fan-out | ✅ implemented | — | Each PutRequest issues a Cosmos POST with `x-ms-documentdb-is-upsert: true`, matching the existing PutItem fast-path. Item attributes are stored flat on the Cosmos document (same shape as PutItem) for round-trip fidelity. |  |  |
| DeleteRequest fan-out | ✅ implemented | — | Each DeleteRequest routes to a Cosmos DELETE on the (pk, id) derived from the key. Deletes of missing items are successful no-ops — matches DynamoDB idempotency. |  |  |
| Bounded parallelism | ✅ implemented | — | Up to 10 concurrent Cosmos writes per batch (SemaphoreSlim-gated). |  |  |
| 25-item-per-call cap | ✅ implemented | — | Requests over 25 writes (across all tables) rejected with ValidationException, matching the DynamoDB hard limit. |  |  |
| Item shape validation (Put) | ✅ implemented | — | Every attribute in PutRequest.Item must be a single-property typed AttributeValue (same validator as PutItem). Malformed entries rejected with ValidationException before any Cosmos write. |  |  |
| Duplicate-key rejection | ✅ implemented | — | Two writes targeting the same (table, pk, id) in a single call are rejected with ValidationException — matches DynamoDB. |  |  |
| Throttling → UnprocessedItems | ✅ implemented | — | Cosmos 429 on any individual write surfaces the original PutRequest/DeleteRequest envelope in `UnprocessedItems`, preserving ordering within the table. Hard errors (5xx, 4xx other than 429/404) fail the whole batch. |  |  |
| ReturnConsumedCapacity / ReturnItemCollectionMetrics | ⛔ unsupported | — | Silently ignored; responses omit ConsumedCapacity and ItemCollectionMetrics. |  |  |

### Behaviour differences

- Key attribute values (S/B) are hex-encoded into the internal Cosmos `id`/partition-key (S → hex(UTF-8 bytes), B → hex(raw bytes), N → order-preserving numeric digit string), accepting Cosmos-forbidden characters (`/`, `\`, `?`, `#`) and fixing B byte-ordering. Effective raw key limit ~127 bytes; over-limit keys are rejected with ValidationException. **On-disk-format breaking change** vs earlier builds. See PutItem for the full rationale.
- 16 MB request body cap (DynamoDB) not enforced — bounded only by Kestrel limits.
- Per-item 400 KB cap not enforced — bounded only by Cosmos document size limits.
- Cosmos 429 maps to `UnprocessedItems` rather than `ProvisionedThroughputExceededException`; matches DDB SDK retry behaviour.
- Order is preserved within a table when echoing into `UnprocessedItems`, but Cosmos calls execute in parallel — no guarantee that writes within a table commit in the order they were submitted.
- Only validated against scripted Cosmos REST fakes; not yet exercised against real Azure Cosmos.
- Each Put unit's standalone document body is sent as CosmosBinary (the `0x80` format) when the opt-in `DynamoDb.CosmosBinaryRequests` is enabled (default off); each unit is its own `POST /docs` upsert, so the gateway auto-detects the marker (no negotiation header or special Content-Type). Delete units carry no body. The chosen format is observable on `aws2azure_dynamodb_write_body_total{format=binary|text}`. The Cosmos DB Linux emulator neither emits nor reliably accepts CosmosBinary, so the binary write path is validated against real Azure only — confirmed parsed + indexed by the nightly acceptance test.

### References

- <https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_BatchWriteItem.html>

## CreateTable

- **Status:** ✅ implemented
- **Azure equivalent:** `Azure Cosmos DB (Core SQL API) — POST /dbs/{db}/colls`

### Sub-features

| Name | Status | Real-Azure | Notes | Gap | Workaround |
|---|---|---|---|---|---|
| HASH key | ✅ implemented | — |  |  |  |
| HASH + RANGE composite key | ✅ implemented | — |  |  |  |
| PAY_PER_REQUEST + PROVISIONED billing mode (informational) | ✅ implemented | — |  |  |  |
| AttributeDefinitions round-trip via sidecar metadata | ✅ implemented | — |  |  |  |
| GlobalSecondaryIndexes (schema accepted + persisted) | 🟡 partial | — |  |  |  |
| LocalSecondaryIndexes (schema accepted + persisted) | 🟡 partial | — |  |  |  |
| StreamSpecification | ⛔ unsupported | — |  |  |  |
| SSESpecification | ⛔ unsupported | — |  |  |  |
| Tags | ⛔ unsupported | — |  |  |  |

### Behaviour differences

- Cosmos containers use a fixed /pk partition path. Composite tables synthesise pk = '<HASH>#<RANGE>'.
- ProvisionedThroughput / BillingMode values are accepted but not enforced; throughput is governed by the Cosmos account/database, not per-table.
- TableStatus is always returned as ACTIVE since Cosmos container creation is synchronous.
- On metadata-sidecar persist failure the container is best-effort deleted to avoid orphan containers.
- GSI/LSI schemas are validated (key arity, HASH/RANGE roles, LSI HASH must match the table HASH, required Projection with projection type + INCLUDE NonKeyAttributes rules and limits (<=20 per index, <=100 total, names <=255 chars), attribute-definition references, name uniqueness, service limits) and persisted into the sidecar metadata. Index Query/Scan execution lands in later slices; until then a table can be created with indexes but querying them still returns ValidationException.
- GSI/LSI ProvisionedThroughput on an index is accepted but not enforced, mirroring base-table throughput handling.
- Smoke-verified against the Cosmos DB Linux emulator (vNext preview) via Testcontainers; not yet exercised against real Azure Cosmos DB.

### References

- <https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_CreateTable.html>
- <https://learn.microsoft.com/rest/api/cosmos-db/create-a-collection>

## DeleteItem

- **Status:** 🟡 partial
- **Azure equivalent:** `Azure Cosmos DB (Core SQL API)`

### Sub-features

| Name | Status | Real-Azure | Notes | Gap | Workaround |
|---|---|---|---|---|---|
| HASH-only key tables | ✅ implemented | — |  |  |  |
| HASH+RANGE composite key tables | ✅ implemented | — |  |  |  |
| Idempotent delete (missing item returns success) | ✅ implemented | — | Cosmos 404 → DynamoDB 200 empty, matching DynamoDB semantics. |  |  |
| ConditionExpression / Expected / ConditionalOperator | ✅ implemented | — | Conditional path performs GET → evaluate → DELETE(If-Match) with retry on 412/Conflict/404. If the condition evaluates true against a missing item, the operation returns success as a no-op. Failure returns HTTP 400 ConditionalCheckFailedException with optional Item when ReturnValuesOnConditionCheckFailure=ALL_OLD. |  |  |
| ExpressionAttributeNames / ExpressionAttributeValues | ⛔ unsupported | — |  |  |  |
| ReturnValues | 🟡 partial | — | Only NONE accepted; ALL_OLD rejected with ValidationException. |  |  |
| ReturnConsumedCapacity / ReturnItemCollectionMetrics | ⛔ unsupported | — |  |  |  |

### Behaviour differences

- Cosmos storage-metadata system fields (`_rid`/`_self`/`_etag`/`_ts`/`_attachments`/`_lsn`/`_metadata`) are stripped from response items and never surface as DynamoDB attributes (#203). Caveat: a user attribute literally named identically is also stripped on read; the durable fix is attribute namespacing.
- Missing container (table deleted mid-op) is distinguished from missing item via Cosmos `x-ms-substatus: 1003` and surfaces as ResourceNotFoundException; missing items remain idempotent successes.
- Key attribute values (S/B) are hex-encoded into the internal Cosmos `id`/partition-key (S → hex(UTF-8 bytes), B → hex(raw bytes), N → order-preserving numeric digit string), so Cosmos-forbidden characters (`/`, `\`, `?`, `#`) are accepted. The encoding is order- and prefix-preserving and invisible to clients. Effective raw key limit is ~127 bytes (hex doubles length against Cosmos' 255-char id cap); over-limit keys are rejected with ValidationException. **On-disk-format breaking change** — items written by earlier builds route under a different id.
- Cosmos 429 surfaced as DynamoDB ProvisionedThroughputExceededException — including 429 on metadata read.
- Conditional deletes use the single-item `atomicWrite_v2` Cosmos stored procedure when stored procedures are enabled and the ConditionExpression is within the sproc's supported subset (scalar comparisons, AND/OR/NOT, `attribute_exists`/`attribute_not_exists`, `attribute_type` of S/N/BOOL/NULL/L/M, `begins_with`, BETWEEN, IN): the condition is evaluated server-side and the delete is applied atomically, addressing the document by its own `_self` link from the read query (a constructed `getSelfLink() + 'docs/' + id` link is an invalid mixed link rejected by real Cosmos). **Validated against real Azure Cosmos DB** (Strong consistency). Conditions outside the subset (`size()`, `contains()`, `attribute_type` of a set/binary, binary/set literal comparisons, list-index paths) and the case where the sproc is unavailable (e.g. the emulator) fall back to the non-atomic GET → DELETE path under mode `Preferred`, or fail loud under `Required`.
- Smoke-verified against the Cosmos DB Linux emulator (vNext preview) via Testcontainers; the fallback path is emulator-covered, the `atomicWrite_v2` sproc path is validated against real Azure Cosmos DB.
- When the existing item is materialized for ConditionExpression evaluation (the GET → DELETE fallback path) and Cosmos returns a CosmosBinary body (opt-in `DynamoDb.CosmosBinaryResponses`), the AttributeValue map is built straight off the binary body via `CosmosBinaryReader` (no binary→text decode + JsonDocument DOM), falling back to decode-to-text on an unsupported marker. The chosen path is observable on `aws2azure_dynamodb_read_decode_path_total{op="delete",path=binary|fallback|text}`. The emulator never emits CosmosBinary, so the binary-direct path is exercised against real Azure only.

## DeleteTable

- **Status:** ✅ implemented
- **Azure equivalent:** `Azure Cosmos DB (Core SQL API) — DELETE /dbs/{db}/colls/{name}`

### Sub-features

| Name | Status | Real-Azure | Notes | Gap | Workaround |
|---|---|---|---|---|---|
| Synchronous delete | ✅ implemented | — |  |  |  |
| TableDescription echoed (key schema, attrs) via sidecar metadata | ✅ implemented | — |  |  |  |

### Behaviour differences

- DynamoDB DeleteTable is asynchronous (returns DELETING). The proxy returns the same DELETING status for SDK parity even though the Cosmos delete is synchronous.
- On a non-existent table the proxy returns ResourceNotFoundException.
- Smoke-verified against the Cosmos DB Linux emulator (vNext preview) via Testcontainers; not yet exercised against real Azure Cosmos DB.

### References

- <https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_DeleteTable.html>
- <https://learn.microsoft.com/rest/api/cosmos-db/delete-a-collection>

## DescribeTable

- **Status:** ✅ implemented
- **Azure equivalent:** `Azure Cosmos DB (Core SQL API) — GET /dbs/{db}/colls/{name} + sidecar metadata`

### Sub-features

| Name | Status | Real-Azure | Notes | Gap | Workaround |
|---|---|---|---|---|---|
| AttributeDefinitions / KeySchema round-trip | ✅ implemented | — |  |  |  |
| BillingModeSummary echo | ✅ implemented | — |  |  |  |
| TableArn synthesis (azure-region pseudo-arn) | ✅ implemented | — |  |  |  |
| ItemCount / TableSizeBytes (live metrics) | ⛔ unsupported | — |  |  |  |
| GSI/LSI description | 🟡 partial | — |  |  |  |

### Behaviour differences

- ItemCount and TableSizeBytes default to 0; populating them requires either Cosmos partition-key statistics or a stored aggregate, deferred to a later slice.
- TableArn is synthetic (region 'azure', account '000000000000'); real AWS arns carry the region + account id which are not meaningful in this deployment.
- Tables created out-of-band (no sidecar metadata) still describe but with empty attribute/key arrays.
- GSI/LSI descriptions echo the persisted index schema (IndexName, KeySchema, Projection) plus a synthetic IndexArn; GSIs report IndexStatus=ACTIVE. IndexSizeBytes / ItemCount / Backfilling / ProvisionedThroughput are not populated.

### References

- <https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_DescribeTable.html>
- <https://learn.microsoft.com/rest/api/cosmos-db/get-a-collection>

## DescribeTimeToLive

- **Status:** 🟡 partial
- **Azure equivalent:** `Azure Cosmos DB container `defaultTtl` / per-item `ttl``

### Sub-features

| Name | Status | Real-Azure | Notes | Gap | Workaround |
|---|---|---|---|---|---|
| Reports ENABLED/DISABLED + AttributeName | ✅ implemented | — | Reads the proxy's per-table metadata sidecar and returns `{TimeToLiveDescription: {TimeToLiveStatus: "ENABLED"\|"DISABLED", AttributeName: <name>}}`. AttributeName is echoed only when TTL is enabled, matching DynamoDB. |  |  |

### Behaviour differences

- Reports the TTL state recorded by this proxy (the metadata sidecar written by `UpdateTimeToLive`). A Cosmos container whose `defaultTtl` was configured out-of-band (not via this proxy) is not reflected here, since the DynamoDB attribute name is unknown to the proxy.
- DynamoDB's transient `ENABLING` / `DISABLING` states are not surfaced; the proxy flips between ENABLED and DISABLED synchronously once the Cosmos container replace + metadata write complete.
- Validated against real Azure Cosmos DB alongside UpdateTimeToLive.

### References

- <https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_DescribeTimeToLive.html>
- <https://learn.microsoft.com/en-us/azure/cosmos-db/nosql/time-to-live>

## GetItem

- **Status:** 🟡 partial
- **Azure equivalent:** `Azure Cosmos DB (Core SQL API)`

### Sub-features

| Name | Status | Real-Azure | Notes | Gap | Workaround |
|---|---|---|---|---|---|
| HASH-only key tables | ✅ implemented | — |  |  |  |
| HASH+RANGE composite key tables | ✅ implemented | — |  |  |  |
| Full wire-form round-trip on response Item | ✅ implemented | — |  |  |  |
| ConsistentRead | 🟡 partial | — | Mapped to Cosmos `x-ms-consistency-level: Strong` request header. Honoured only when the account's max consistency permits Strong; Session/weaker accounts silently downgrade. Opt-in startup probe (`DynamoDb.ConsistencyCheck` = Warn/Required, #204) detects such accounts at boot and warns or fails startup. |  |  |
| ProjectionExpression | ✅ implemented | ✅ | Top-level attributes, `#alias` references, and nested document paths (`a.b` map members, `a[0]` list indices, and combinations like `a.b[1]`) are honoured. A projected map keeps only the referenced members; a projected list is compacted to the referenced indices in ascending order (positions are not preserved, matching DynamoDB); paths that do not exist or whose type does not match are silently omitted. Overlapping paths (e.g. `a` and `a.b`, or a duplicate) are rejected with ValidationException. A ProjectionExpression forces the materialized read path (extract → prune → buffered write); a plain GetItem keeps the fused stream-splice fast path. |  |  |
| AttributesToGet | ⛔ unsupported | — | Legacy AttributesToGet is rejected with ValidationException (matching Query/Scan/BatchGetItem); use ProjectionExpression. |  |  |
| ExpressionAttributeNames | ✅ implemented | — | Honoured as `#alias` substitution within ProjectionExpression. Supplying ExpressionAttributeNames without a ProjectionExpression is rejected with ValidationException. |  |  |
| ReturnConsumedCapacity | ⛔ unsupported | — |  |  |  |

### Behaviour differences

- Cosmos storage-metadata system fields (`_rid`/`_self`/`_etag`/`_ts`/`_attachments`/`_lsn`/`_metadata`) are stripped from response items and never surface as DynamoDB attributes (#203). Caveat: a user attribute literally named identically is also stripped on read; the durable fix is attribute namespacing.
- Missing item yields 200 with no `Item` field (matches DynamoDB).
- Missing container (table deleted mid-op) is distinguished from missing item via Cosmos `x-ms-substatus: 1003` and surfaces as ResourceNotFoundException.
- Key attribute values (S/B) are hex-encoded into the internal Cosmos `id`/partition-key (S → hex(UTF-8 bytes), B → hex(raw bytes), N → order-preserving numeric digit string), so Cosmos-forbidden characters (`/`, `\`, `?`, `#`) are accepted. The encoding is order- and prefix-preserving and invisible to clients. Effective raw key limit is ~127 bytes (hex doubles length against Cosmos' 255-char id cap); over-limit keys are rejected with ValidationException. **On-disk-format breaking change** — items written by earlier builds route under a different id.
- ConsistentRead effectiveness is account-dependent; document divergence per deployment.
- Multi-region Cosmos accounts honor configured `cosmos.preferredRegions` for read locality and client-side failover: GetItem routes to the first available readable preferred region, then remaining readable regions, then the configured account endpoint. Failover is implemented for regional 503/408 and transport failures; emulator coverage is unavailable because the Cosmos emulator is single-region.
- Cosmos binary JSON response bodies are supported only when explicitly enabled with `DynamoDb.CosmosBinaryResponses=true`; the proxy sends `x-ms-cosmos-supported-serialization-formats: CosmosBinary`, decodes `0x80` CosmosBinary bodies back to JSON before the normal DynamoDB response transform, and falls back to the unchanged text path whenever Cosmos returns text. For GetItem the binary body is streamed straight into the response envelope via `CosmosBinaryReader` (a forward-only `ITokenReader` over the binary format), skipping the intermediate decode-to-text materialization; output is byte-identical to the decode-then-text path (pinned by a full-marker-surface differential corpus and randomized fuzz tests), and any marker the streaming reader does not fast-path falls back to the proven decode-to-text path before any byte is emitted. The decode path taken per request is observable via the `aws2azure_dynamodb_getitem_decode_path_total{path="fused|fallback|text"}` Prometheus counter, so an operator can confirm the fast path is active in their topology. Emulator-unverified: the Cosmos DB Linux emulator used by CI does not emit CosmosBinary bodies, so the fused path is exercised only against real Azure (the `run-real-azure` integration job asserts the `path="fused"` counter increments on a binary GetItem round-trip).
- Cosmos 429 on metadata read surfaces as ProvisionedThroughputExceededException (not a fake ResourceNotFoundException).
- Smoke-verified against the Cosmos DB Linux emulator (vNext preview) via Testcontainers; the CosmosBinary fused path is additionally validated against real Azure Cosmos DB by the `run-real-azure` integration job (decode-path counter assertion). The text/fallback decode-path tagging is covered by unit tests.

### References

- <https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_GetItem.html>

## ListTables

- **Status:** ✅ implemented
- **Azure equivalent:** `Azure Cosmos DB (Core SQL API) — GET /dbs/{db}/colls`

### Sub-features

| Name | Status | Real-Azure | Notes | Gap | Workaround |
|---|---|---|---|---|---|
| Limit (1..100) | ✅ implemented | — |  |  |  |
| ExclusiveStartTableName cursor | ✅ implemented | — |  |  |  |
| LastEvaluatedTableName pagination | ✅ implemented | — |  |  |  |

### Behaviour differences

- Container names are sorted ordinally (case-sensitive). DynamoDB pagination is also ordinal so the cursor semantics match.
- All containers in the configured database are surfaced, including sidecar-less ones. Operators using a shared database for non-DynamoDB workloads will see those container ids too.
- Pagination is server-side: the proxy fetches all containers once and slices in-memory. For databases with thousands of containers this should be split across Cosmos result pages — tracked as a follow-up.

### References

- <https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_ListTables.html>
- <https://learn.microsoft.com/rest/api/cosmos-db/list-collections>

## ListTagsOfResource

- **Status:** ✅ implemented
- **Azure equivalent:** `Azure Cosmos DB account/resource tags (control plane)`

### Sub-features

| Name | Status | Real-Azure | Notes | Gap | Workaround |
|---|---|---|---|---|---|
| Returns persisted TableMetadata tags | ✅ implemented | — | Reads tags from the aws2azure TableMetadata sidecar document written by TagResource. |  |  |
| Pagination | 🟡 partial | — | The proxy returns the full tag set (DynamoDB allows at most 50 tags) and rejects NextToken instead of paginating. |  |  |

### Behaviour differences

- Tags are stored in the aws2azure TableMetadata sidecar document inside the table's Cosmos container, not as Azure control-plane resource tags.
- Persisted tags have no effect on Azure billing, routing, Azure Policy, or Azure-native tag queries.
- Acceptance has unit-test coverage against the Cosmos REST test double; real-Azure validation is pending.

### References

- <https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_ListTagsOfResource.html>

## PutItem

- **Status:** 🟡 partial
- **Azure equivalent:** `Azure Cosmos DB (Core SQL API)`

### Sub-features

| Name | Status | Real-Azure | Notes | Gap | Workaround |
|---|---|---|---|---|---|
| HASH-only key tables | ✅ implemented | — |  |  |  |
| HASH+RANGE composite key tables | ✅ implemented | — |  |  |  |
| Full DynamoDB wire-form round-trip (S/N/B/BOOL/NULL/M/L/SS/NS/BS) | ✅ implemented | — | Attributes stored as inferred Cosmos JSON (no `{S}`/`{N}` wrapping); number values are normalised to DynamoDB's canonical decimal form (no trailing zeros, no exponent, no `-0`) — matching real DDB's documented behaviour. Numbers whose canonical form exceeds IEEE 754 double round-trip safety are stored via the `{"_a2a:N":"<canonical>"}` envelope so 16–38 digit precision survives Cosmos storage byte-identical. |  |  |
| ConditionExpression / Expected / ConditionalOperator | ✅ implemented | — | Conditional path performs GET → evaluate → PUT(If-Match) or POST(If-None-Match: *) with up to 4 retries on Cosmos 412/409. Failure returns HTTP 400 ConditionalCheckFailedException with optional Item when ReturnValuesOnConditionCheckFailure=ALL_OLD. attribute_not_exists(pk) is the standard idiom for first-time create. |  |  |
| ExpressionAttributeNames / ExpressionAttributeValues | ⛔ unsupported | — |  |  |  |
| ReturnValues | 🟡 partial | — | Only NONE accepted; ALL_OLD/UPDATED_* rejected with ValidationException. |  |  |
| ReturnConsumedCapacity / ReturnItemCollectionMetrics | ⛔ unsupported | — | Silently ignored; response omits ConsumedCapacity / ItemCollectionMetrics. |  |  |

### Behaviour differences

- Cosmos storage-metadata system fields (`_rid`/`_self`/`_etag`/`_ts`/`_attachments`/`_lsn`/`_metadata`) are stripped from response items and never surface as DynamoDB attributes (#203). Caveat: a user attribute literally named identically is also stripped on read; the durable fix is attribute namespacing.
- Attributes are stored *flat* on the Cosmos document — `{id, _a2a_pk, ...inferred attributes...}` — with no per-item type wrapper. Scalar values are stored without `{S}`/`{N}`/`{B}` tags; the type is inferred on read from the JSON value kind. Maps/lists nest as Cosmos JSON. Number values are normalised to DDB canonical form (matching real DDB) — e.g. `42.0`→`42`, `1e10`→`10000000000`. Values that cannot survive an IEEE 754 round-trip (high-precision numbers, binary, sets) are stored under a typed envelope (`_a2a:N`, `_a2a:B`, `_a2a:SS`, `_a2a:NS`, `_a2a:BS`).
- Attribute names with the reserved `_a2a:` prefix are rejected with ValidationException at the API surface, both at top level and inside nested maps. The prefix is reserved for the typed-envelope encoding above; allowing user attributes to collide would let callers shadow / probe storage internals.
- Numbers outside DynamoDB's published range are rejected with ValidationException — match real DDB. The bounds are 38 significant digits of precision and a magnitude (most-significant-digit exponent) in [1e-130, ~9.99e+125]; the lower bound is a magnitude floor, so values like `1.1e-130` (magnitude ≥ 1e-130, with digits below 1e-130) are accepted exactly as real DynamoDB does.
- Sentinel id `__aws2azure_table_meta__` is no longer special-cased for key values: S/B keys are hex-encoded before routing (see below), so a user key can never collide with the sidecar sentinel and the value is stored normally.
- Key attribute values are encoded into the internal Cosmos `id`/partition-key with an **order-preserving, digits-only codec** so Cosmos collation never matters: S → lowercase hex(UTF-8 bytes); B → lowercase hex(raw bytes after base64-decode); N → a fixed-width sign+exponent+mantissa digit string (sign flag, 3-digit biased decimal exponent, 38-digit mantissa = 42 chars) that sorts in true numeric order. Numerically-equal numbers collapse to one id (`42`, `42.0`, `4.2e1`, `+42` → same; `0`, `-0`, `0.0` → same), fixing the earlier bug where `{"N":"42"}` and `{"N":"42.0"}` routed to different documents. This is an **on-disk-format breaking change** — documents written by earlier builds route under a different id and are not readable by this build. The encoding is invisible to clients (key attributes are always returned from the flat-stored attributes, never reconstructed from id). Cosmos-forbidden characters (`/`, `\`, `?`, `#`) and previously-rejected empty-after-trim strings are now accepted because the codec never emits them. Because hex doubles length and Cosmos caps the id at 255 chars, the effective raw S/B key limit is ~127 bytes (vs DynamoDB's 1024); N keys are always 42 chars; over-limit keys are rejected with ValidationException.
- Cosmos 429 (throttled) is surfaced to clients as DynamoDB ProvisionedThroughputExceededException — including 429 on the sidecar metadata read.
- Conditional writes use the single-item `atomicWrite_v2` Cosmos stored procedure when stored procedures are enabled and the ConditionExpression is within the sproc's supported subset (scalar comparisons, AND/OR/NOT, `attribute_exists`/`attribute_not_exists`, `attribute_type` of S/N/BOOL/NULL/L/M, `begins_with`, BETWEEN, IN): the condition is evaluated server-side and the PUT is applied atomically in one round-trip. **Validated against real Azure Cosmos DB** (Strong consistency). The v2 body reads the existing document with a partition-local `SELECT * FROM c WHERE c.id = @id` query (an earlier `getSelfLink() + 'docs/' + id` read link was an invalid RID+id mixed link rejected by real Cosmos with `Error creating request message`; the emulator could not catch it because it does not run sprocs) and strips Cosmos system fields before the upsert. Conditions outside the subset — `size()`, `contains()`, `attribute_type` of a set/binary, comparisons against binary/set literals, list-index paths — are routed away from the sproc: under mode `Preferred` the request falls back to the non-atomic GET → PUT loop; under `Required` it fails loud rather than degrade atomicity.
- Smoke-verified against the Cosmos DB Linux emulator (vNext preview) via Testcontainers; the GET → PUT fallback path is emulator-covered, the `atomicWrite_v2` sproc path is validated against real Azure Cosmos DB.
- When the existing item is materialized for ConditionExpression evaluation (the GET → PUT loop) and Cosmos returns a CosmosBinary body (opt-in `DynamoDb.CosmosBinaryResponses`), the AttributeValue map is built straight off the binary body via `CosmosBinaryReader` (no binary→text decode + JsonDocument DOM), falling back to decode-to-text on an unsupported marker. The chosen path is observable on `aws2azure_dynamodb_read_decode_path_total{op="put",path=binary|fallback|text}`. The emulator never emits CosmosBinary, so the binary-direct path is exercised against real Azure only.
- The standalone document write body (the unconditional upsert and the non-atomic GET → PUT/POST fallback) is sent as CosmosBinary (the `0x80` format) when the opt-in `DynamoDb.CosmosBinaryRequests` is enabled (default off), encoded single-pass straight to the wire; the gateway auto-detects the marker so no negotiation header or special Content-Type is used. The sproc-embedded conditional path (`atomicWrite_v2`) keeps JSON text, since the document is embedded as a value inside the sproc parameter array. The chosen format is observable on `aws2azure_dynamodb_write_body_total{format=binary|text}`. The Cosmos DB Linux emulator neither emits nor reliably accepts CosmosBinary, so the binary write path is validated against real Azure only — confirmed parsed + indexed (text read-back + indexed query) by the nightly acceptance test, not stored opaquely.

## Query

- **Status:** 🟡 partial
- **Azure equivalent:** `Azure Cosmos DB (Core SQL API)`

### Sub-features

| Name | Status | Real-Azure | Notes | Gap | Workaround |
|---|---|---|---|---|---|
| KeyConditionExpression on HASH-only tables | ✅ implemented | — |  |  |  |
| KeyConditionExpression on HASH+RANGE tables (= / < / <= / > / >= / BETWEEN / begins_with) | ✅ implemented | — | Translated to a partition-scoped Cosmos SQL query against `c.pk = <hash>` with a predicate on `c.id` (which holds the formatted RANGE value). RANGE (and HASH) key values share one order-preserving, digits-only codec with storage (S → hex(UTF-8 bytes); B → hex(raw bytes); N → fixed-width sign+exponent+mantissa digit string), so ordered comparisons, BETWEEN, and begins_with on S/B sort keys compare in correct DynamoDB byte order — `begins_with` maps to an exact prefix match because hex is prefix-preserving on byte boundaries — and ordered comparisons / BETWEEN on N sort keys compare in true numeric order. `begins_with` on an N sort key is rejected (ValidationException), matching real DDB. Query operands and stored ids share the codec, so they always agree. |  |  |
| FilterExpression | ✅ implemented | — | Pushed into the Cosmos SQL WHERE clause where safe; the remainder is evaluated in-process after the Cosmos page returns. Count always reflects post-filter rows. ScannedCount reflects pre-filter rows: when nothing is pushed it is the streamed count; when a fragment is pushed (Cosmos pre-filters) a complete unbounded query recovers it with a partition-scoped server-side `SELECT VALUE COUNT(1)` over the same key scope minus the pushed filter, so it stays faithful to DynamoDB. The pushed-filter + Limit combination is a documented divergence (see behavior_differences). Predicates supported: comparison (=, <, <=, >, >=), BETWEEN, IN, attribute_exists/not_exists/type, begins_with, contains, AND/OR/NOT. Pushdown carve-outs (these stay residual): `<>` on any path (DDB cross-type semantics), ordered comparisons / BETWEEN on B (base64 lexical order ≠ underlying byte order), begins_with on B, size(), nested paths whose first segment matches the reserved `_a2a:` envelope prefix. Numeric equality (=) and IN push a hybrid IS_NUMBER / `StringToNumber(_a2a:N)` branch as a *prefilter only* — false negatives are impossible by construction (envelope values cannot exactly equal a round-trippable parameter) and the client-side evaluator re-checks the exact canonical string anyway. Numeric ordered comparisons (<, <=, >, >=) and BETWEEN widen the envelope branch to `IS_DEFINED(_a2a:N)` so every envelope-stored row reaches the residual evaluator — otherwise `StringToNumber` rounding could false-negative boundary values. |  |  |
| ProjectionExpression | ✅ implemented | ✅ | Top-level attributes, `#alias` references, and nested document paths (`a.b` map members, `a[0]` list indices, and combinations like `a.b[1]`) are honoured. A projected map keeps only the referenced members; a projected list is compacted to the referenced indices in ascending order (positions are not preserved, matching DynamoDB); paths that do not exist or whose type does not match are silently omitted. Overlapping paths (e.g. `a` and `a.b`, or a duplicate) are rejected with ValidationException. Applied in-process after the Cosmos page returns. |  |  |
| ExpressionAttributeNames / ExpressionAttributeValues | ✅ implemented | — |  |  |  |
| Limit | ✅ implemented | — |  |  |  |
| ExclusiveStartKey / LastEvaluatedKey | ✅ implemented | — | Pagination round-trips the Cosmos `x-ms-continuation` token inside a sentinel attribute `__a2a_continuation` (typed-string `S`). Most AWS SDKs treat LastEvaluatedKey as opaque and pass it back verbatim, which is what the proxy requires. |  |  |
| ScanIndexForward | ✅ implemented | — | Maps to `ORDER BY c.id ASC\|DESC`; only emitted for composite-key tables (hash-only Query returns at most one item). |  |  |
| ConsistentRead | ✅ implemented | — | Forwards `x-ms-consistency-level: Strong` for the Cosmos query when true; account-level consistency cap still applies. Opt-in startup probe (`DynamoDb.ConsistencyCheck` = Warn/Required, #204) flags accounts that cannot honor Strong at boot. |  |  |
| Select | 🟡 partial | — | ALL_ATTRIBUTES (default for base-table queries), SPECIFIC_ATTRIBUTES, and COUNT supported. SPECIFIC_ATTRIBUTES requires a ProjectionExpression (rejected without one, matching DynamoDB). On an LSI query, ALL_PROJECTED_ATTRIBUTES (also the default when neither Select nor ProjectionExpression is supplied, matching DynamoDB) resolves against the index projection: ALL behaves like ALL_ATTRIBUTES; KEYS_ONLY projects (in-process) to the base HASH + base RANGE + the LSI sort attribute; INCLUDE adds the index's NonKeyAttributes. An explicit ProjectionExpression always takes precedence. Without IndexName, ALL_PROJECTED_ATTRIBUTES is rejected. |  |  |
| IndexName (GSI / LSI) | 🟡 partial | — | Local Secondary Index (LSI) Query is supported. An LSI shares the base table HASH key, so the query stays partition-scoped (same `x-ms-documentdb-partitionkey` header as a base-table query); only the sort-key predicate and ORDER BY target the LSI's alternate sort attribute. The sort attribute is stored as a regular document attribute (raw storage form, not the key codec), so the sort-key predicate is translated against `c.<lsiSort>` by reusing the FilterExpression pushdown (Option-A) — comparison (= / < / <= / > / >=), BETWEEN, and begins_with are supported, with the same hybrid IS_NUMBER/`_a2a:N` envelope handling and residual fallback (high-precision envelope N re-checked in-process). begins_with on an N LSI sort key is rejected (ValidationException), matching real DDB. ORDER BY emits `ORDER BY c.<lsiSort> ASC\|DESC` honoring ScanIndexForward; items missing the sort attribute are excluded by an explicit IS_DEFINED guard (so sparse-index semantics hold regardless of the container indexing policy), matching LSI sparse-index behavior. For a numeric (N) LSI sort key an opt-in, default-off config flag (`DynamoDb.EnableLocalSecondaryIndexNumericOrdering=true`) instead orders by (and range-filters against) the synthetic order-preserving encoded field `_a2a$ord$<attr>` so high-precision `_a2a:N` envelope values order in true numeric order; because that switch adds an `IS_DEFINED(_a2a$ord$<attr>)` guard that excludes items written before the field existed, and LSI Query is otherwise always-on, the flag is opt-in and should be enabled only after a backfill or for new tables (see behavior_differences). ConsistentRead is accepted (LSIs are strongly consistent). LSI ScannedCount caveat: see behavior_differences. Global Secondary Index (GSI) Query is supported behind an opt-in, default-off config flag (`DynamoDb.EnableGlobalSecondaryIndexQueries=true`); when the flag is off a GSI IndexName is rejected with ValidationException ("Querying global secondary indexes is not yet supported by the proxy"). With the flag on, a GSI Query is served as a cross-partition Cosmos query (no `x-ms-documentdb-partitionkey` header; `x-ms-documentdb-query-enablecrosspartition: true`). The GSI HASH equality and optional sort-key predicate target the index's own attributes stored raw (Option-A), translated via the FilterExpression pushdown against `c.<gsiHash>` / `c.<gsiSort>` (HASH must be `=`; sort predicate supports =/</<=/>/>= , BETWEEN, begins_with with the same N envelope/residual handling; begins_with on N is rejected). For an ordered numeric (N) GSI sort key the sort-key predicate is instead translated exactly against the encoded `_a2a$ord$<attr>` field (each operand encoded the same way), so range conditions filter server-side with no residual — avoiding an over-scan that could mis-paginate under Limit. IS_DEFINED guards on the index key attribute(s) enforce GSI membership (an item indexes only if it carries the key attributes); composite GSIs emit `ORDER BY … ASC\|DESC` honoring ScanIndexForward, hash-only GSIs return unordered. For a numeric (N) GSI sort key the ORDER BY targets a synthetic order-preserving encoded field (`_a2a$ord$<attr>`, a digits-only lexical encoding of the number written at item-write time by every write path), so high-precision `_a2a:N` envelope values order in true numeric order rather than being sorted structurally as objects; the query adds an `IS_DEFINED(_a2a$ord$<attr>)` membership guard, and the client-side merge comparator recomputes the same encoding from each row's raw `{"N":…}` value (the hidden field is never projected). For a string (S) sort key the ORDER BY targets the raw attribute. A composite GSI Query is ordered across partitions by a client-side fan-out + merge executor (real Cosmos cannot serve an ordered cross-partition query in one request); see behavior_differences for the per-range merge, continuation, and concurrent-mutation caveat. An ordered (non-COUNT) GSI query on a binary (B) sort key is rejected with ValidationException (B is envelope-stored and cannot be ordered by the per-range query). GSIs are eventually consistent: ConsistentRead=true is rejected with ValidationException. The base-table ScannedCount aggregate recovery is skipped for GSI queries (see behavior_differences). Because a GSI is a projected view that cannot fetch non-projected attributes from the base table, `Select=ALL_ATTRIBUTES` is rejected unless the GSI projection type is ALL, and a `ProjectionExpression` referencing an attribute not projected into the index is rejected (ValidationException), matching DynamoDB. An IndexName matching no index is rejected with ValidationException ("The table does not have the specified index"). |  |  |
| Legacy KeyConditions / QueryFilter / ConditionalOperator | ⛔ unsupported | — | Legacy v1 parameters are rejected loudly with ValidationException — use KeyConditionExpression / FilterExpression. |  |  |
| ReturnConsumedCapacity | ⛔ unsupported | — | Silently ignored; response omits ConsumedCapacity. |  |  |

### Behaviour differences

- Cosmos storage-metadata system fields (`_rid`/`_self`/`_etag`/`_ts`/`_attachments`/`_lsn`/`_metadata`) are stripped from response items and never surface as DynamoDB attributes (#203). Caveat: a user attribute literally named identically is also stripped on read; the durable fix is attribute namespacing.
- Sort-key ordering follows the order-preserving key codec: S/B sort keys compare in true byte order and N sort keys compare in true numeric order (numerically-equal forms like `42`/`42.0` collapse to one id). `begins_with` is only valid on S/B sort keys; on an N sort key it is rejected with ValidationException, matching real DDB.
- Every Query is partition-scoped — there is no cross-partition fan-out — matching DynamoDB's single-partition guarantee.
- Multi-region Cosmos accounts honor configured `cosmos.preferredRegions` for read locality and client-side failover: Query routes to the first available readable preferred region, then remaining readable regions, then the configured account endpoint. Failover is implemented for regional 503/408 and transport failures; emulator coverage is unavailable because the Cosmos emulator is single-region.
- Cosmos binary JSON response bodies are supported only when explicitly enabled with `DynamoDb.CosmosBinaryResponses=true`; the proxy sends `x-ms-cosmos-supported-serialization-formats: CosmosBinary`, decodes `0x80` CosmosBinary query pages back to JSON before the normal DynamoDB response transform, and falls back to the unchanged text path whenever Cosmos returns text. Emulator-unverified: the Cosmos DB Linux emulator used by CI does not emit CosmosBinary bodies.
- Queries with no FilterExpression, no ProjectionExpression, no COUNT Select, and nothing pushed into the Cosmos SQL use a *fused* streaming transform: Cosmos `Documents` are rewritten directly into the DynamoDB response envelope (`{"Items":[…],"Count":N,"ScannedCount":N[,"LastEvaluatedKey":…]}`) without materializing a per-item AttributeValue map. Output is byte-identical to the materialized path (pinned by a golden test). The path taken is observable via the `aws2azure_dynamodb_read_transform_path_total{op="query",path="fused"|"materialized"}` counter. Filtered/projected/pushed queries and COUNT queries stay on the materialized path because they need per-item logic (in-process filter evaluation, projection). Measured ~9x faster (≈−89% CPU) and per-page managed allocation reduced from ~280 KB (100 lean items) / ~2.8 MB (1000 lean items) to a constant 168 B, on `tests/Aws2Azure.Benchmarks` `CosmosMultiItemTransformBenchmarks` (AMD EPYC 7763, .NET 10); benchmark numbers are host-bound, not real-Azure RU.
- When the fused path runs on a CosmosBinary page it streams each page straight off the binary body via `CosmosBinaryReader` directly into the response writer — a pure binary-direct streaming transform with no binary→text page decode and no decode-to-text fallback. A marker the streaming reader declines propagates as an error, exactly like a malformed text page would; the fused path keeps no per-page scratch buffer or rollback. This deliberately diverges from the single-document and materialized read paths, which retain a per-document decode-to-text fallback, because the fused path shares one cross-page response writer that cannot be rolled back. The decode path is observable via `aws2azure_dynamodb_read_decode_path_total{op="query",path=binary|text}`. Measured −35…−40% CPU and half the managed allocation (336 B → 168 B) vs decode-to-text-fused on `tests/Aws2Azure.Benchmarks` `CosmosFusedBinaryDirectBenchmarks` (AMD EPYC 7763, .NET 10; host-bound, not real-Azure RU). The emulator never emits CosmosBinary, so the binary-direct fused path is exercised against real Azure only.
- Filtered/projected/pushed queries and COUNT queries stay on the materialized path, but when Cosmos returns a CosmosBinary page the per-document AttributeValue maps are built straight off the binary body via `CosmosBinaryReader` (no binary→text page decode + full-page JsonDocument DOM); a marker the streaming reader declines makes the whole page atomically fall back to decode-to-text. The decode path is observable via `aws2azure_dynamodb_read_decode_path_total{op="query",path=binary|fallback|text}`. The emulator never emits CosmosBinary, so the binary-direct path is exercised against real Azure only.
- When a FilterExpression is pushed into the Cosmos SQL, ScannedCount for a single complete unbounded query (no Limit, no incoming ExclusiveStartKey, no outgoing continuation) is recovered faithfully via a partition-scoped server-side `SELECT VALUE COUNT(1)` over the same key scope minus the pushed filter. The recovery is deliberately skipped on a paginated pushed-filter query — both for a Limit and for any page reached via ExclusiveStartKey — because the whole-scope aggregate cannot reproduce a single page's pre-filter boundary; on those pages ScannedCount falls back to the streamed (post-prefilter) count and may be lower than DynamoDB's. The proxy preserves the Cosmos page boundary in that case: it returns after the first non-empty page and surfaces the Cosmos continuation as LastEvaluatedKey, rather than topping up matches across pages with a drifted page boundary. If the count aggregate cannot be read it falls back to the streamed count. Because the recovery aggregate is a second round-trip, a concurrent write between the data pass and the count means the recovered ScannedCount is from a marginally different snapshot than Count — real DynamoDB scans-then-filters in one pass and cannot exhibit this; it is accepted as best-effort.
- Cosmos 429 (throttled) is surfaced as DynamoDB ProvisionedThroughputExceededException.
- LSI Query ordering follows Cosmos collation on the raw stored sort attribute, which can diverge from DynamoDB: real DDB orders S sort keys in UTF-8 byte order whereas Cosmos ORDER BY on a raw S attribute uses Unicode code-point collation (these agree on BMP/ASCII but can differ for multi-byte sequences such as surrogate-pair code points); and high-precision N values stored in the `_a2a:N` envelope are not ordered numerically by Cosmos (the bare-number branch orders numerically, the envelope branch does not). For numeric (N) LSI sort keys this mis-ordering is fixed via the same synthetic order-preserving encoded field (`_a2a$ord$<attr>`, "Option B") used for GSI, behind an opt-in, default-off config flag (`DynamoDb.EnableLocalSecondaryIndexNumericOrdering=true`). Unlike GSI Query, LSI Query is always-on (not itself flag-gated), so switching its ORDER BY to the encoded field unconditionally would silently drop every item written before the field existed (Cosmos ORDER BY excludes rows where the path is undefined) — hence the separate opt-in: enable it only after a backfill (rewrite) or for new tables. The write path always emits `_a2a$ord$<attr>` for N-typed LSI sort keys (independent of the query flag), so items written after the proxy upgrade always carry it; the flag only controls whether the query orders by (and range-filters against) it, adding an `IS_DEFINED(_a2a$ord$<attr>)` guard that excludes pre-encoded legacy items rather than mis-ordering them. With the flag off (default), an N LSI query keeps the legacy raw-attribute ORDER BY (all items visible, high-precision envelope values structurally mis-ordered). The S code-point-collation divergence remains (raw-attribute ORDER BY). LSI Query ScannedCount reflects rows after the Cosmos prefilter of the sort-key predicate (and the sparse IS_DEFINED guard), not DynamoDB's pre-filter evaluated-items count: for exactly-pushable predicates (S keys, normal-precision N, and — with the flag on — encoded-field N) it equals DynamoDB, but for residual cases (flag-off high-precision envelope N) it may be lower. The base-table ScannedCount aggregate recovery is deliberately skipped for LSI queries (its scope is on `c.id`, not the LSI sort attribute).
- GSI Query (opt-in `DynamoDb.EnableGlobalSecondaryIndexQueries=true`) is a cross-partition Cosmos query, so unlike a base-table or LSI Query it fans out across partitions rather than honoring DynamoDB's single-partition guarantee; this is inherent to GSIs (which have their own partition key) and trades RU/latency for the index lookup. GSIs are eventually consistent in DynamoDB, so ConsistentRead=true is rejected (ValidationException). A hash-only GSI returns rows unordered (matching DynamoDB, which only orders within a GSI partition when the index is composite). A composite GSI is ordered by the index sort key: because real Azure Cosmos refuses to serve an ordered cross-partition query in a single request (the gateway returns a query plan and expects client-side fan-out + merge), the proxy lists the collection's physical partition key ranges (`GET .../pkranges`), runs the single-partition `ORDER BY c.<gsiSort>` against each range individually (`x-ms-documentdb-partitionkeyrangeid`), and k-way merges the per-range ordered streams client-side (Cosmos comparison semantics; cross-range ties broken deterministically by the range's `minInclusive`). Pagination uses a continuation of `(boundaryValue, direction, skip)` carried on the opaque `__a2a_continuation` LastEvaluatedKey sentinel: resume re-queries every range with `c.<gsiSort> >= V` (ASC) / `<= V` (DESC) and drops the first `skip` rows whose value equals `V`. This `skip` resume is exact while the per-range tie order is stable across the two requests (true for Cosmos's physical `_rid` index order over unchanged data — the same property the official SDK's `_rid` resume relies on); a concurrent insert/delete inside the exact boundary-value block between two page fetches can shift the `skip` alignment by the net change (a narrow eventual-consistency window; DynamoDB pagination exhibits analogous behaviour under concurrent writes). GSI ordering follows the same Cosmos-collation caveat as LSI for string (S) sort keys (raw-attribute ORDER BY: S code-point collation vs DDB UTF-8 byte order). Numeric (N) GSI sort keys do NOT share the LSI `_a2a:N` mis-ordering caveat: they are ordered by a synthetic order-preserving encoded field (`_a2a$ord$<attr>`) written at item-write time by every write path ("Option B"), so high-precision envelope values order in true numeric order; the query filters on `IS_DEFINED(_a2a$ord$<attr>)` and the client-side merge comparator recomputes the encoding from each row's raw `{"N":…}` value (deterministic, so the per-range SQL order and the client merge order agree exactly). Two limitations follow from the encoded field being write-time: (1) items written before the field existed (or before the containing table declared the N GSI) lack `_a2a$ord$<attr>` and are excluded from ordered N-GSI results by the IS_DEFINED guard until rewritten — a backfill gap, acceptable while GSI Query is default-off/experimental; (2) UpdateItem on a table with an N GSI sort key skips the server-side merge sproc (which cannot recompute the encoded field) and falls back to the read-modify-write path so the field stays consistent. A binary (B) GSI sort key is always envelope-stored (`{"_a2a:B":...}`), so it cannot be ordered by the per-range query; an ordered (non-COUNT) GSI query on a B sort key is rejected with ValidationException. A composite-GSI COUNT query does not need ordering and stays on the unordered cross-partition loop (no fan-out executor). GSI Query ScannedCount reflects rows after the Cosmos prefilter of the key condition (and the IS_DEFINED membership guard), not DynamoDB's pre-filter evaluated-items count; the base-table aggregate recovery is skipped for GSI queries (the count scope is cross-partition on the index key attribute, not `c.id`). The fan-out seeds one in-flight page per overlapping range (peak memory O(ranges × page); page size bounded), the documented footprint cost of the opt-in feature. Numeric (N) GSI sort keys now order correctly via the `_a2a$ord$<attr>` synthetic order-preserving field ("Option B", implemented for GSI); the analogous Option-B ordering for LSI numeric sort keys is implemented behind the opt-in `DynamoDb.EnableLocalSecondaryIndexNumericOrdering` flag (see the LSI behavior_difference above), while the string (S) collation divergence remains.
- Smoke-verified against the Cosmos DB Linux emulator (vNext preview) via Testcontainers, and validated against real Azure Cosmos DB by the `Category=RealAzure` secondary-index suite (`DynamoDbRealAzureSecondaryIndexTests`, run 28195043777): GSI hash-only cross-partition Query, composite GSI Query ordered by the sort key across real physical partitions (the client-side pkranges fan-out + merge-sort executor — single-request ordered cross-partition is refused by the Cosmos gateway), GSI Scan with KEYS_ONLY projection, LSI ordered Query (single-partition), and LSI Scan all pass against real Azure. The capability still ships default-off (`DynamoDb.EnableGlobalSecondaryIndexQueries=false`) because of the documented footprint (per-range fan-out peak memory) and remaining ordering/eventual-consistency caveats (Cosmos collation on raw S attributes, the skip-continuation concurrent-mutation window). The `_a2a$ord$<attr>` synthetic-field fix for high-precision numeric (N) GSI ordering ("Option B") is validated against real Azure Cosmos DB (`Category=RealAzure` run 28608295197: `Gsi_composite_query_orders_by_numeric_sort_key_across_real_pages` orders 21-digit envelope-stored integers across real physical partitions, ascending/descending, with Limit + LastEvaluatedKey resume). Option-B ordering for LSI numeric sort keys is implemented behind the opt-in `DynamoDb.EnableLocalSecondaryIndexNumericOrdering` flag; a `Category=RealAzure` LSI high-precision-numeric ordering test (`Lsi_query_orders_by_numeric_sort_key`) validates it against real Azure Cosmos DB.

### References

- <https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_Query.html>

## Scan

- **Status:** 🟡 partial
- **Azure equivalent:** `Azure Cosmos DB (Core SQL API)`

### Sub-features

| Name | Status | Real-Azure | Notes | Gap | Workaround |
|---|---|---|---|---|---|
| Full-table scan | ✅ implemented | — | Translated to a cross-partition Cosmos SQL query (`x-ms-documentdb-query-enablecrosspartition: true`). Every Scan is an O(N) walk of the container — expensive in RU. |  |  |
| FilterExpression | ✅ implemented | — | Pushed into the Cosmos SQL WHERE clause where safe; the remainder is evaluated in-process after each Cosmos page returns. Count always reflects post-filter rows. ScannedCount reflects pre-filter rows: when nothing is pushed it is the streamed count; when a fragment is pushed (Cosmos pre-filters at the storage layer) a complete unbounded pass recovers it with a server-side `SELECT VALUE COUNT(1)` over the same scope minus the pushed filter, so it stays faithful to DynamoDB. The pushed-filter + Limit combination is a documented divergence (see behavior_differences). Same pushdown carve-outs as Query: `<>`, ordered comparisons / BETWEEN / begins_with on B, size(), and paths whose first segment matches the reserved `_a2a:` envelope prefix stay residual. Numeric equality (=) and IN push a hybrid IS_NUMBER / `StringToNumber(_a2a:N)` branch as a *prefilter only* (false negatives impossible by construction; client-side evaluator re-checks the exact canonical string anyway). Numeric ordered comparisons (<, <=, >, >=) and BETWEEN widen the envelope branch to `IS_DEFINED(_a2a:N)` so every envelope-stored row reaches the residual evaluator — otherwise `StringToNumber` rounding could false-negative boundary values. |  |  |
| ProjectionExpression | ✅ implemented | ✅ | Top-level attributes, `#alias` references, and nested document paths (`a.b` map members, `a[0]` list indices, and combinations like `a.b[1]`) are honoured. A projected map keeps only the referenced members; a projected list is compacted to the referenced indices in ascending order (positions are not preserved, matching DynamoDB); paths that do not exist or whose type does not match are silently omitted. Overlapping paths are rejected with ValidationException. Applied in-process after the Cosmos page returns. |  |  |
| ExpressionAttributeNames / ExpressionAttributeValues | ✅ implemented | — |  |  |  |
| Limit | ✅ implemented | — | Caps the *scanned* (pre-filter) row count when the filter is residual-only; pageSize is sized to the remaining evaluation budget so the per-page continuation never skips rows. When a FilterExpression is pushed (fully or partially) into the Cosmos SQL, Cosmos pre-filters at the storage layer; an unbounded scan recovers the faithful ScannedCount via a server-side count, but a Limit cannot be reconciled with server-side pre-filtering — see behavior_differences for the page-boundary trade-off. |  |  |
| ExclusiveStartKey / LastEvaluatedKey | ✅ implemented | — | Pagination round-trips the Cosmos `x-ms-continuation` token inside a sentinel attribute `__a2a_continuation` (typed-string `S`). Most AWS SDKs treat LastEvaluatedKey as opaque and pass it back verbatim. |  |  |
| ConsistentRead | ✅ implemented | — | Forwards `x-ms-consistency-level: Strong` for the Cosmos query when true; account-level consistency cap still applies. Opt-in startup probe (`DynamoDb.ConsistencyCheck` = Warn/Required, #204) flags accounts that cannot honor Strong at boot. |  |  |
| Select | 🟡 partial | — | ALL_ATTRIBUTES (default), SPECIFIC_ATTRIBUTES, and COUNT supported. SPECIFIC_ATTRIBUTES requires a ProjectionExpression (rejected without one, matching DynamoDB). ALL_PROJECTED_ATTRIBUTES requires IndexName: on an index scan it resolves against the index projection (ALL → all attributes; KEYS_ONLY → base keys + the index's own key attributes; INCLUDE → those keys plus the index's NonKeyAttributes, applied in-process); without IndexName it is rejected. On a non-ALL GSI, Select=ALL_ATTRIBUTES is rejected (a GSI cannot fetch non-projected attributes from the base table). |  |  |
| IndexName (GSI / LSI) | 🟡 partial | — | Local Secondary Index (LSI) Scan is supported. An LSI scan is still cross-partition (Scan never scopes to a partition) but is restricted to the index's member items via an explicit `IS_DEFINED(c.<lsiSort>)` guard (sparse-index semantics hold regardless of the container indexing policy), and the index projection (ALL / KEYS_ONLY / INCLUDE) is resolved in-process. ScannedCount counts index members examined: with no pushed FilterExpression it is the streamed (post-IS_DEFINED) count; with a pushed filter on an unbounded scan it is recovered with a server-side `SELECT VALUE COUNT(1)` over the index scope (IS_DEFINED) minus the pushed filter. Global Secondary Index (GSI) Scan is supported behind the opt-in `DynamoDb.EnableGlobalSecondaryIndexQueries` flag (default off; the same flag gates GSI Query); when the flag is off a GSI IndexName is rejected with ValidationException. A GSI scan is cross-partition and restricted to index members via `IS_DEFINED(c.<gsiHash>)` (plus `IS_DEFINED(c.<gsiSort>)` when the GSI is composite — both key attributes must be defined for an item to be an index member); the index projection (ALL / KEYS_ONLY / INCLUDE) is resolved in-process and the projected-attribute set is enforced (Select=ALL_ATTRIBUTES is rejected on a non-ALL GSI and a ProjectionExpression referencing a non-projected attribute is rejected). ConsistentRead=true is rejected on a GSI (GSI reads are eventually consistent). An IndexName matching no index is rejected with ValidationException ("The table does not have the specified index"). |  |  |
| Parallel scan (Segment / TotalSegments) | ⛔ unsupported | — | Rejected with ValidationException. Cosmos cross-partition queries fan out internally; explicit per-segment parallelism is deferred to a later slice. |  |  |
| Legacy ScanFilter / ConditionalOperator / AttributesToGet | ⛔ unsupported | — | Legacy v1 parameters are rejected loudly with ValidationException — use FilterExpression / ProjectionExpression. |  |  |
| ReturnConsumedCapacity | ⛔ unsupported | — | Silently ignored; response omits ConsumedCapacity. |  |  |

### Behaviour differences

- Local Secondary Index Scan is emulator-verified only and not yet exercised against real Azure Cosmos DB. The scan is restricted to index members with an `IS_DEFINED(c.<lsiSort>)` guard and the index projection is applied in-process; it does not impose any ordering (Scan is unordered, matching DynamoDB). Items missing the LSI sort attribute are excluded, matching LSI sparse-index semantics. LSI ScannedCount reflects index members examined (exact for the unbounded case via the server-side count recovery; a Limit / paginated pushed-filter LSI scan inherits the same page-boundary divergence documented below for base-table scans).
- Global Secondary Index Scan is opt-in behind `DynamoDb.EnableGlobalSecondaryIndexQueries` (default off; shared with GSI Query) and is unit/emulator-verified only — not yet exercised against real Azure Cosmos DB. The GSI scan is cross-partition and restricted to index members with `IS_DEFINED(c.<gsiHash>)` (plus `IS_DEFINED(c.<gsiSort>)` for a composite GSI; both key attributes must be present for membership), and imposes no ordering (Scan is unordered). Because the proxy stores all attributes in one base container and reads the full document, it enforces the GSI projected-attribute set in-process to match DynamoDB's projected-view semantics: Select=ALL_ATTRIBUTES is rejected on a non-ALL GSI and a ProjectionExpression referencing a non-projected attribute is rejected. ConsistentRead=true is rejected (GSI reads are eventually consistent in DynamoDB). GSI ScannedCount reflects index members examined, recovered for the unbounded pushed-filter case via the same server-side count over the membership scope; the projection-staleness window inherent to DynamoDB GSIs is not modelled (the proxy reads the live base document).
- Cosmos storage-metadata system fields (`_rid`/`_self`/`_etag`/`_ts`/`_attachments`/`_lsn`/`_metadata`) are stripped from response items and never surface as DynamoDB attributes (#203). Caveat: a user attribute literally named identically is also stripped on read; the durable fix is attribute namespacing.
- Scan order is **not** stable. Cosmos cross-partition query does not guarantee a deterministic walk across partitions; DynamoDB Scan is also unordered, but specific item ordering across pages may differ.
- Multi-region Cosmos accounts honor configured `cosmos.preferredRegions` for read locality and client-side failover: Scan routes to the first available readable preferred region, then remaining readable regions, then the configured account endpoint. Failover is implemented for regional 503/408 and transport failures; emulator coverage is unavailable because the Cosmos emulator is single-region.
- When a FilterExpression is pushed into the Cosmos SQL, ScannedCount for a single complete unbounded scan (no Limit, no incoming ExclusiveStartKey, no outgoing continuation) is recovered faithfully via a server-side `SELECT VALUE COUNT(1)` over the same scope minus the pushed filter. The recovery is deliberately skipped on a paginated pushed-filter scan — both for a Limit and for any page reached via ExclusiveStartKey — because the whole-scope aggregate cannot reproduce a single page's pre-filter boundary; on those pages ScannedCount falls back to the streamed (post-prefilter) count and may be lower than DynamoDB's. The proxy preserves the Cosmos page boundary in that case: it returns after the first non-empty page and surfaces the Cosmos continuation as LastEvaluatedKey, rather than topping up matches across pages with a drifted page boundary. If the count aggregate cannot be read it falls back to the streamed count. Because the recovery aggregate is a second round-trip, a concurrent write between the data pass and the count means the recovered ScannedCount is from a marginally different snapshot than Count — real DynamoDB scans-then-filters in one pass and cannot exhibit this; it is accepted as best-effort.
- Cosmos binary JSON response bodies are supported only when explicitly enabled with `DynamoDb.CosmosBinaryResponses=true`; the proxy sends `x-ms-cosmos-supported-serialization-formats: CosmosBinary`, decodes `0x80` CosmosBinary scan pages back to JSON before the normal DynamoDB response transform, and falls back to the unchanged text path whenever Cosmos returns text. Emulator-unverified: the Cosmos DB Linux emulator used by CI does not emit CosmosBinary bodies.
- Scans with no FilterExpression, no ProjectionExpression, no COUNT Select, and nothing pushed into the Cosmos SQL use a *fused* streaming transform: Cosmos `Documents` are rewritten directly into the DynamoDB response envelope (`{"Items":[…],"Count":N,"ScannedCount":N[,"LastEvaluatedKey":…]}`) without materializing a per-item AttributeValue map. Output is byte-identical to the materialized path (pinned by a golden test). The path taken is observable via the `aws2azure_dynamodb_read_transform_path_total{op="scan",path="fused"|"materialized"}` counter. Filtered/projected/pushed scans and COUNT scans stay on the materialized path because they need per-item logic (in-process filter evaluation, projection). Measured ~9x faster (≈−89% CPU) and per-page managed allocation reduced from ~280 KB (100 lean items) / ~2.8 MB (1000 lean items) to a constant 168 B, on `tests/Aws2Azure.Benchmarks` `CosmosMultiItemTransformBenchmarks` (AMD EPYC 7763, .NET 10); benchmark numbers are host-bound, not real-Azure RU.
- When the fused path runs on a CosmosBinary page it streams each page straight off the binary body via `CosmosBinaryReader` directly into the response writer — a pure binary-direct streaming transform with no binary→text page decode and no decode-to-text fallback. A marker the streaming reader declines propagates as an error, exactly like a malformed text page would; the fused path keeps no per-page scratch buffer or rollback. This deliberately diverges from the single-document and materialized read paths, which retain a per-document decode-to-text fallback, because the fused path shares one cross-page response writer that cannot be rolled back. The decode path is observable via `aws2azure_dynamodb_read_decode_path_total{op="scan",path=binary|text}`. Measured −35…−40% CPU and half the managed allocation (336 B → 168 B) vs decode-to-text-fused on `tests/Aws2Azure.Benchmarks` `CosmosFusedBinaryDirectBenchmarks` (AMD EPYC 7763, .NET 10; host-bound, not real-Azure RU). The emulator never emits CosmosBinary, so the binary-direct fused path is exercised against real Azure only.
- Filtered/projected/pushed scans and COUNT scans stay on the materialized path, but when Cosmos returns a CosmosBinary page the per-document AttributeValue maps are built straight off the binary body via `CosmosBinaryReader` (no binary→text page decode + full-page JsonDocument DOM); a marker the streaming reader declines makes the whole page atomically fall back to decode-to-text. The decode path is observable via `aws2azure_dynamodb_read_decode_path_total{op="scan",path=binary|fallback|text}`. The emulator never emits CosmosBinary, so the binary-direct path is exercised against real Azure only.
- Cosmos 429 (throttled) is surfaced as DynamoDB ProvisionedThroughputExceededException — expect this often on large scans.
- RU cost is significant for cross-partition scans; the proxy does no rate-limiting beyond what Cosmos imposes.
- Smoke-verified against the Cosmos DB Linux emulator (vNext preview) via Testcontainers; not yet exercised against real Azure Cosmos DB.

### References

- <https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_Scan.html>

## TagResource

- **Status:** ✅ implemented
- **Azure equivalent:** `Azure Cosmos DB account/resource tags (control plane)`

### Sub-features

| Name | Status | Real-Azure | Notes | Gap | Workaround |
|---|---|---|---|---|---|
| Tag persistence and round-trip | ✅ implemented | — | Persists table tags in the aws2azure TableMetadata sidecar document inside the Cosmos container and returns them from ListTagsOfResource. |  |  |
| Merge duplicate keys | ✅ implemented | — | New values overwrite existing keys while preserving unrelated tags; the final tag set is limited to 50 tags. |  |  |

### Behaviour differences

- Tags are stored in the aws2azure TableMetadata sidecar document inside the table's Cosmos container, not as Azure control-plane resource tags.
- Persisted tags have no effect on Azure billing, routing, Azure Policy, or Azure-native tag queries.
- Acceptance has unit-test coverage against the Cosmos REST test double; real-Azure validation is pending.

### References

- <https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_TagResource.html>

## TransactGetItems

- **Status:** 🟡 partial
- **Azure equivalent:** `Azure Cosmos DB (Core SQL API)`

### Sub-features

| Name | Status | Real-Azure | Notes | Gap | Workaround |
|---|---|---|---|---|---|
| Per-item strong-consistency reads | ✅ implemented | — | Each Get fans out to a Cosmos GET with `x-ms-consistency-level: Strong`. Bounded parallelism (16) keeps the response sub-second on small batches. |  |  |
| 100-item-per-call cap | ✅ implemented | — | Requests over 100 items rejected with ValidationException. |  |  |
| Positional Responses alignment | ✅ implemented | — | Missing items emit an empty `{}` entry to preserve index alignment with TransactItems (matches DynamoDB). |  |  |
| ProjectionExpression / ExpressionAttributeNames (per item) | ✅ implemented | ✅ | Top-level attribute names, `#alias` references, and nested document paths (`a.b`, `a[0]`, `a.b[1]`) honoured. Projected maps keep only referenced members; projected lists compact to referenced indices (ascending); non-existent/type-mismatched paths omitted; overlapping paths rejected with ValidationException. |  |  |
| TransactionCanceledException on Cosmos error | ✅ implemented | — | Any non-2xx, non-404 from a fan-out call cancels the transaction with `TransactionCanceledException`. `CancellationReasons` is aligned positionally — `None` for successful items, the Cosmos-derived AWS code (e.g. `ProvisionedThroughputExceededException`, `InternalServerError`) for failed ones. |  |  |
| ReturnConsumedCapacity | ⛔ unsupported | — | Silently ignored; response omits ConsumedCapacity. |  |  |

### Behaviour differences

- Cosmos storage-metadata system fields (`_rid`/`_self`/`_etag`/`_ts`/`_attachments`/`_lsn`/`_metadata`) are stripped from response items and never surface as DynamoDB attributes (#203). Caveat: a user attribute literally named identically is also stripped on read; the durable fix is attribute namespacing.
- Key attribute values (S/B) are hex-encoded into the internal Cosmos `id`/partition-key (S → hex(UTF-8 bytes), B → hex(raw bytes), N → order-preserving numeric digit string), accepting Cosmos-forbidden characters (`/`, `\`, `?`, `#`) and fixing B byte-ordering. Effective raw key limit ~127 bytes; over-limit keys are rejected with ValidationException. **On-disk-format breaking change** vs earlier builds. See PutItem for the full rationale.
- Not a true cross-container ACID read — each fan-out call sees Cosmos' latest committed value independently. For items in the same logical partition this is functionally equivalent to DynamoDB; cross-partition or cross-container reads can in theory observe writes that committed mid-fan-out (DynamoDB internally serializes the entire transaction).
- Each per-item point read builds the AttributeValue map straight off a CosmosBinary body via `CosmosBinaryReader` (no binary→text decode + JsonDocument DOM) when `DynamoDb.CosmosBinaryResponses=true`, falling back to decode-to-text on an unsupported marker or a text body; observable on `aws2azure_dynamodb_read_decode_path_total{op="transactget",path=binary|fallback|text}`. The emulator never emits CosmosBinary, so the binary-direct path is exercised against real Azure only.
- Only validated against scripted Cosmos REST fakes; not yet exercised against real Azure Cosmos.

### References

- <https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_TransactGetItems.html>

## TransactWriteItems

- **Status:** 🟡 partial
- **Azure equivalent:** `Azure Cosmos DB (Core SQL API) — single-partition stored-procedure transaction`

### Sub-features

| Name | Status | Real-Azure | Notes | Gap | Workaround |
|---|---|---|---|---|---|
| Atomic Put / Delete / ConditionCheck | ✅ implemented | — | All operations run inside one Cosmos stored procedure (`atomicTransactWrite_v2`), which executes as a single server-side ACID transaction. Either every write commits or none do (any write error throws and Cosmos rolls back the sproc). |  |  |
| ConditionExpression (Put / Delete / ConditionCheck) | ✅ implemented | — | Conditions are evaluated server-side in the sproc before any write. If ANY condition fails, no writes are performed and the call returns `TransactionCanceledException` with positional `CancellationReasons`. `ConditionCheck.ConditionExpression` is required (matches DynamoDB). Top-level / `#alias` attribute paths; same expression surface as PutItem/DeleteItem conditions. A condition whose ROOT attribute is a reserved Cosmos field (`id`, `ttl`, or any `_a2a` name — these are shadow-encoded or injected by storage) is rejected with `ValidationException`: the sproc evaluates against the raw Cosmos document where those keys do not hold the user's value, and unlike single-item conditional writes there is no in-process fallback to evaluate them faithfully. |  |  |
| Update | ⛔ unsupported | — | Atomic in-transaction `Update` is rejected with `ValidationException`. Use `Put` to overwrite the whole item, or perform the update outside the transaction. Documented gap — server-side UpdateExpression execution inside the multi-op sproc is a planned fast-follow. |  |  |
| 100-item-per-call cap | ✅ implemented | — | Requests over 100 items rejected with ValidationException. |  |  |
| Positional CancellationReasons | ✅ implemented | — | On condition failure, `CancellationReasons` is aligned positionally with TransactItems — `None` for items whose condition passed, `ConditionalCheckFailed` for those that failed. |  |  |
| ClientRequestToken (idempotency) | ⛔ unsupported | — | Accepted but not honoured — aws2azure has no idempotency store, so a retried token is re-executed rather than de-duplicated. |  |  |
| ReturnConsumedCapacity / ReturnItemCollectionMetrics | ⛔ unsupported | — | Silently ignored; response omits ConsumedCapacity / ItemCollectionMetrics. |  |  |

### Behaviour differences

- **Single table + single partition key only.** A Cosmos stored-procedure transaction is scoped to one container and one logical partition. Operations spanning more than one table, or more than one partition-key value, are rejected with `ValidationException`. DynamoDB allows up to 100 writes across multiple tables and partitions with full ACID — that surface is not reproducible on Cosmos.
- Duplicate operations on the same item (same table + key) are rejected with `ValidationException` (matches DynamoDB's `cannot include multiple operations on one item`).
- Stored procedures must be enabled (DynamoDB stored-procedure mode `Preferred` or `Required`). With sprocs disabled the request is rejected with `ValidationException` — there is no honest non-atomic fallback for a transaction.
- `Update` is rejected (see sub-features) — atomic transactional update is not yet implemented.
- Large transactions (approaching 100 operations) may exceed the Cosmos stored-procedure execution-time / response-size budget; such calls surface as a rolled-back failure rather than a partial commit.
- Key attribute values are hex/numeric-encoded into the internal Cosmos `id`/partition-key the same way as PutItem/DeleteItem (S → hex(UTF-8 bytes), B → hex(raw bytes), N → order-preserving digit string). See PutItem for the full rationale.
- The Cosmos linux emulator (`vnext-preview`) rejects server-side scripts (`Server-side scripts are not supported in this emulator`), so the `atomicTransactWrite_v2` stored procedure cannot be provisioned there. The C# request-validation surface (single-table / single-partition / duplicate-target / 100-item-cap rejection) is exercised against the emulator, but the **server-side JS transaction body itself can only be validated against real Azure Cosmos DB**. Integration tests that execute the sproc skip automatically when provisioning fails.
- **Validated against real Azure Cosmos DB** (serverless, Strong consistency): the sproc reads existing documents with a partition-local `SELECT * FROM c WHERE c.id = @id` query and deletes via the document's own `_self` link. An earlier body that built the read link as `getSelfLink() + 'docs/' + id` was rejected by real Cosmos (`Error creating request message`) — that RID+id mixed link is invalid; the emulator could never have caught it since it does not run sprocs. Read-your-write determinism requires an account default consistency of Strong (the proxy issues independent REST calls and does not propagate Cosmos session tokens).

### References

- <https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_TransactWriteItems.html>

## UntagResource

- **Status:** ✅ implemented
- **Azure equivalent:** `Azure Cosmos DB account/resource tags (control plane)`

### Sub-features

| Name | Status | Real-Azure | Notes | Gap | Workaround |
|---|---|---|---|---|---|
| Remove persisted tag keys | ✅ implemented | — | Removes requested keys from the aws2azure TableMetadata sidecar document and invalidates the table metadata cache. |  |  |

### Behaviour differences

- Tags are stored in the aws2azure TableMetadata sidecar document inside the table's Cosmos container, not as Azure control-plane resource tags.
- Removing tags has no effect on Azure billing, routing, Azure Policy, or Azure-native tag queries.
- Acceptance has unit-test coverage against the Cosmos REST test double; real-Azure validation is pending.

### References

- <https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_UntagResource.html>

## UpdateItem

- **Status:** 🟡 partial
- **Azure equivalent:** `Azure Cosmos DB (Core SQL API)`

### Sub-features

| Name | Status | Real-Azure | Notes | Gap | Workaround |
|---|---|---|---|---|---|
| UpdateExpression grammar (SET / REMOVE / ADD / DELETE) | ✅ implemented | — | Hand-rolled lexer + parser shared with the future Condition/Filter slice. |  |  |
| SET arithmetic (`a = a + :i`, `a = :x - :y`) | ✅ implemented | — | Decimal arithmetic preserves up to 28-29 significant digits; DynamoDB allows 38. Overflow surfaces as ValidationException. |  |  |
| SET functions `if_not_exists(path, fallback)` and `list_append(l1, l2)` | ✅ implemented | — |  |  |  |
| SET on nested paths (`addr.zip`, `items[0].name`) | ✅ implemented | — | Parent path must already exist as a map/list, matching DynamoDB. Creating a deeply-nested fresh structure requires top-level SET. |  |  |
| REMOVE on nested paths and missing attributes | ✅ implemented | — | REMOVE on a missing path is a no-op. |  |  |
| ADD on numeric attribute (create-if-missing + addition) | ✅ implemented | — |  |  |  |
| ADD / DELETE on string/number/binary sets (union / subtract) | ✅ implemented | — | Empty result set causes the attribute to be removed entirely, matching DynamoDB. |  |  |
| AttributeUpdates (legacy) PUT / DELETE / ADD | ✅ implemented | — | Normalised internally into the same UpdateExpression AST. |  |  |
| ExpressionAttributeNames / ExpressionAttributeValues (`#name`, `:value`) | ✅ implemented | — |  |  |  |
| Path overlap detection | ✅ implemented | — | Two paths in the same expression where one is a prefix of the other are rejected with ValidationException. |  |  |
| ReturnValues (NONE / ALL_OLD / UPDATED_OLD / ALL_NEW / UPDATED_NEW) | ✅ implemented | — | UPDATED_OLD/UPDATED_NEW project only the top-level attributes touched by the expression, matching AWS. |  |  |
| Create-if-missing (upsert) semantics | ✅ implemented | — | Atomic create with `If-None-Match: *` when the target item does not exist; concurrent create races surface as Cosmos 409 and are replayed by the optimistic-retry loop against the winner's state. |  |  |
| ConditionExpression / Expected / ConditionalOperator | ✅ implemented | — | Modern ConditionExpression and legacy Expected + ConditionalOperator both supported; mutual exclusion enforced with ValidationException. Evaluator covers comparisons, AND/OR/NOT, BETWEEN, IN, attribute_exists/not_exists/type, begins_with, contains, size(). Failure returns HTTP 400 ConditionalCheckFailedException; ReturnValuesOnConditionCheckFailure=ALL_OLD includes the prior item. |  |  |
| ReturnConsumedCapacity / ReturnItemCollectionMetrics | ⛔ unsupported | — | Silently ignored; response omits ConsumedCapacity / ItemCollectionMetrics. |  |  |

### Behaviour differences

- Cosmos storage-metadata system fields (`_rid`/`_self`/`_etag`/`_ts`/`_attachments`/`_lsn`/`_metadata`) are stripped from response items and never surface as DynamoDB attributes (#203). Caveat: a user attribute literally named identically is also stripped on read; the durable fix is attribute namespacing.
- Key attribute values (S/B) are hex-encoded into the internal Cosmos `id`/partition-key (S → hex(UTF-8 bytes), B → hex(raw bytes), N → order-preserving numeric digit string), accepting Cosmos-forbidden characters (`/`, `\`, `?`, `#`) and fixing B byte-ordering. Effective raw key limit ~127 bytes; over-limit keys are rejected with ValidationException. **On-disk-format breaking change** vs earlier builds. See PutItem for the full rationale.
- Atomicity is implemented as a GET → modify → PUT(If-Match) (or atomic-create with If-None-Match) loop with up to 4 retries on Cosmos 412/409. Sustained contention surfaces as InternalServerError after the retry budget is exhausted.
- Numeric arithmetic is performed with System.Decimal (28-29 significant digits) rather than DynamoDB's 38-digit precision. Operands exceeding the proxy's precision are rejected up front with ValidationException to avoid silent rounding; overflow also throws ValidationException.
- Key attributes referenced by the request are always reinforced into the resulting item — a REMOVE targeting the partition or sort key never deletes them in the stored doc.
- Cosmos 429 (throttled) is surfaced to clients as DynamoDB ProvisionedThroughputExceededException.
- Conditional / ReturnValues=NONE updates use the single-item `atomicWrite_v2` Cosmos stored procedure when stored procedures are enabled AND the expression is within the sproc's supported subset: the condition is evaluated and the UpdateExpression applied server-side in one atomic round-trip. **Validated against real Azure Cosmos DB** (Strong consistency). The v2 body fixes two defects the emulator could never catch (it does not run sprocs): (1) the read link is a partition-local `SELECT * FROM c WHERE c.id = @id` query rather than an invalid `getSelfLink() + 'docs/' + id` mixed link; (2) SET-value operands are serialised as `$k`-tagged envelopes (`lit`/`path`/`op`/`ifne`/`lap`) so the sproc resolves arithmetic (`a = a + :i`), attribute copies, `if_not_exists` and `list_append` server-side instead of storing the unresolved AST.
- The sproc executes only the slice of the expression surface it can reproduce faithfully: SET (scalar/native-map/list literals, `+`/`-` arithmetic, path copy, `if_not_exists`, `list_append`) and REMOVE, with scalar conditions (comparisons, AND/OR/NOT, `attribute_exists`/`attribute_not_exists`, `attribute_type` of S/N/BOOL/NULL/L/M, `begins_with`, BETWEEN, IN). Anything outside it — `ADD`/`DELETE` clauses, string/number/binary **sets**, **binary** values, **high-precision numbers** that do not round-trip through a double, **list-index paths** (`a[0]`), and the `size()` / `contains()` condition forms — is routed away from the sproc: under stored-procedure mode `Preferred` it falls back to the non-atomic GET → modify → PUT loop; under `Required` it fails loud rather than run divergent server-side JS. Atomic counters are still served atomically via `SET c = c + :n`.
- Smoke-verified against the Cosmos DB Linux emulator (vNext preview) via Testcontainers; the GET → modify → PUT fallback path is emulator-covered, the `atomicWrite_v2` sproc path is validated against real Azure Cosmos DB.
- When the existing item must be materialized for condition evaluation or ReturnValues (the GET → modify → PUT loop) and Cosmos returns a CosmosBinary body (opt-in `DynamoDb.CosmosBinaryResponses`), the AttributeValue map is built straight off the binary body via `CosmosBinaryReader` (no binary→text decode + JsonDocument DOM). A marker the streaming reader does not fast-path falls back to the decode-to-text path; a text body uses it directly. The chosen path is observable on `aws2azure_dynamodb_read_decode_path_total{op="update",path=binary|fallback|text}`. The emulator never emits CosmosBinary, so the binary-direct path is exercised against real Azure only.
- The standalone document write body (the create-with-If-None-Match / replace-with-If-Match in the GET → modify → PUT loop) is sent as CosmosBinary (the `0x80` format) when the opt-in `DynamoDb.CosmosBinaryRequests` is enabled (default off), encoded single-pass straight to the wire; the gateway auto-detects the marker so no negotiation header or special Content-Type is used. The sproc-embedded atomic path (`atomicWrite_v2`) keeps JSON text, since the document is embedded as a value inside the sproc parameter array. The chosen format is observable on `aws2azure_dynamodb_write_body_total{format=binary|text}`. The Cosmos DB Linux emulator neither emits nor reliably accepts CosmosBinary, so the binary write path is validated against real Azure only — confirmed parsed + indexed (text read-back + indexed query) by the nightly acceptance test.
- https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/Expressions.UpdateExpressions.html

## UpdateTimeToLive

- **Status:** 🟡 partial
- **Azure equivalent:** `Azure Cosmos DB container `defaultTtl` / per-item `ttl``

### Sub-features

| Name | Status | Real-Azure | Notes | Gap | Workaround |
|---|---|---|---|---|---|
| TTL enable | ✅ implemented | — | Arms the Cosmos container by setting `defaultTtl = -1` (TTL enabled, no blanket expiry) and persists the DynamoDB attribute name in the proxy's per-table metadata sidecar. From that point every write path (PutItem / UpdateItem / BatchWriteItem / TransactWriteItems) translates the named attribute's absolute epoch-seconds value into Cosmos' per-item relative `ttl` (`ttl = epochAttr - now`, recomputed on every write so the absolute expiry stays correct across updates). The container replace runs FIRST, then the metadata write, so a metadata-write failure leaves a benign non-expiring state rather than silently dropping items. |  |  |
| TTL disable | ✅ implemented | — | Removes the container `defaultTtl` (Cosmos stops honouring per-item `ttl`) and clears the attribute name in metadata. Items keep any previously written `ttl` field but it becomes inert. |  |  |
| AttributeName validation | ✅ implemented | — | Rejects an enable request that omits `TimeToLiveSpecification.AttributeName` with HTTP 400; rejects an unknown table with ResourceNotFoundException. |  |  |

### Behaviour differences

- DynamoDB TTL stores an *absolute* epoch-seconds expiry in a named item attribute; Cosmos `ttl` is a *relative* duration measured from the document's `_ts`. The proxy bridges this by recomputing `ttl = epochAttr - now` on every write. Items written BEFORE TTL was enabled carry no per-item `ttl` and are not retroactively expired until they are rewritten — this differs from DynamoDB, which begins evaluating the attribute for all items as soon as TTL is enabled.
- Expiry sweep cadence differs: DynamoDB deletes expired items within ~48h of expiry; Cosmos removes them on its own background TTL sweep. Neither guarantees deletion exactly at the expiry instant — callers must not rely on read-after-expiry returning empty immediately.
- Past-due expiry (attribute value already in the past, within a 5-year guard window) is clamped to `ttl = 1` so Cosmos expires the item promptly. An expiry more than 5 years in the past is treated as non-expiring (no `ttl` written), mirroring DynamoDB's safety guard against accidental mass-deletion.
- The TTL attribute value must be a Number (epoch seconds); a non-Number value, a missing attribute, or a fractional value (floored) is handled per DynamoDB semantics — a missing/non-Number attribute simply yields no per-item `ttl`.
- A DynamoDB attribute literally named `ttl` (the most common TTL attribute name) is supported: the proxy stores it shadow-encoded (`_a2a$ttl`) so the user value round-trips while Cosmos' reserved native `ttl` field carries the computed relative duration. The proxy's injected native `ttl` is stripped from read responses. This is an on-disk-format change: an item written by an earlier build that stored a literal `ttl` attribute (unshadowed) is no longer surfaced for that attribute.
- Concurrency: arming the Cosmos container `defaultTtl` and persisting the TTL metadata are two steps, not one atomic unit. Racing concurrent enable/disable calls for the SAME table can interleave and leave the container/metadata states inconsistent (e.g. metadata disabled while the container stays armed). Accepted limitation — TTL is a rare control-plane op and a single DynamoDB client serialises UpdateTimeToLive per table (real DynamoDB uses transient ENABLING/DISABLING states); cross-sidecar coordination is out of scope.
- Validated against real Azure Cosmos DB (container `defaultTtl` armed, per-item `ttl` written and read back); background expiry timing is not asserted in tests.

### References

- <https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_UpdateTimeToLive.html>
- <https://learn.microsoft.com/en-us/azure/cosmos-db/nosql/time-to-live>

