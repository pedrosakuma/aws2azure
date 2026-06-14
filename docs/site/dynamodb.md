# dynamodb

## BatchGetItem

- **Status:** 🟡 partial
- **Azure equivalent:** `Azure Cosmos DB (Core SQL API)`

### Sub-features

| Name | Status | Notes | Gap | Workaround |
|---|---|---|---|---|
| Multi-table fan-out | ✅ implemented | Each table's keys are grouped by Cosmos partition key. Keys that share a partition are served by a single `SELECT * FROM c WHERE c.id IN (...)` query (one round-trip per partition); a lone key keeps the cheap `GET /docs/{id}` point read. Bounded parallelism (16 concurrent calls) keeps a single multi-partition request from saturating the proxy. |  |  |
| Single-partition batching | ✅ implemented | issue #185 — a BatchGetItem whose keys all share a partition (e.g. 25 sort keys under one HASH) issues one IN-list Cosmos query instead of N point reads, draining `x-ms-continuation` as needed. Roughly an order of magnitude fewer round-trips for the common single-partition shape. |  |  |
| Per-item miss semantics | ✅ implemented | Missing items are omitted from `Responses` (matching DynamoDB), not surfaced as errors. In the batched-query path a requested key whose document is absent from the partition is simply left out of the result set. |  |  |
| Throttling → UnprocessedKeys | ✅ implemented | A Cosmos 429 on a point read drops that key into `UnprocessedKeys`; a 429 on a batched single-partition query drops the whole partition's keys into `UnprocessedKeys`. Either way SDK retry loops re-issue only the throttled subset and the rest of the batch still returns 200. |  |  |
| ProjectionExpression (per table) | 🟡 partial | Top-level attribute names + `#alias` honoured. Nested paths (`a.b`, `a[0]`) rejected. |  |  |
| ExpressionAttributeNames (per table) | ✅ implemented |  |  |  |
| ConsistentRead (per table) | ✅ implemented | Sets `x-ms-consistency-level: Strong` on every Cosmos read (point read or batched query) for that table; account-level consistency cap still applies. Opt-in startup probe (`DynamoDb.ConsistencyCheck` = Warn/Required, #204) flags accounts that cannot honor Strong at boot. |  |  |
| 100-item-per-call cap | ✅ implemented | Requests over 100 keys (across all tables) rejected with ValidationException, matching the DynamoDB hard limit. |  |  |
| Duplicate-key rejection | ✅ implemented | Same (table, pk, id) repeated in a single call → ValidationException, matching DynamoDB. |  |  |
| Legacy AttributesToGet | ⛔ unsupported | Rejected with ValidationException — use ProjectionExpression. |  |  |
| ReturnConsumedCapacity | ⛔ unsupported | Silently ignored; response omits ConsumedCapacity. |  |  |

### Behaviour differences

- Cosmos storage-metadata system fields (`_rid`/`_self`/`_etag`/`_ts`/`_attachments`/`_lsn`/`_metadata`) are stripped from response items and never surface as DynamoDB attributes (#203). Caveat: a user attribute literally named identically is also stripped on read; the durable fix is attribute namespacing.
- Key attribute values (S/B) are hex-encoded into the internal Cosmos `id`/partition-key (S → hex(UTF-8 bytes), B → hex(raw bytes), N → order-preserving numeric digit string), accepting Cosmos-forbidden characters (`/`, `\`, `?`, `#`) and fixing B byte-ordering. Effective raw key limit ~127 bytes; over-limit keys are rejected with ValidationException. **On-disk-format breaking change** vs earlier builds. See PutItem for the full rationale.
- 16 MB total response size cap (DynamoDB) not enforced — bounded only by the underlying Cosmos response sizes.
- Multi-region Cosmos accounts honor configured `cosmos.preferredRegions` for read locality and client-side failover: BatchGetItem point reads and batched partition queries route to the first available readable preferred region, then remaining readable regions, then the configured account endpoint. Failover is implemented for regional 503/408 and transport failures; emulator coverage is unavailable because the Cosmos emulator is single-region.
- Hard error on any single item (non-429, non-404) fails the whole batch with a single error response — DynamoDB has the same all-or-nothing semantics for non-throttle failures.
- Cosmos 429 maps to `UnprocessedKeys` rather than `ProvisionedThroughputExceededException`; matches DDB SDK retry behaviour. For a single-partition batched query, a 429 throttles the keys not yet returned (a first-page 429 throttles the whole partition group; items already fetched on earlier continuation pages stay in `Responses`).
- Cosmos binary JSON response bodies are supported only when explicitly enabled with `DynamoDb.CosmosBinaryResponses=true`; the proxy sends `x-ms-cosmos-supported-serialization-formats: CosmosBinary` on point reads and partition-batched queries, decodes `0x80` CosmosBinary bodies back to JSON before the normal DynamoDB response transform, and falls back to the unchanged text path whenever Cosmos returns text. Emulator-unverified: the Cosmos DB Linux emulator used by CI does not emit CosmosBinary bodies.
- Only validated against scripted Cosmos REST fakes; not yet exercised against real Azure Cosmos.

### References

- <https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_BatchGetItem.html>

## BatchWriteItem

- **Status:** 🟡 partial
- **Azure equivalent:** `Azure Cosmos DB (Core SQL API)`

### Sub-features

| Name | Status | Notes | Gap | Workaround |
|---|---|---|---|---|
| PutRequest fan-out | ✅ implemented | Each PutRequest issues a Cosmos POST with `x-ms-documentdb-is-upsert: true`, matching the existing PutItem fast-path. Item attributes are stored flat on the Cosmos document (same shape as PutItem) for round-trip fidelity. |  |  |
| DeleteRequest fan-out | ✅ implemented | Each DeleteRequest routes to a Cosmos DELETE on the (pk, id) derived from the key. Deletes of missing items are successful no-ops — matches DynamoDB idempotency. |  |  |
| Bounded parallelism | ✅ implemented | Up to 10 concurrent Cosmos writes per batch (SemaphoreSlim-gated). |  |  |
| 25-item-per-call cap | ✅ implemented | Requests over 25 writes (across all tables) rejected with ValidationException, matching the DynamoDB hard limit. |  |  |
| Item shape validation (Put) | ✅ implemented | Every attribute in PutRequest.Item must be a single-property typed AttributeValue (same validator as PutItem). Malformed entries rejected with ValidationException before any Cosmos write. |  |  |
| Duplicate-key rejection | ✅ implemented | Two writes targeting the same (table, pk, id) in a single call are rejected with ValidationException — matches DynamoDB. |  |  |
| Throttling → UnprocessedItems | ✅ implemented | Cosmos 429 on any individual write surfaces the original PutRequest/DeleteRequest envelope in `UnprocessedItems`, preserving ordering within the table. Hard errors (5xx, 4xx other than 429/404) fail the whole batch. |  |  |
| ReturnConsumedCapacity / ReturnItemCollectionMetrics | ⛔ unsupported | Silently ignored; responses omit ConsumedCapacity and ItemCollectionMetrics. |  |  |

### Behaviour differences

- Key attribute values (S/B) are hex-encoded into the internal Cosmos `id`/partition-key (S → hex(UTF-8 bytes), B → hex(raw bytes), N → order-preserving numeric digit string), accepting Cosmos-forbidden characters (`/`, `\`, `?`, `#`) and fixing B byte-ordering. Effective raw key limit ~127 bytes; over-limit keys are rejected with ValidationException. **On-disk-format breaking change** vs earlier builds. See PutItem for the full rationale.
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
- Smoke-verified against the Cosmos DB Linux emulator (vNext preview) via Testcontainers; not yet exercised against real Azure Cosmos DB.

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

- Cosmos storage-metadata system fields (`_rid`/`_self`/`_etag`/`_ts`/`_attachments`/`_lsn`/`_metadata`) are stripped from response items and never surface as DynamoDB attributes (#203). Caveat: a user attribute literally named identically is also stripped on read; the durable fix is attribute namespacing.
- Missing container (table deleted mid-op) is distinguished from missing item via Cosmos `x-ms-substatus: 1003` and surfaces as ResourceNotFoundException; missing items remain idempotent successes.
- Key attribute values (S/B) are hex-encoded into the internal Cosmos `id`/partition-key (S → hex(UTF-8 bytes), B → hex(raw bytes), N → order-preserving numeric digit string), so Cosmos-forbidden characters (`/`, `\`, `?`, `#`) are accepted. The encoding is order- and prefix-preserving and invisible to clients. Effective raw key limit is ~127 bytes (hex doubles length against Cosmos' 255-char id cap); over-limit keys are rejected with ValidationException. **On-disk-format breaking change** — items written by earlier builds route under a different id.
- Cosmos 429 surfaced as DynamoDB ProvisionedThroughputExceededException — including 429 on metadata read.
- Conditional deletes use the single-item `atomicWrite_v2` Cosmos stored procedure when stored procedures are enabled and the ConditionExpression is within the sproc's supported subset (scalar comparisons, AND/OR/NOT, `attribute_exists`/`attribute_not_exists`, `attribute_type` of S/N/BOOL/NULL/L/M, `begins_with`, BETWEEN, IN): the condition is evaluated server-side and the delete is applied atomically, addressing the document by its own `_self` link from the read query (a constructed `getSelfLink() + 'docs/' + id` link is an invalid mixed link rejected by real Cosmos). **Validated against real Azure Cosmos DB** (Strong consistency). Conditions outside the subset (`size()`, `contains()`, `attribute_type` of a set/binary, binary/set literal comparisons, list-index paths) and the case where the sproc is unavailable (e.g. the emulator) fall back to the non-atomic GET → DELETE path under mode `Preferred`, or fail loud under `Required`.
- Smoke-verified against the Cosmos DB Linux emulator (vNext preview) via Testcontainers; the fallback path is emulator-covered, the `atomicWrite_v2` sproc path is validated against real Azure Cosmos DB.

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
- Smoke-verified against the Cosmos DB Linux emulator (vNext preview) via Testcontainers; not yet exercised against real Azure Cosmos DB.

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
| ConsistentRead | 🟡 partial | Mapped to Cosmos `x-ms-consistency-level: Strong` request header. Honoured only when the account's max consistency permits Strong; Session/weaker accounts silently downgrade. Opt-in startup probe (`DynamoDb.ConsistencyCheck` = Warn/Required, #204) detects such accounts at boot and warns or fails startup. |  |  |
| ProjectionExpression / AttributesToGet | ⛔ unsupported | Rejected with ValidationException pending expression parser slice (#12). |  |  |
| ExpressionAttributeNames | ⛔ unsupported |  |  |  |
| ReturnConsumedCapacity | ⛔ unsupported |  |  |  |

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
| Full DynamoDB wire-form round-trip (S/N/B/BOOL/NULL/M/L/SS/NS/BS) | ✅ implemented | Attributes stored as inferred Cosmos JSON (no `{S}`/`{N}` wrapping); number values are normalised to DynamoDB's canonical decimal form (no trailing zeros, no exponent, no `-0`) — matching real DDB's documented behaviour. Numbers whose canonical form exceeds IEEE 754 double round-trip safety are stored via the `{"_a2a:N":"<canonical>"}` envelope so 16–38 digit precision survives Cosmos storage byte-identical. |  |  |
| ConditionExpression / Expected / ConditionalOperator | ✅ implemented | Conditional path performs GET → evaluate → PUT(If-Match) or POST(If-None-Match: *) with up to 4 retries on Cosmos 412/409. Failure returns HTTP 400 ConditionalCheckFailedException with optional Item when ReturnValuesOnConditionCheckFailure=ALL_OLD. attribute_not_exists(pk) is the standard idiom for first-time create. |  |  |
| ExpressionAttributeNames / ExpressionAttributeValues | ⛔ unsupported |  |  |  |
| ReturnValues | 🟡 partial | Only NONE accepted; ALL_OLD/UPDATED_* rejected with ValidationException. |  |  |
| ReturnConsumedCapacity / ReturnItemCollectionMetrics | ⛔ unsupported | Silently ignored; response omits ConsumedCapacity / ItemCollectionMetrics. |  |  |

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

## Query

- **Status:** 🟡 partial
- **Azure equivalent:** `Azure Cosmos DB (Core SQL API)`

### Sub-features

| Name | Status | Notes | Gap | Workaround |
|---|---|---|---|---|
| KeyConditionExpression on HASH-only tables | ✅ implemented |  |  |  |
| KeyConditionExpression on HASH+RANGE tables (= / < / <= / > / >= / BETWEEN / begins_with) | ✅ implemented | Translated to a partition-scoped Cosmos SQL query against `c.pk = <hash>` with a predicate on `c.id` (which holds the formatted RANGE value). RANGE (and HASH) key values share one order-preserving, digits-only codec with storage (S → hex(UTF-8 bytes); B → hex(raw bytes); N → fixed-width sign+exponent+mantissa digit string), so ordered comparisons, BETWEEN, and begins_with on S/B sort keys compare in correct DynamoDB byte order — `begins_with` maps to an exact prefix match because hex is prefix-preserving on byte boundaries — and ordered comparisons / BETWEEN on N sort keys compare in true numeric order. `begins_with` on an N sort key is rejected (ValidationException), matching real DDB. Query operands and stored ids share the codec, so they always agree. |  |  |
| FilterExpression | ✅ implemented | Pushed into the Cosmos SQL WHERE clause where safe; the remainder is evaluated in-process after the Cosmos page returns. Count always reflects post-filter rows. ScannedCount reflects pre-filter rows: when nothing is pushed it is the streamed count; when a fragment is pushed (Cosmos pre-filters) a complete unbounded query recovers it with a partition-scoped server-side `SELECT VALUE COUNT(1)` over the same key scope minus the pushed filter, so it stays faithful to DynamoDB. The pushed-filter + Limit combination is a documented divergence (see behavior_differences). Predicates supported: comparison (=, <, <=, >, >=), BETWEEN, IN, attribute_exists/not_exists/type, begins_with, contains, AND/OR/NOT. Pushdown carve-outs (these stay residual): `<>` on any path (DDB cross-type semantics), ordered comparisons / BETWEEN on B (base64 lexical order ≠ underlying byte order), begins_with on B, size(), nested paths whose first segment matches the reserved `_a2a:` envelope prefix. Numeric equality (=) and IN push a hybrid IS_NUMBER / `StringToNumber(_a2a:N)` branch as a *prefilter only* — false negatives are impossible by construction (envelope values cannot exactly equal a round-trippable parameter) and the client-side evaluator re-checks the exact canonical string anyway. Numeric ordered comparisons (<, <=, >, >=) and BETWEEN widen the envelope branch to `IS_DEFINED(_a2a:N)` so every envelope-stored row reaches the residual evaluator — otherwise `StringToNumber` rounding could false-negative boundary values. |  |  |
| ProjectionExpression | 🟡 partial | Top-level attributes and `#alias` references are honoured. Nested paths (`a.b`, `a[0]`) are not yet supported and are rejected with ValidationException. |  |  |
| ExpressionAttributeNames / ExpressionAttributeValues | ✅ implemented |  |  |  |
| Limit | ✅ implemented |  |  |  |
| ExclusiveStartKey / LastEvaluatedKey | ✅ implemented | Pagination round-trips the Cosmos `x-ms-continuation` token inside a sentinel attribute `__a2a_continuation` (typed-string `S`). Most AWS SDKs treat LastEvaluatedKey as opaque and pass it back verbatim, which is what the proxy requires. |  |  |
| ScanIndexForward | ✅ implemented | Maps to `ORDER BY c.id ASC\|DESC`; only emitted for composite-key tables (hash-only Query returns at most one item). |  |  |
| ConsistentRead | ✅ implemented | Forwards `x-ms-consistency-level: Strong` for the Cosmos query when true; account-level consistency cap still applies. Opt-in startup probe (`DynamoDb.ConsistencyCheck` = Warn/Required, #204) flags accounts that cannot honor Strong at boot. |  |  |
| Select | 🟡 partial | ALL_ATTRIBUTES (default), SPECIFIC_ATTRIBUTES, and COUNT supported. ALL_PROJECTED_ATTRIBUTES requires IndexName and is rejected. |  |  |
| IndexName (GSI / LSI) | ⛔ unsupported | Querying secondary indexes is not yet supported; requests carrying IndexName are rejected with ValidationException. |  |  |
| Legacy KeyConditions / QueryFilter / ConditionalOperator | ⛔ unsupported | Legacy v1 parameters are rejected loudly with ValidationException — use KeyConditionExpression / FilterExpression. |  |  |
| ReturnConsumedCapacity | ⛔ unsupported | Silently ignored; response omits ConsumedCapacity. |  |  |

### Behaviour differences

- Cosmos storage-metadata system fields (`_rid`/`_self`/`_etag`/`_ts`/`_attachments`/`_lsn`/`_metadata`) are stripped from response items and never surface as DynamoDB attributes (#203). Caveat: a user attribute literally named identically is also stripped on read; the durable fix is attribute namespacing.
- Sort-key ordering follows the order-preserving key codec: S/B sort keys compare in true byte order and N sort keys compare in true numeric order (numerically-equal forms like `42`/`42.0` collapse to one id). `begins_with` is only valid on S/B sort keys; on an N sort key it is rejected with ValidationException, matching real DDB.
- Every Query is partition-scoped — there is no cross-partition fan-out — matching DynamoDB's single-partition guarantee.
- Multi-region Cosmos accounts honor configured `cosmos.preferredRegions` for read locality and client-side failover: Query routes to the first available readable preferred region, then remaining readable regions, then the configured account endpoint. Failover is implemented for regional 503/408 and transport failures; emulator coverage is unavailable because the Cosmos emulator is single-region.
- Cosmos binary JSON response bodies are supported only when explicitly enabled with `DynamoDb.CosmosBinaryResponses=true`; the proxy sends `x-ms-cosmos-supported-serialization-formats: CosmosBinary`, decodes `0x80` CosmosBinary query pages back to JSON before the normal DynamoDB response transform, and falls back to the unchanged text path whenever Cosmos returns text. Emulator-unverified: the Cosmos DB Linux emulator used by CI does not emit CosmosBinary bodies.
- When a FilterExpression is pushed into the Cosmos SQL, ScannedCount for a single complete unbounded query (no Limit, no incoming ExclusiveStartKey, no outgoing continuation) is recovered faithfully via a partition-scoped server-side `SELECT VALUE COUNT(1)` over the same key scope minus the pushed filter. The recovery is deliberately skipped on a paginated pushed-filter query — both for a Limit and for any page reached via ExclusiveStartKey — because the whole-scope aggregate cannot reproduce a single page's pre-filter boundary; on those pages ScannedCount falls back to the streamed (post-prefilter) count and may be lower than DynamoDB's. The proxy preserves the Cosmos page boundary in that case: it returns after the first non-empty page and surfaces the Cosmos continuation as LastEvaluatedKey, rather than topping up matches across pages with a drifted page boundary. If the count aggregate cannot be read it falls back to the streamed count. Because the recovery aggregate is a second round-trip, a concurrent write between the data pass and the count means the recovered ScannedCount is from a marginally different snapshot than Count — real DynamoDB scans-then-filters in one pass and cannot exhibit this; it is accepted as best-effort.
- Cosmos 429 (throttled) is surfaced as DynamoDB ProvisionedThroughputExceededException.
- Smoke-verified against the Cosmos DB Linux emulator (vNext preview) via Testcontainers; not yet exercised against real Azure Cosmos DB.

### References

- <https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_Query.html>

## Scan

- **Status:** 🟡 partial
- **Azure equivalent:** `Azure Cosmos DB (Core SQL API)`

### Sub-features

| Name | Status | Notes | Gap | Workaround |
|---|---|---|---|---|
| Full-table scan | ✅ implemented | Translated to a cross-partition Cosmos SQL query (`x-ms-documentdb-query-enablecrosspartition: true`). Every Scan is an O(N) walk of the container — expensive in RU. |  |  |
| FilterExpression | ✅ implemented | Pushed into the Cosmos SQL WHERE clause where safe; the remainder is evaluated in-process after each Cosmos page returns. Count always reflects post-filter rows. ScannedCount reflects pre-filter rows: when nothing is pushed it is the streamed count; when a fragment is pushed (Cosmos pre-filters at the storage layer) a complete unbounded pass recovers it with a server-side `SELECT VALUE COUNT(1)` over the same scope minus the pushed filter, so it stays faithful to DynamoDB. The pushed-filter + Limit combination is a documented divergence (see behavior_differences). Same pushdown carve-outs as Query: `<>`, ordered comparisons / BETWEEN / begins_with on B, size(), and paths whose first segment matches the reserved `_a2a:` envelope prefix stay residual. Numeric equality (=) and IN push a hybrid IS_NUMBER / `StringToNumber(_a2a:N)` branch as a *prefilter only* (false negatives impossible by construction; client-side evaluator re-checks the exact canonical string anyway). Numeric ordered comparisons (<, <=, >, >=) and BETWEEN widen the envelope branch to `IS_DEFINED(_a2a:N)` so every envelope-stored row reaches the residual evaluator — otherwise `StringToNumber` rounding could false-negative boundary values. |  |  |
| ProjectionExpression | 🟡 partial | Top-level attributes and `#alias` references are honoured. Nested paths (`a.b`, `a[0]`) are not yet supported and are rejected with ValidationException. |  |  |
| ExpressionAttributeNames / ExpressionAttributeValues | ✅ implemented |  |  |  |
| Limit | ✅ implemented | Caps the *scanned* (pre-filter) row count when the filter is residual-only; pageSize is sized to the remaining evaluation budget so the per-page continuation never skips rows. When a FilterExpression is pushed (fully or partially) into the Cosmos SQL, Cosmos pre-filters at the storage layer; an unbounded scan recovers the faithful ScannedCount via a server-side count, but a Limit cannot be reconciled with server-side pre-filtering — see behavior_differences for the page-boundary trade-off. |  |  |
| ExclusiveStartKey / LastEvaluatedKey | ✅ implemented | Pagination round-trips the Cosmos `x-ms-continuation` token inside a sentinel attribute `__a2a_continuation` (typed-string `S`). Most AWS SDKs treat LastEvaluatedKey as opaque and pass it back verbatim. |  |  |
| ConsistentRead | ✅ implemented | Forwards `x-ms-consistency-level: Strong` for the Cosmos query when true; account-level consistency cap still applies. Opt-in startup probe (`DynamoDb.ConsistencyCheck` = Warn/Required, #204) flags accounts that cannot honor Strong at boot. |  |  |
| Select | 🟡 partial | ALL_ATTRIBUTES (default), SPECIFIC_ATTRIBUTES, and COUNT supported. ALL_PROJECTED_ATTRIBUTES requires IndexName and is rejected. |  |  |
| IndexName (GSI / LSI) | ⛔ unsupported | Scanning secondary indexes is not yet supported; requests carrying IndexName are rejected with ValidationException. |  |  |
| Parallel scan (Segment / TotalSegments) | ⛔ unsupported | Rejected with ValidationException. Cosmos cross-partition queries fan out internally; explicit per-segment parallelism is deferred to a later slice. |  |  |
| Legacy ScanFilter / ConditionalOperator / AttributesToGet | ⛔ unsupported | Legacy v1 parameters are rejected loudly with ValidationException — use FilterExpression / ProjectionExpression. |  |  |
| ReturnConsumedCapacity | ⛔ unsupported | Silently ignored; response omits ConsumedCapacity. |  |  |

### Behaviour differences

- Cosmos storage-metadata system fields (`_rid`/`_self`/`_etag`/`_ts`/`_attachments`/`_lsn`/`_metadata`) are stripped from response items and never surface as DynamoDB attributes (#203). Caveat: a user attribute literally named identically is also stripped on read; the durable fix is attribute namespacing.
- Scan order is **not** stable. Cosmos cross-partition query does not guarantee a deterministic walk across partitions; DynamoDB Scan is also unordered, but specific item ordering across pages may differ.
- Multi-region Cosmos accounts honor configured `cosmos.preferredRegions` for read locality and client-side failover: Scan routes to the first available readable preferred region, then remaining readable regions, then the configured account endpoint. Failover is implemented for regional 503/408 and transport failures; emulator coverage is unavailable because the Cosmos emulator is single-region.
- When a FilterExpression is pushed into the Cosmos SQL, ScannedCount for a single complete unbounded scan (no Limit, no incoming ExclusiveStartKey, no outgoing continuation) is recovered faithfully via a server-side `SELECT VALUE COUNT(1)` over the same scope minus the pushed filter. The recovery is deliberately skipped on a paginated pushed-filter scan — both for a Limit and for any page reached via ExclusiveStartKey — because the whole-scope aggregate cannot reproduce a single page's pre-filter boundary; on those pages ScannedCount falls back to the streamed (post-prefilter) count and may be lower than DynamoDB's. The proxy preserves the Cosmos page boundary in that case: it returns after the first non-empty page and surfaces the Cosmos continuation as LastEvaluatedKey, rather than topping up matches across pages with a drifted page boundary. If the count aggregate cannot be read it falls back to the streamed count. Because the recovery aggregate is a second round-trip, a concurrent write between the data pass and the count means the recovered ScannedCount is from a marginally different snapshot than Count — real DynamoDB scans-then-filters in one pass and cannot exhibit this; it is accepted as best-effort.
- Cosmos binary JSON response bodies are supported only when explicitly enabled with `DynamoDb.CosmosBinaryResponses=true`; the proxy sends `x-ms-cosmos-supported-serialization-formats: CosmosBinary`, decodes `0x80` CosmosBinary scan pages back to JSON before the normal DynamoDB response transform, and falls back to the unchanged text path whenever Cosmos returns text. Emulator-unverified: the Cosmos DB Linux emulator used by CI does not emit CosmosBinary bodies.
- Cosmos 429 (throttled) is surfaced as DynamoDB ProvisionedThroughputExceededException — expect this often on large scans.
- RU cost is significant for cross-partition scans; the proxy does no rate-limiting beyond what Cosmos imposes.
- Smoke-verified against the Cosmos DB Linux emulator (vNext preview) via Testcontainers; not yet exercised against real Azure Cosmos DB.

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

- Cosmos storage-metadata system fields (`_rid`/`_self`/`_etag`/`_ts`/`_attachments`/`_lsn`/`_metadata`) are stripped from response items and never surface as DynamoDB attributes (#203). Caveat: a user attribute literally named identically is also stripped on read; the durable fix is attribute namespacing.
- Key attribute values (S/B) are hex-encoded into the internal Cosmos `id`/partition-key (S → hex(UTF-8 bytes), B → hex(raw bytes), N → order-preserving numeric digit string), accepting Cosmos-forbidden characters (`/`, `\`, `?`, `#`) and fixing B byte-ordering. Effective raw key limit ~127 bytes; over-limit keys are rejected with ValidationException. **On-disk-format breaking change** vs earlier builds. See PutItem for the full rationale.
- Not a true cross-container ACID read — each fan-out call sees Cosmos' latest committed value independently. For items in the same logical partition this is functionally equivalent to DynamoDB; cross-partition or cross-container reads can in theory observe writes that committed mid-fan-out (DynamoDB internally serializes the entire transaction).
- Only validated against scripted Cosmos REST fakes; not yet exercised against real Azure Cosmos.

### References

- <https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_TransactGetItems.html>

## TransactWriteItems

- **Status:** 🟡 partial
- **Azure equivalent:** `Azure Cosmos DB (Core SQL API) — single-partition stored-procedure transaction`

### Sub-features

| Name | Status | Notes | Gap | Workaround |
|---|---|---|---|---|
| Atomic Put / Delete / ConditionCheck | ✅ implemented | All operations run inside one Cosmos stored procedure (`atomicTransactWrite_v2`), which executes as a single server-side ACID transaction. Either every write commits or none do (any write error throws and Cosmos rolls back the sproc). |  |  |
| ConditionExpression (Put / Delete / ConditionCheck) | ✅ implemented | Conditions are evaluated server-side in the sproc before any write. If ANY condition fails, no writes are performed and the call returns `TransactionCanceledException` with positional `CancellationReasons`. `ConditionCheck.ConditionExpression` is required (matches DynamoDB). Top-level / `#alias` attribute paths; same expression surface as PutItem/DeleteItem conditions. |  |  |
| Update | ⛔ unsupported | Atomic in-transaction `Update` is rejected with `ValidationException`. Use `Put` to overwrite the whole item, or perform the update outside the transaction. Documented gap — server-side UpdateExpression execution inside the multi-op sproc is a planned fast-follow. |  |  |
| 100-item-per-call cap | ✅ implemented | Requests over 100 items rejected with ValidationException. |  |  |
| Positional CancellationReasons | ✅ implemented | On condition failure, `CancellationReasons` is aligned positionally with TransactItems — `None` for items whose condition passed, `ConditionalCheckFailed` for those that failed. |  |  |
| ClientRequestToken (idempotency) | ⛔ unsupported | Accepted but not honoured — aws2azure has no idempotency store, so a retried token is re-executed rather than de-duplicated. |  |  |
| ReturnConsumedCapacity / ReturnItemCollectionMetrics | ⛔ unsupported | Silently ignored; response omits ConsumedCapacity / ItemCollectionMetrics. |  |  |

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

- Cosmos storage-metadata system fields (`_rid`/`_self`/`_etag`/`_ts`/`_attachments`/`_lsn`/`_metadata`) are stripped from response items and never surface as DynamoDB attributes (#203). Caveat: a user attribute literally named identically is also stripped on read; the durable fix is attribute namespacing.
- Key attribute values (S/B) are hex-encoded into the internal Cosmos `id`/partition-key (S → hex(UTF-8 bytes), B → hex(raw bytes), N → order-preserving numeric digit string), accepting Cosmos-forbidden characters (`/`, `\`, `?`, `#`) and fixing B byte-ordering. Effective raw key limit ~127 bytes; over-limit keys are rejected with ValidationException. **On-disk-format breaking change** vs earlier builds. See PutItem for the full rationale.
- Atomicity is implemented as a GET → modify → PUT(If-Match) (or atomic-create with If-None-Match) loop with up to 4 retries on Cosmos 412/409. Sustained contention surfaces as InternalServerError after the retry budget is exhausted.
- Numeric arithmetic is performed with System.Decimal (28-29 significant digits) rather than DynamoDB's 38-digit precision. Operands exceeding the proxy's precision are rejected up front with ValidationException to avoid silent rounding; overflow also throws ValidationException.
- Key attributes referenced by the request are always reinforced into the resulting item — a REMOVE targeting the partition or sort key never deletes them in the stored doc.
- Cosmos 429 (throttled) is surfaced to clients as DynamoDB ProvisionedThroughputExceededException.
- Conditional / ReturnValues=NONE updates use the single-item `atomicWrite_v2` Cosmos stored procedure when stored procedures are enabled AND the expression is within the sproc's supported subset: the condition is evaluated and the UpdateExpression applied server-side in one atomic round-trip. **Validated against real Azure Cosmos DB** (Strong consistency). The v2 body fixes two defects the emulator could never catch (it does not run sprocs): (1) the read link is a partition-local `SELECT * FROM c WHERE c.id = @id` query rather than an invalid `getSelfLink() + 'docs/' + id` mixed link; (2) SET-value operands are serialised as `$k`-tagged envelopes (`lit`/`path`/`op`/`ifne`/`lap`) so the sproc resolves arithmetic (`a = a + :i`), attribute copies, `if_not_exists` and `list_append` server-side instead of storing the unresolved AST.
- The sproc executes only the slice of the expression surface it can reproduce faithfully: SET (scalar/native-map/list literals, `+`/`-` arithmetic, path copy, `if_not_exists`, `list_append`) and REMOVE, with scalar conditions (comparisons, AND/OR/NOT, `attribute_exists`/`attribute_not_exists`, `attribute_type` of S/N/BOOL/NULL/L/M, `begins_with`, BETWEEN, IN). Anything outside it — `ADD`/`DELETE` clauses, string/number/binary **sets**, **binary** values, **high-precision numbers** that do not round-trip through a double, **list-index paths** (`a[0]`), and the `size()` / `contains()` condition forms — is routed away from the sproc: under stored-procedure mode `Preferred` it falls back to the non-atomic GET → modify → PUT loop; under `Required` it fails loud rather than run divergent server-side JS. Atomic counters are still served atomically via `SET c = c + :n`.
- Smoke-verified against the Cosmos DB Linux emulator (vNext preview) via Testcontainers; the GET → modify → PUT fallback path is emulator-covered, the `atomicWrite_v2` sproc path is validated against real Azure Cosmos DB.
- https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/Expressions.UpdateExpressions.html

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

