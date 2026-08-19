# dynamodb / BatchWriteItem {#operation-dynamodb-batchwriteitem}

[← dynamodb operation index](../../dynamodb.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:dynamodb:batchwriteitem`
- **Status:** 🟡 partial
- **Disposition:** 🔵 by design
- **Azure equivalent:** `Azure Cosmos DB (Core SQL API)`
- **Real-Azure verified:** ✅ 2026-07-16 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/29473539261) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/29473539261)

## Sub-features

### PutRequest fan-out {#sub-feature-putrequest-fan-out}

- **Capability ID:** `sub-feature:dynamodb:batchwriteitem:putrequest-fan-out`
- **Status:** ✅ implemented

Each PutRequest issues a Cosmos POST with `x-ms-documentdb-is-upsert: true`, matching the existing PutItem fast-path. Item attributes are stored flat on the Cosmos document (same shape as PutItem) for round-trip fidelity.

### DeleteRequest fan-out {#sub-feature-deleterequest-fan-out}

- **Capability ID:** `sub-feature:dynamodb:batchwriteitem:deleterequest-fan-out`
- **Status:** ✅ implemented

Each DeleteRequest routes to a Cosmos DELETE on the (pk, id) derived from the key. Deletes of missing items are successful no-ops — matches DynamoDB idempotency.

### Bounded parallelism {#sub-feature-bounded-parallelism}

- **Capability ID:** `sub-feature:dynamodb:batchwriteitem:bounded-parallelism`
- **Status:** ✅ implemented

Up to 10 concurrent Cosmos writes per batch (SemaphoreSlim-gated).

### 25-item-per-call cap {#sub-feature-25-item-per-call-cap}

- **Capability ID:** `sub-feature:dynamodb:batchwriteitem:25-item-per-call-cap`
- **Status:** ✅ implemented

Requests over 25 writes (across all tables) rejected with ValidationException, matching the DynamoDB hard limit.

### Item shape validation (Put) {#sub-feature-item-shape-validation--put}

- **Capability ID:** `sub-feature:dynamodb:batchwriteitem:item-shape-validation--put`
- **Status:** ✅ implemented

Every attribute in PutRequest.Item must be a single-property typed AttributeValue (same validator as PutItem). Malformed entries rejected with ValidationException before any Cosmos write.

### Duplicate-key rejection {#sub-feature-duplicate-key-rejection}

- **Capability ID:** `sub-feature:dynamodb:batchwriteitem:duplicate-key-rejection`
- **Status:** ✅ implemented

Two writes targeting the same (table, pk, id) in a single call are rejected with ValidationException — matches DynamoDB.

### Throttling → UnprocessedItems {#sub-feature-throttling--unprocesseditems}

- **Capability ID:** `sub-feature:dynamodb:batchwriteitem:throttling--unprocesseditems`
- **Status:** ✅ implemented

Cosmos 429 on any individual write surfaces the original PutRequest/DeleteRequest envelope in `UnprocessedItems`, preserving ordering within the table. Hard errors (5xx, 4xx other than 429/404) fail the whole batch.

### ReturnConsumedCapacity / ReturnItemCollectionMetrics {#sub-feature-returnconsumedcapacity---returnitemcollectionmetrics}

- **Capability ID:** `sub-feature:dynamodb:batchwriteitem:returnconsumedcapacity---returnitemcollectionmetrics`
- **Status:** ⛔ unsupported
- **Disposition:** 🔵 by design

Silently ignored; responses omit ConsumedCapacity and ItemCollectionMetrics.

## Behaviour differences

- Key attribute values (S/B) are hex-encoded into the internal Cosmos `id`/partition-key (S → hex(UTF-8 bytes), B → hex(raw bytes), N → order-preserving numeric digit string), accepting Cosmos-forbidden characters (`/`, `\`, `?`, `#`) and fixing B byte-ordering. Effective raw key limit ~127 bytes; over-limit keys are rejected with ValidationException. **On-disk-format breaking change** vs earlier builds. See PutItem for the full rationale.
- 16 MB request body cap (DynamoDB) not enforced — bounded only by Kestrel limits.
- Per-item 400 KB cap not enforced — bounded only by Cosmos document size limits.
- Cosmos 429 maps to `UnprocessedItems` rather than `ProvisionedThroughputExceededException`; matches DDB SDK retry behaviour.
- Order is preserved within a table when echoing into `UnprocessedItems`, but Cosmos calls execute in parallel — no guarantee that writes within a table commit in the order they were submitted.
- Core batch put/delete behavior is validated against real Azure Cosmos DB; throttling and specialized edge paths retain their narrower deterministic coverage.
- Each Put unit's standalone document body is sent as CosmosBinary (the `0x80` format) when the opt-in `DynamoDb.CosmosBinaryRequests` is enabled (default off); each unit is its own `POST /docs` upsert, so the gateway auto-detects the marker (no negotiation header or special Content-Type). Delete units carry no body. The chosen format is observable on `aws2azure_dynamodb_write_body_total{format=binary|text}`. The Cosmos DB Linux emulator neither emits nor reliably accepts CosmosBinary, so the binary write path is validated against real Azure only — confirmed parsed + indexed by the nightly acceptance test.
- Perf baseline throughput for this operation (~5/s for 25-item batches, see `docs/perf/baseline-latest.md`) is an inherent cost of the fan-out design, not a regression: each of the 25 Put/Delete units is its own Cosmos REST call (bounded parallelism, see 'Bounded parallelism' above), and the perf harness additionally retries `UnprocessedItems` with backoff within the measured window. Confirmed stable across historical runs (`docs/perf/history.csv`) and within the perf-gate floor (`docs/perf/baseline-reference.json`). Investigated and closed via issue #519 — do not re-flag as a regression without new evidence.

## References

- <https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_BatchWriteItem.html>

