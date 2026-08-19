# dynamodb design gap / Transaction execution has one configured Cosmos authority {#design-gap-dynamodb-transaction-execution-has-one-configured-cosmos-authority}

[← Design-gap index](../../design-gaps.md)

- **Capability ID:** `design-gap:dynamodb:transaction-execution-has-one-configured-cosmos-authority`
- **Status:** 🔵 by design

Cosmos stored procedures execute through a writable region. To preserve one atomic snapshot and one durable ClientRequestToken history, every TransactGetItems/TransactWriteItems execution uses one deployment-stable regional authority. On a multi-write account this is exactly target.preferredRegions[0], not the first currently available match. The proxy never replays a transaction in a second independently writable region or through the dynamically routed global account endpoint.

**Impact.** Multi-write deployments must configure the same first preferred region on every replica and binding that targets the same data. Losing that region makes transactions unavailable even if another write region remains healthy; this is the availability cost of preserving one transaction/idempotency authority. Non-transactional routing and read failover are unchanged.

**Workaround.** Prefer a single-write Cosmos account for this profile, or configure an explicit preferredRegions list whose first entry is the intended transaction authority. Restore that region before retrying. Change the first entry only as a coordinated migration after outstanding 10-minute idempotency windows and replication have converged; restarting alone never changes authority.

## References

- <https://learn.microsoft.com/azure/cosmos-db/nosql/how-to-multi-master>
- <https://learn.microsoft.com/azure/cosmos-db/nosql/stored-procedures-triggers-udfs>

