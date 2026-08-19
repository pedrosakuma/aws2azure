# dynamodb design gap / Secondary indexes (GSI / LSI) {#design-gap-dynamodb-secondary-indexes--gsi---lsi}

[← Design-gap index](../../design-gaps.md)

- **Capability ID:** `design-gap:dynamodb:secondary-indexes--gsi---lsi`
- **Status:** 🟡 partial
- **Disposition:** 🔵 by design

All attributes live in one base container; GSI queries are opt-in and LSI queries are always available over that container. GSI Query is a cross-partition fan-out (unlike a base-table Query's single-partition guarantee); string sort keys follow Cosmos code-point collation rather than DynamoDB UTF-8 byte order; numeric ordering relies on a synthetic order-preserving field written at item-write time.

**Impact.** Items written before the encoded-ordering field existed are excluded from ordered numeric-GSI results until rewritten (a backfill gap). Binary sort keys cannot be ordered. Index ItemCount / IndexSizeBytes / Backfilling remain unavailable for non-empty tables because there is no separate physical index resource to meter cheaply or truthfully.

**Workaround.** Keep GSI Query default-off unless the collation and live-base-document caveats are acceptable. Enable exact numeric LSI ordering only after rewriting pre-existing items to populate ordering fields.

