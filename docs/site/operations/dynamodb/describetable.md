# dynamodb / DescribeTable {#operation-dynamodb-describetable}

[← dynamodb operation index](../../dynamodb.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:dynamodb:describetable`
- **Status:** ✅ implemented
- **Azure equivalent:** `Azure Cosmos DB (Core SQL API) — GET /dbs/{db}/colls/{name} + sidecar metadata`
- **Real-Azure verified:** ✅ 2026-07-24 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/30059183242) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/30059183242)

## Sub-features

### AttributeDefinitions / KeySchema round-trip {#sub-feature-attributedefinitions---keyschema-round-trip}

- **Capability ID:** `sub-feature:dynamodb:describetable:attributedefinitions---keyschema-round-trip`
- **Status:** ✅ implemented

### BillingModeSummary echo {#sub-feature-billingmodesummary-echo}

- **Capability ID:** `sub-feature:dynamodb:describetable:billingmodesummary-echo`
- **Status:** ✅ implemented

### TableArn synthesis (azure-region pseudo-arn) {#sub-feature-tablearn-synthesis--azure-region-pseudo-arn}

- **Capability ID:** `sub-feature:dynamodb:describetable:tablearn-synthesis--azure-region-pseudo-arn`
- **Status:** ✅ implemented

### ItemCount live metric {#sub-feature-itemcount-live-metric}

- **Capability ID:** `sub-feature:dynamodb:describetable:itemcount-live-metric`
- **Status:** ✅ implemented

### TableSizeBytes live metric {#sub-feature-tablesizebytes-live-metric}

- **Capability ID:** `sub-feature:dynamodb:describetable:tablesizebytes-live-metric`
- **Status:** ✅ implemented

### GSI/LSI schema / status / projection description {#sub-feature-gsi-lsi-schema---status---projection-description}

- **Capability ID:** `sub-feature:dynamodb:describetable:gsi-lsi-schema---status---projection-description`
- **Status:** ✅ implemented

### GSI/LSI ItemCount / IndexSizeBytes / Backfilling / ProvisionedThroughput description {#sub-feature-gsi-lsi-itemcount---indexsizebytes---backfilling---provisionedthroughput-description}

- **Capability ID:** `sub-feature:dynamodb:describetable:gsi-lsi-itemcount---indexsizebytes---backfilling---provisionedthroughput-description`
- **Status:** ⛔ unsupported
- **Disposition:** 🔵 by design

## Behaviour differences

- ItemCount and TableSizeBytes are best-effort approximations sourced from Cosmos' `x-ms-resource-usage` header when Cosmos returns it. ItemCount subtracts the proxy metadata sidecar document when present; TableSizeBytes uses Cosmos' approximate document-storage accounting (KiB → bytes). When Cosmos omits that header (observed on freshly created containers), both fields are omitted rather than fabricated. This mirrors native DynamoDB's own documented behavior that these metrics are approximate and only refreshed periodically (about every six hours), not guaranteed real-time after table or item changes.
- TableArn is synthetic (region 'azure', account '000000000000'); real AWS arns carry the region + account id which are not meaningful in this deployment.
- Tables created out-of-band (no sidecar metadata) still describe but with empty attribute/key arrays.
- GSI/LSI descriptions are reconstructed from sidecar metadata over one base container. GSI IndexStatus is a synthetic ACTIVE (CreateTable-created indexes have no separate backfill lifecycle here). CreateTable reports zero index ItemCount/IndexSizeBytes for its brand-new empty table response. DescribeTable exposes index ItemCount/IndexSizeBytes only when the backing table metrics are available and identify an empty table; otherwise those fields, plus Backfilling / ProvisionedThroughput, remain omitted because the single-container model has no cheap truthful separate index-byte/accounting surface and AWS itself treats these counters as approximate.

## References

- <https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_DescribeTable.html>
- <https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_TableDescription.html>
- <https://learn.microsoft.com/rest/api/cosmos-db/get-a-collection>
- <https://learn.microsoft.com/rest/api/cosmos-db/common-cosmosdb-rest-response-headers>

