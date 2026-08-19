# dynamodb / UpdateTimeToLive {#operation-dynamodb-updatetimetolive}

[← dynamodb operation index](../../dynamodb.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:dynamodb:updatetimetolive`
- **Status:** 🟡 partial
- **Disposition:** 🔵 by design
- **Azure equivalent:** `Azure Cosmos DB container `defaultTtl` / per-item `ttl``

## Sub-features

### TTL enable {#sub-feature-ttl-enable}

- **Capability ID:** `sub-feature:dynamodb:updatetimetolive:ttl-enable`
- **Status:** ✅ implemented

Arms the Cosmos container by setting `defaultTtl = -1` (TTL enabled, no blanket expiry) and persists the DynamoDB attribute name in the proxy's per-table metadata sidecar. From that point every write path (PutItem / UpdateItem / BatchWriteItem / TransactWriteItems) translates the named attribute's absolute epoch-seconds value into Cosmos' per-item relative `ttl` (`ttl = epochAttr - now`, recomputed on every write so the absolute expiry stays correct across updates). The container replace runs FIRST, then the metadata write, so a metadata-write failure leaves a benign non-expiring state rather than silently dropping items.

### TTL disable {#sub-feature-ttl-disable}

- **Capability ID:** `sub-feature:dynamodb:updatetimetolive:ttl-disable`
- **Status:** ✅ implemented

Removes the container `defaultTtl` (Cosmos stops honouring per-item `ttl`) and clears the attribute name in metadata. Items keep any previously written `ttl` field but it becomes inert.

### AttributeName validation {#sub-feature-attributename-validation}

- **Capability ID:** `sub-feature:dynamodb:updatetimetolive:attributename-validation`
- **Status:** ✅ implemented

Rejects an enable request that omits `TimeToLiveSpecification.AttributeName` with HTTP 400; rejects an unknown table with ResourceNotFoundException.

## Behaviour differences

- DynamoDB TTL stores an *absolute* epoch-seconds expiry in a named item attribute; Cosmos `ttl` is a *relative* duration measured from the document's `_ts`. The proxy bridges this by recomputing `ttl = epochAttr - now` on every write. Items written BEFORE TTL was enabled carry no per-item `ttl` and are not retroactively expired until they are rewritten — this differs from DynamoDB, which begins evaluating the attribute for all items as soon as TTL is enabled.
- Expiry sweep cadence differs: DynamoDB deletes expired items within ~48h of expiry; Cosmos removes them on its own background TTL sweep. Neither guarantees deletion exactly at the expiry instant — callers must not rely on read-after-expiry returning empty immediately.
- Past-due expiry (attribute value already in the past, within a 5-year guard window) is clamped to `ttl = 1` so Cosmos expires the item promptly. An expiry more than 5 years in the past is treated as non-expiring (no `ttl` written), mirroring DynamoDB's safety guard against accidental mass-deletion.
- The TTL attribute value must be a Number (epoch seconds); a non-Number value, a missing attribute, or a fractional value (floored) is handled per DynamoDB semantics — a missing/non-Number attribute simply yields no per-item `ttl`.
- A DynamoDB attribute literally named `ttl` (the most common TTL attribute name) is supported: the proxy stores it shadow-encoded (`_a2a$ttl`) so the user value round-trips while Cosmos' reserved native `ttl` field carries the computed relative duration. The proxy's injected native `ttl` is stripped from read responses. This is an on-disk-format change: an item written by an earlier build that stored a literal `ttl` attribute (unshadowed) is no longer surfaced for that attribute.
- Concurrency: arming the Cosmos container `defaultTtl` and persisting the TTL metadata are two steps, not one atomic unit. Racing concurrent enable/disable calls for the SAME table can interleave and leave the container/metadata states inconsistent (e.g. metadata disabled while the container stays armed). Accepted limitation — TTL is a rare control-plane op and a single DynamoDB client serialises UpdateTimeToLive per table (real DynamoDB uses transient ENABLING/DISABLING states); cross-sidecar coordination is out of scope.
- Validated against real Azure Cosmos DB (container `defaultTtl` armed, per-item `ttl` written and read back); background expiry timing is not asserted in tests.

## References

- <https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_UpdateTimeToLive.html>
- <https://learn.microsoft.com/en-us/azure/cosmos-db/nosql/time-to-live>

