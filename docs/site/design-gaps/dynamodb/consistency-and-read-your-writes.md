# dynamodb design gap / Consistency and read-your-writes {#design-gap-dynamodb-consistency-and-read-your-writes}

[← Design-gap index](../../design-gaps.md)

- **Capability ID:** `design-gap:dynamodb:consistency-and-read-your-writes`
- **Status:** 🔵 by design

Ordinary operations issue independent Cosmos REST calls and do not propagate Cosmos session tokens between requests, so read-your-write determinism depends on the account's default consistency level. Single-partition TransactGetItems is the exception: it executes as one server-side stored-procedure snapshot. ConsistentRead effectiveness for non-transactional reads remains account-dependent.

**Impact.** A DynamoDB client that assumes strong read-your-writes may observe stale reads if the Cosmos account is configured for Session/Eventual consistency. GSI reads are always eventually consistent (ConsistentRead=true is rejected, matching DynamoDB).

**Workaround.** Provision the Cosmos account with Strong (or at least Bounded Staleness) default consistency for workloads that need DynamoDB-equivalent semantics; record the chosen level per deployment.

