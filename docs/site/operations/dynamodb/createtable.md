# dynamodb / CreateTable {#operation-dynamodb-createtable}

[← dynamodb operation index](../../dynamodb.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:dynamodb:createtable`
- **Status:** ✅ implemented
- **Azure equivalent:** `Azure Cosmos DB (Core SQL API) — POST /dbs/{db}/colls`
- **Real-Azure verified:** ✅ 2026-07-16 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/29473539261) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/29473539261)

## Sub-features

### HASH key {#sub-feature-hash-key}

- **Capability ID:** `sub-feature:dynamodb:createtable:hash-key`
- **Status:** ✅ implemented

### HASH + RANGE composite key {#sub-feature-hash--range-composite-key}

- **Capability ID:** `sub-feature:dynamodb:createtable:hash--range-composite-key`
- **Status:** ✅ implemented

### PAY_PER_REQUEST + PROVISIONED billing mode (informational) {#sub-feature-payperrequest--provisioned-billing-mode--informational}

- **Capability ID:** `sub-feature:dynamodb:createtable:payperrequest--provisioned-billing-mode--informational`
- **Status:** ✅ implemented

### AttributeDefinitions round-trip via sidecar metadata {#sub-feature-attributedefinitions-round-trip-via-sidecar-metadata}

- **Capability ID:** `sub-feature:dynamodb:createtable:attributedefinitions-round-trip-via-sidecar-metadata`
- **Status:** ✅ implemented

### GlobalSecondaryIndexes (schema accepted + persisted) {#sub-feature-globalsecondaryindexes--schema-accepted--persisted}

- **Capability ID:** `sub-feature:dynamodb:createtable:globalsecondaryindexes--schema-accepted--persisted`
- **Status:** ✅ implemented

### LocalSecondaryIndexes (schema accepted + persisted) {#sub-feature-localsecondaryindexes--schema-accepted--persisted}

- **Capability ID:** `sub-feature:dynamodb:createtable:localsecondaryindexes--schema-accepted--persisted`
- **Status:** ✅ implemented

### StreamSpecification {#sub-feature-streamspecification}

- **Capability ID:** `sub-feature:dynamodb:createtable:streamspecification`
- **Status:** ⛔ unsupported
- **Disposition:** ⚫ non-goal

### SSESpecification {#sub-feature-ssespecification}

- **Capability ID:** `sub-feature:dynamodb:createtable:ssespecification`
- **Status:** ⛔ unsupported
- **Disposition:** 🔵 by design

### Tags {#sub-feature-tags}

- **Capability ID:** `sub-feature:dynamodb:createtable:tags`
- **Status:** ⛔ unsupported
- **Disposition:** ⚫ non-goal

## Behaviour differences

- Cosmos containers use a fixed /pk partition path. Composite tables synthesise pk = '<HASH>#<RANGE>'.
- ProvisionedThroughput / BillingMode values are accepted but not enforced; throughput is governed by the Cosmos account/database, not per-table.
- TableStatus is always returned as ACTIVE since Cosmos container creation is synchronous.
- On metadata-sidecar persist failure the container is best-effort deleted to avoid orphan containers.
- GSI/LSI schemas are validated (key arity, HASH/RANGE roles, LSI HASH must match the table HASH, required Projection with projection type + INCLUDE NonKeyAttributes rules and limits (<=20 per index, <=100 total, names <=255 chars), attribute-definition references, name uniqueness, service limits) and persisted into the sidecar metadata so DescribeTable, Query, and Scan can resolve the declared index shape.
- GSI/LSI ProvisionedThroughput on an index is accepted but not enforced, mirroring base-table throughput handling.
- Core table creation is validated against real Azure Cosmos DB; index execution and other partial sub-features retain their narrower coverage noted in the Query / Scan / design-gap docs.

## References

- <https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_CreateTable.html>
- <https://learn.microsoft.com/rest/api/cosmos-db/create-a-collection>

