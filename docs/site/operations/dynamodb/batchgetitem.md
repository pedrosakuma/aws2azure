# dynamodb / BatchGetItem {#operation-dynamodb-batchgetitem}

[← dynamodb operation index](../../dynamodb.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:dynamodb:batchgetitem`
- **Status:** 🟡 partial
- **Disposition:** 🔵 by design
- **Azure equivalent:** `Azure Cosmos DB (Core SQL API)`
- **Real-Azure verified:** ✅ 2026-07-16 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/29473539261) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/29473539261)

## Sub-features

### Multi-table fan-out {#sub-feature-multi-table-fan-out}

- **Capability ID:** `sub-feature:dynamodb:batchgetitem:multi-table-fan-out`
- **Status:** ✅ implemented

Each table's keys are grouped by Cosmos partition key. Keys that share a partition are served by a single `SELECT * FROM c WHERE c.id IN (...)` query (one round-trip per partition); a lone key keeps the cheap `GET /docs/{id}` point read. Bounded parallelism (16 concurrent calls) keeps a single multi-partition request from saturating the proxy.

### Single-partition batching {#sub-feature-single-partition-batching}

- **Capability ID:** `sub-feature:dynamodb:batchgetitem:single-partition-batching`
- **Status:** ✅ implemented

issue #185 — a BatchGetItem whose keys all share a partition (e.g. 25 sort keys under one HASH) issues one IN-list Cosmos query instead of N point reads, draining `x-ms-continuation` as needed. Roughly an order of magnitude fewer round-trips for the common single-partition shape.

### Per-item miss semantics {#sub-feature-per-item-miss-semantics}

- **Capability ID:** `sub-feature:dynamodb:batchgetitem:per-item-miss-semantics`
- **Status:** ✅ implemented

Missing items are omitted from `Responses` (matching DynamoDB), not surfaced as errors. In the batched-query path a requested key whose document is absent from the partition is simply left out of the result set.

### Throttling → UnprocessedKeys {#sub-feature-throttling--unprocessedkeys}

- **Capability ID:** `sub-feature:dynamodb:batchgetitem:throttling--unprocessedkeys`
- **Status:** ✅ implemented

A Cosmos 429 on a point read drops that key into `UnprocessedKeys`; a 429 on a batched single-partition query drops the whole partition's keys into `UnprocessedKeys`. Either way SDK retry loops re-issue only the throttled subset and the rest of the batch still returns 200.

### ProjectionExpression (per table) {#sub-feature-projectionexpression--per-table}

- **Capability ID:** `sub-feature:dynamodb:batchgetitem:projectionexpression--per-table`
- **Status:** ✅ implemented
- **Real-Azure verified:** ✅ 2026-07-02 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/28566172080) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/28566172080)

Top-level attribute names, `#alias` references, and nested document paths (`a.b`, `a[0]`, `a.b[1]`) honoured. Projected maps keep only referenced members; projected lists compact to referenced indices (ascending); non-existent/type-mismatched paths omitted; overlapping paths rejected with ValidationException.

### ExpressionAttributeNames (per table) {#sub-feature-expressionattributenames--per-table}

- **Capability ID:** `sub-feature:dynamodb:batchgetitem:expressionattributenames--per-table`
- **Status:** ✅ implemented

### ConsistentRead (per table) {#sub-feature-consistentread--per-table}

- **Capability ID:** `sub-feature:dynamodb:batchgetitem:consistentread--per-table`
- **Status:** ✅ implemented

Sets `x-ms-consistency-level: Strong` on every Cosmos read (point read or batched query) for that table; account-level consistency cap still applies. Opt-in startup probe (`DynamoDb.ConsistencyCheck` = Warn/Required, #204) flags accounts that cannot honor Strong at boot.

### 100-item-per-call cap {#sub-feature-100-item-per-call-cap}

- **Capability ID:** `sub-feature:dynamodb:batchgetitem:100-item-per-call-cap`
- **Status:** ✅ implemented

Requests over 100 keys (across all tables) rejected with ValidationException, matching the DynamoDB hard limit.

### Duplicate-key rejection {#sub-feature-duplicate-key-rejection}

- **Capability ID:** `sub-feature:dynamodb:batchgetitem:duplicate-key-rejection`
- **Status:** ✅ implemented

Same (table, pk, id) repeated in a single call → ValidationException, matching DynamoDB.

### Legacy AttributesToGet {#sub-feature-legacy-attributestoget}

- **Capability ID:** `sub-feature:dynamodb:batchgetitem:legacy-attributestoget`
- **Status:** ⛔ unsupported
- **Disposition:** ⚫ non-goal

Rejected with ValidationException — use ProjectionExpression.

### ReturnConsumedCapacity {#sub-feature-returnconsumedcapacity}

- **Capability ID:** `sub-feature:dynamodb:batchgetitem:returnconsumedcapacity`
- **Status:** ⛔ unsupported
- **Disposition:** 🔵 by design

Silently ignored; response omits ConsumedCapacity.

## Behaviour differences

- Cosmos storage-metadata system fields (`_rid`/`_self`/`_etag`/`_ts`/`_attachments`/`_lsn`/`_metadata`) are stripped from response items and never surface as DynamoDB attributes (#203). Caveat: a user attribute literally named identically is also stripped on read; the durable fix is attribute namespacing.
- Key attribute values (S/B) are hex-encoded into the internal Cosmos `id`/partition-key (S → hex(UTF-8 bytes), B → hex(raw bytes), N → order-preserving numeric digit string), accepting Cosmos-forbidden characters (`/`, `\`, `?`, `#`) and fixing B byte-ordering. Effective raw key limit ~127 bytes; over-limit keys are rejected with ValidationException. **On-disk-format breaking change** vs earlier builds. See PutItem for the full rationale.
- 16 MB total response size cap (DynamoDB) not enforced — bounded only by the underlying Cosmos response sizes.
- Multi-region Cosmos accounts honor configured `cosmos.preferredRegions` for read locality and client-side failover: BatchGetItem point reads and batched partition queries route to the first available readable preferred region, then remaining readable regions, then the configured account endpoint. Failover is implemented for regional 503/408 and transport failures; emulator coverage is unavailable because the Cosmos emulator is single-region.
- Hard error on any single item (non-429, non-404) fails the whole batch with a single error response — DynamoDB has the same all-or-nothing semantics for non-throttle failures.
- Cosmos 429 maps to `UnprocessedKeys` rather than `ProvisionedThroughputExceededException`; matches DDB SDK retry behaviour. For a single-partition batched query, a 429 throttles the keys not yet returned (a first-page 429 throttles the whole partition group; items already fetched on earlier continuation pages stay in `Responses`).
- Cosmos binary JSON response bodies are supported only when explicitly enabled with `DynamoDb.CosmosBinaryResponses=true`; the proxy sends `x-ms-cosmos-supported-serialization-formats: CosmosBinary` on point reads and partition-batched queries, decodes `0x80` CosmosBinary bodies back to JSON before the normal DynamoDB response transform, and falls back to the unchanged text path whenever Cosmos returns text. Emulator-unverified: the Cosmos DB Linux emulator used by CI does not emit CosmosBinary bodies.
- Singleton-group point reads (`GET /docs/{id}`) build the AttributeValue map straight off a CosmosBinary body via `CosmosBinaryReader` (no binary→text decode + JsonDocument DOM), falling back to decode-to-text on an unsupported marker; observable on `aws2azure_dynamodb_read_decode_path_total{op="batchget",path=binary|fallback|text}`. The partition-batched IN query page still decodes to text (binary-direct multi-doc walk is a later increment).
- Core multi-table BatchGetItem behavior is validated against real Azure Cosmos DB; specialized projection, consistency, throttling, regional failover, and CosmosBinary paths retain their narrower per-feature coverage noted above.

## References

- <https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_BatchGetItem.html>

