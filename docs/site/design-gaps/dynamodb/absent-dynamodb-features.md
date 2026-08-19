# dynamodb design gap / Absent DynamoDB features {#design-gap-dynamodb-absent-dynamodb-features}

[← Design-gap index](../../design-gaps.md)

- **Capability ID:** `design-gap:dynamodb:absent-dynamodb-features`
- **Status:** ⛔ unsupported
- **Disposition:** ⚫ non-goal

DynamoDB Streams, DAX, point-in-time recovery / on-demand backups, global tables, and auto-scaling have no in-scope Cosmos translation and are not exposed by the proxy.

**Impact.** Applications depending on these control-plane / streaming features cannot run through the proxy for those code paths.

**Workaround.** Use the corresponding Azure Cosmos capability directly (change feed, continuous backup, multi-region writes) outside the AWS wire protocol.

