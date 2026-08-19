# dynamodb design gap / Transaction scope is single-partition, single-table {#design-gap-dynamodb-transaction-scope-is-single-partition-single-table}

[← Design-gap index](../../design-gaps.md)

- **Capability ID:** `design-gap:dynamodb:transaction-scope-is-single-partition-single-table`
- **Status:** 🔵 by design

TransactWriteItems / TransactGetItems are translated to a Cosmos DB stored-procedure transaction, which is scoped to one container and one logical partition. Operations spanning more than one table, or more than one partition-key value, are rejected with ValidationException.

**Impact.** DynamoDB's cross-table / cross-partition ACID surface (up to 100 items across tables and partitions) is not reproducible. Applications that rely on multi-entity transactions must be remodelled so all transacted items share a partition key.

**Workaround.** Co-locate transacted items under a single partition key, or fall back to idempotent application-level compensation.

## References

- <https://learn.microsoft.com/azure/cosmos-db/nosql/stored-procedures-triggers-udfs>

