# dynamodb design gap / 99.999% availability depends on Cosmos account topology {#design-gap-dynamodb-99999-availability-depends-on-cosmos-account-topology}

[← Design-gap index](../../design-gaps.md)

- **Capability ID:** `design-gap:dynamodb:99999-availability-depends-on-cosmos-account-topology`
- **Status:** 🔵 by design

Cosmos's 99.999% read/write SLA is an account-provisioning property, not something the proxy can add at request time. It depends on the operator creating a multi-write account with at least two write regions, automatic failover, and Session-or-stronger default consistency.

**Impact.** A workload that is functionally compatible with dynamodb_basic_crud can still run on a single-region or Eventual-consistency Cosmos account and therefore miss the documented Cosmos SLA tier an operator may assume. The proxy does not surface or enforce this distinction during normal CRUD calls.

**Workaround.** Choose and document the Cosmos account topology per deployment. If the workload requires the 99.999% SLA tier, provision the account with the corresponding Azure prerequisites before routing production traffic through the proxy.

## References

- <https://azure.microsoft.com/en-us/support/legal/sla/cosmos-db/v1_5/>

