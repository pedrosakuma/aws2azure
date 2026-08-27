# dynamodb design gap / Cross-partition continuation tokens can be large {#design-gap-dynamodb-cross-partition-continuation-tokens-can-be-large}

[← Design-gap index](../../design-gaps.md)

- **Capability ID:** `design-gap:dynamodb:cross-partition-continuation-tokens-can-be-large`
- **Status:** 🔵 by design

Cross-partition Scan and unordered/hash-only GSI Query pagination reuses the raw Cosmos continuation token, wrapped into DynamoDB's LastEvaluatedKey / ExclusiveStartKey sentinel shape. The proxy does not set x-ms-documentdb-responsecontinuationtoken-limitinkb, so a continuation token that encodes per-partition progress can grow to several KB.

**Impact.** AWS callers usually treat LastEvaluatedKey as a small key-attribute map. Large continuation tokens can exceed assumptions in SDK versions, custom clients, or persistence layers that cap pagination-state size much more aggressively than Cosmos does. Ordered composite-GSI queries use a separate proxy-defined continuation shape, so this size risk applies specifically to the raw-Cosmos-token paths above.

**Workaround.** Validate pagination against representative high-fan-out datasets before migration, and persist LastEvaluatedKey opaquely rather than inspecting or truncating it. If client-side token size limits are strict, prefer partition-scoped access patterns over broad Scan / GSI fan-out.

## References

- <https://learn.microsoft.com/en-us/rest/api/cosmos-db/common-cosmosdb-rest-request-headers>

