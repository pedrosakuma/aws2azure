# dynamodb design gap / Consistency and read-your-writes {#design-gap-dynamodb-consistency-and-read-your-writes}

[← Design-gap index](../../design-gaps.md)

- **Capability ID:** `design-gap:dynamodb:consistency-and-read-your-writes`
- **Status:** 🔵 by design

Ordinary operations issue independent Cosmos REST calls and do not propagate Cosmos session tokens between requests, so read-your-write determinism depends on the account's default consistency level. On a Session-consistency account this means separate proxy HTTP calls do not share the Cosmos session required for session-token-based read-your-writes, so they behave effectively like Eventual reads unless the account default is stronger. Single-partition TransactGetItems is the exception: it executes as one server-side stored-procedure snapshot. ConsistentRead effectiveness for non-transactional reads remains account-dependent.

**Impact.** A DynamoDB client that assumes strong read-your-writes may observe stale reads if the Cosmos account is configured for Session/Eventual consistency. Operators who choose Session because it is the cheaper Cosmos default should treat independent proxy calls as effectively Eventual for read-your-writes purposes. GSI reads are always eventually consistent (ConsistentRead=true is rejected, matching DynamoDB).

**Workaround.** Provision the Cosmos account with Strong (or at least Bounded Staleness) default consistency for workloads that need DynamoDB-equivalent semantics across independent proxy calls, and record the chosen level per deployment. If you intentionally run at Session for cost, document that cross-call read-your-writes is degraded. Full session-token propagation would require the proxy to keep and replay per-request Cosmos session state across otherwise stateless AWS calls, which conflicts with the proxy's request-per-call model and is therefore documented as a by-design limitation.

## References

- <https://learn.microsoft.com/en-us/azure/cosmos-db/how-to-manage-consistency#utilize-session-tokens>

