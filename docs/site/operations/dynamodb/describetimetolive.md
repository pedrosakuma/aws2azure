# dynamodb / DescribeTimeToLive {#operation-dynamodb-describetimetolive}

[← dynamodb operation index](../../dynamodb.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:dynamodb:describetimetolive`
- **Status:** 🟡 partial
- **Disposition:** 🔵 by design
- **Azure equivalent:** `Azure Cosmos DB container `defaultTtl` / per-item `ttl``

## Sub-features

### Reports ENABLED/DISABLED + AttributeName {#sub-feature-reports-enabled-disabled--attributename}

- **Capability ID:** `sub-feature:dynamodb:describetimetolive:reports-enabled-disabled--attributename`
- **Status:** ✅ implemented

Reads the proxy's per-table metadata sidecar and returns `{TimeToLiveDescription: {TimeToLiveStatus: "ENABLED"|"DISABLED", AttributeName: <name>}}`. AttributeName is echoed only when TTL is enabled, matching DynamoDB.

## Behaviour differences

- Reports the TTL state recorded by this proxy (the metadata sidecar written by `UpdateTimeToLive`). A Cosmos container whose `defaultTtl` was configured out-of-band (not via this proxy) is not reflected here, since the DynamoDB attribute name is unknown to the proxy.
- DynamoDB's transient `ENABLING` / `DISABLING` states are not surfaced; the proxy flips between ENABLED and DISABLED synchronously once the Cosmos container replace + metadata write complete.
- Validated against real Azure Cosmos DB alongside UpdateTimeToLive.

## References

- <https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_DescribeTimeToLive.html>
- <https://learn.microsoft.com/en-us/azure/cosmos-db/nosql/time-to-live>

