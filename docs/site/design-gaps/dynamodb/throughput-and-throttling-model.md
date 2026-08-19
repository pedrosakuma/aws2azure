# dynamodb design gap / Throughput and throttling model {#design-gap-dynamodb-throughput-and-throttling-model}

[← Design-gap index](../../design-gaps.md)

- **Capability ID:** `design-gap:dynamodb:throughput-and-throttling-model`
- **Status:** 🔵 by design

Capacity is Cosmos RU/s, not DynamoDB RCU/WCU. Cosmos 429 (throttled) is surfaced to clients as ProvisionedThroughputExceededException (or as UnprocessedKeys for BatchGetItem), so the AWS SDK's native retry/backoff still engages, but the underlying accounting and limits are Azure's.

**Impact.** ConsumedCapacity figures, burst behaviour, and adaptive-capacity dynamics differ from DynamoDB. Large Scans throttle differently than on DynamoDB.

**Workaround.** Size the Cosmos container/database RU/s (or use autoscale/serverless) for the workload; do not rely on DynamoDB capacity semantics.

