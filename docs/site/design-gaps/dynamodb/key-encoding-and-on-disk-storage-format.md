# dynamodb design gap / Key encoding and on-disk storage format {#design-gap-dynamodb-key-encoding-and-on-disk-storage-format}

[← Design-gap index](../../design-gaps.md)

- **Capability ID:** `design-gap:dynamodb:key-encoding-and-on-disk-storage-format`
- **Status:** 🔵 by design

DynamoDB key attribute values are encoded into the internal Cosmos id/partition-key (S -> hex(UTF-8), B -> hex(raw), N -> order-preserving digit string) to accept Cosmos-forbidden characters and fix binary byte-ordering. Effective raw key limit is ~127 bytes.

**Impact.** Keys longer than the limit are rejected with ValidationException. The storage layout is a proxy-owned format, not portable to a raw Cosmos client, and changed across earlier builds (a breaking on-disk change).

**Workaround.** Keep key attributes within the size limit; treat the backing container as proxy-managed, not directly queryable with DynamoDB semantics.

