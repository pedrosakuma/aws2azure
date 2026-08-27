# dynamodb design gap / Multi-write conflict resolution is Last-Write-Wins {#design-gap-dynamodb-multi-write-conflict-resolution-is-last-write-wins}

[← Design-gap index](../../design-gaps.md)

- **Capability ID:** `design-gap:dynamodb:multi-write-conflict-resolution-is-last-write-wins`
- **Status:** 🔵 by design

On Cosmos accounts with multiple write regions, concurrent updates to the same item in different regions are resolved by Cosmos's default Last-Write-Wins policy on the system timestamp. A non-idempotent DynamoDB write such as UpdateItem ADD can therefore succeed twice at the proxy boundary yet still lose one increment when replication later resolves the conflict.

**Impact.** DynamoDB callers do not get a conflict error for this case. Multi-region active/active deployments can silently lose one side of a concurrent non-idempotent write, which is a correctness hazard DynamoDB operators would normally address with conditional writes or a single-writer topology rather than backend LWW.

**Workaround.** Prefer a single-write Cosmos account, or constrain all non-idempotent writes for a dataset to one authoritative writable region. If active/active writes are required, use idempotent mutations or explicit application-level version checks / conditional writes so overwrites are detectable.

## References

- <https://learn.microsoft.com/en-us/azure/cosmos-db/conflict-resolution-policies>

