# DynamoDB integration tests

Smoke coverage of the Phase-3 DynamoDB module against the **Azure Cosmos DB
Linux emulator (vNext preview)** running in a Testcontainer. Boots Cosmos +
the proxy in-process and exercises the AWS DynamoDB wire protocol
end-to-end.

## What's covered

| Slice | Test class                      | Operations                                            |
|------:|---------------------------------|-------------------------------------------------------|
| 1     | `DynamoDbTableLifecycleTests`   | CreateTable, DescribeTable, ListTables, DeleteTable   |
| 2 / 3 | `DynamoDbItemOpsTests`          | PutItem, GetItem, UpdateItem (SET + REMOVE), DeleteItem |
| 5     | `DynamoDbQueryTests`            | Query (KeyConditionExpression incl. BETWEEN, FilterExpression) |
| 6     | `DynamoDbScanTests`             | Scan (with and without FilterExpression)              |
| 2.x   | `DynamoDbFilterPushdownTests`   | FilterPushdownVisitor end-to-end: pushable string equality, hybrid number with `_a2a:N` envelope row, polymorphic attribute (S vs N), Limit-under-pushdown safety |

Batch (Slice 8) and transactional (Slice 9) operations are intentionally out
of scope here — those will be covered in the Phase-3 retro before declaring
any op `verified-against-real-Azure`.

## Important caveats

- The Cosmos Linux emulator **is not behaviour-equivalent to real Cosmos**.
  Throughput governance, consistency levels, indexing policy edge cases and
  the WHERE-clause vocabulary all diverge to some extent. Treat
  "passes here" as a necessary, not sufficient, signal.
- The emulator listens on plain HTTP on port 8081, so no TLS bypass is
  required. The proxy still talks Cosmos master-key HMAC end-to-end.
- The container takes ~30s to become ready; the fixture uses a log-message
  wait keyed on "System is now fully ready to accept requests".

## Running locally

```bash
docker pull mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator:vnext-preview
dotnet test tests/Aws2Azure.IntegrationTests --filter "FullyQualifiedName~DynamoDb"
```

When Docker is unavailable (some sandboxes) every test is gracefully skipped
via `Skip.IfNot(_fx.DockerAvailable, ...)`.

## CI

Runs under the existing `integration.yml` workflow — nightly, on
`workflow_dispatch`, or on PRs carrying the `run-integration` label.
