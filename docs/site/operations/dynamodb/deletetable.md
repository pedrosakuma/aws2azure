# dynamodb / DeleteTable {#operation-dynamodb-deletetable}

[← dynamodb operation index](../../dynamodb.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:dynamodb:deletetable`
- **Status:** ✅ implemented
- **Azure equivalent:** `Azure Cosmos DB (Core SQL API) — DELETE /dbs/{db}/colls/{name}`
- **Real-Azure verified:** ✅ 2026-07-16 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/29473539261) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/29473539261)

## Sub-features

### Synchronous delete {#sub-feature-synchronous-delete}

- **Capability ID:** `sub-feature:dynamodb:deletetable:synchronous-delete`
- **Status:** ✅ implemented

### TableDescription echoed (key schema, attrs) via sidecar metadata {#sub-feature-tabledescription-echoed--key-schema-attrs--via-sidecar-metadata}

- **Capability ID:** `sub-feature:dynamodb:deletetable:tabledescription-echoed--key-schema-attrs--via-sidecar-metadata`
- **Status:** ✅ implemented

## Behaviour differences

- DynamoDB DeleteTable is asynchronous (returns DELETING). The proxy returns the same DELETING status for SDK parity even though the Cosmos delete is synchronous.
- On a non-existent table the proxy returns ResourceNotFoundException.
- DeleteTable is validated against real Azure Cosmos DB through the item-lifecycle conformance scenario.

## References

- <https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_DeleteTable.html>
- <https://learn.microsoft.com/rest/api/cosmos-db/delete-a-collection>

