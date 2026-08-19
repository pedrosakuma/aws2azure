# dynamodb / TransactGetItems {#operation-dynamodb-transactgetitems}

[← dynamodb operation index](../../dynamodb.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:dynamodb:transactgetitems`
- **Status:** 🟡 partial
- **Disposition:** 🔵 by design
- **Azure equivalent:** `Azure Cosmos DB (Core SQL API) — single-partition read-only stored-procedure snapshot`
- **Real-Azure verified:** ✅ 2026-07-27 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/30242339540) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/30242339540)

## Sub-features

### Single-table single-partition snapshot {#sub-feature-single-table-single-partition-snapshot}

- **Capability ID:** `sub-feature:dynamodb:transactgetitems:single-table-single-partition-snapshot`
- **Status:** ✅ implemented
- **Real-Azure verified:** ✅ 2026-07-27 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/30242339540) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/30242339540)

Every request is validated as one table, one logical partition, and unique item targets before item data is read. The proxy then invokes `atomicTransactGet_v1`, whose partition-local queries execute inside one read-only Cosmos stored-procedure transaction and therefore observe one coherent committed snapshot. Stored-procedure mode Preferred or Required is mandatory; there is no fan-out fallback.

### 100-item-per-call cap {#sub-feature-100-item-per-call-cap}

- **Capability ID:** `sub-feature:dynamodb:transactgetitems:100-item-per-call-cap`
- **Status:** ✅ implemented

Requests over 100 items are rejected with ValidationException.

### Positional Responses alignment {#sub-feature-positional-responses-alignment}

- **Capability ID:** `sub-feature:dynamodb:transactgetitems:positional-responses-alignment`
- **Status:** ✅ implemented

The stored procedure returns one position per requested key. Missing items emit an empty `{}` response entry, and a malformed or count-mismatched 2xx stored-procedure body fails closed as InternalServerError.

### ProjectionExpression / ExpressionAttributeNames (per item) {#sub-feature-projectionexpression---expressionattributenames--per-item}

- **Capability ID:** `sub-feature:dynamodb:transactgetitems:projectionexpression---expressionattributenames--per-item`
- **Status:** ✅ implemented
- **Real-Azure verified:** ✅ 2026-07-27 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/30242339540) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/30242339540)

Top-level attributes, aliases, and nested projection paths are applied positionally after the server-side snapshot. Every declared ExpressionAttributeNames alias must be consumed; leftovers fail with ValidationException before table metadata or stored-procedure I/O. The The final transaction qualification reverified projection behavior together with the current snapshot and validation contract.

### ReturnConsumedCapacity {#sub-feature-returnconsumedcapacity}

- **Capability ID:** `sub-feature:dynamodb:transactgetitems:returnconsumedcapacity`
- **Status:** ⛔ unsupported
- **Disposition:** 🔵 by design

Omitted or NONE is accepted. Other values are rejected with ValidationException rather than silently omitting ConsumedCapacity.

### Deployment-stable Cosmos transaction authority {#sub-feature-deployment-stable-cosmos-transaction-authority}

- **Capability ID:** `sub-feature:dynamodb:transactgetitems:deployment-stable-cosmos-transaction-authority`
- **Status:** ✅ implemented

Although the stored procedure is read-only, Cosmos executes it through the write-region transaction boundary. Single-write accounts use the explicit writable regional endpoint returned by account topology. Multi-write accounts require target.preferredRegions[0]; that configured region is the authority across restarts and topology refreshes. A later preferred region is never selected for a transaction, even when the authority is absent or unavailable. The request then fails closed with a retryable AWS InternalServerError.

## Behaviour differences

- **Single table + single partition key only.** Cross-table and cross-partition reads are rejected before the snapshot stored procedure is invoked. DynamoDB can transact across tables and partitions.
- Duplicate keys in one request are rejected with ValidationException before item data is read.
- Stored procedures must be enabled. The Cosmos Linux emulator does not execute server-side scripts, so snapshot execution remains a real-Azure-only test surface.
- Key values use the proxy-owned encoded Cosmos id/partition-key format (S -> hex UTF-8, B -> hex raw bytes, N -> order-preserving digits); the effective raw key limit remains approximately 127 bytes.
- The stored-procedure response is JSON text even when CosmosBinary document responses are enabled; projected DynamoDB AttributeValue maps are reconstructed from the returned raw documents.
- Transaction snapshot provisioning/execution never fails over between independently writable Cosmos regions. For a multi-write account, target.preferredRegions[0] is a deployment contract: every replica and binding for the same data must configure the same first region. If that region is missing from discovered writable topology, or returns 403/3, 408, 503, timeout, or a transport failure, the snapshot fails retryably instead of using a later preference or the global account endpoint. Later preferred regions remain available to ordinary non-transactional routing.

## References

- <https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_TransactGetItems.html>
- <https://learn.microsoft.com/azure/cosmos-db/nosql/database-transactions-optimistic-concurrency>
- <https://learn.microsoft.com/azure/cosmos-db/nosql/stored-procedures-triggers-udfs>

