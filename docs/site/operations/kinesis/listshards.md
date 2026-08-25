# kinesis / ListShards {#operation-kinesis-listshards}

[← kinesis operation index](../../kinesis.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:kinesis:listshards`
- **Status:** 🟡 partial
- **Disposition:** 🔵 by design
- **Azure equivalent:** `Azure Event Hubs Service Bus management REST API`
- **Real-Azure verified:** ✅ 2026-07-16 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/29473539261) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/29473539261)

## Sub-features

### ExclusiveStartShardId + MaxResults pagination {#sub-feature-exclusivestartshardid--maxresults-pagination}

- **Capability ID:** `sub-feature:kinesis:listshards:exclusivestartshardid--maxresults-pagination`
- **Status:** ✅ implemented

Paginates the Event Hubs partition list and emits aws2azure NextToken cursors when more mapped shards remain.

### HMAC-signed NextToken cursors {#sub-feature-hmac-signed-nexttoken-cursors}

- **Capability ID:** `sub-feature:kinesis:listshards:hmac-signed-nexttoken-cursors`
- **Status:** ✅ implemented

Uses the Event Hubs shard iterator signing key (or an ephemeral fallback) to sign 5-minute list-shards cursors.

### AT_LATEST / FROM_TRIM_HORIZON / FROM_TIMESTAMP shard filters {#sub-feature-atlatest---fromtrimhorizon---fromtimestamp-shard-filters}

- **Capability ID:** `sub-feature:kinesis:listshards:atlatest---fromtrimhorizon---fromtimestamp-shard-filters`
- **Status:** ✅ implemented

AT_LATEST and FROM_TRIM_HORIZON are accepted as no-ops because Event Hubs always exposes the current open-partition set; FROM_TIMESTAMP is likewise a no-op because AWS requires all open shards to be returned and Event Hubs does not surface closed historical partitions.

### AFTER_SHARD_ID shard filter {#sub-feature-aftershardid-shard-filter}

- **Capability ID:** `sub-feature:kinesis:listshards:aftershardid-shard-filter`
- **Status:** ✅ implemented

Uses the fixed shard-id ordering derived from Event Hubs partition ids and applies the same exclusive lower bound as ExclusiveStartShardId.

### AT_TRIM_HORIZON shard filter {#sub-feature-attrimhorizon-shard-filter}

- **Capability ID:** `sub-feature:kinesis:listshards:attrimhorizon-shard-filter`
- **Status:** 🟡 partial
- **Disposition:** 🔵 by design

Rejected with InvalidArgumentException.

**Gap.** Event Hubs Premium and Dedicated can add partitions after hub creation, but the management/runtime APIs do not expose when each partition first became available.

**Workaround.** Use FROM_TRIM_HORIZON when callers only need the currently open shard set.

### AT_TIMESTAMP shard filter {#sub-feature-attimestamp-shard-filter}

- **Capability ID:** `sub-feature:kinesis:listshards:attimestamp-shard-filter`
- **Status:** 🟡 partial
- **Disposition:** 🔵 by design

Rejected with InvalidArgumentException.

**Gap.** Event Hubs does not expose per-partition open timestamps, so the proxy cannot determine which partitions were open at an arbitrary historical instant after a partition-count increase.

**Workaround.** Use FROM_TIMESTAMP when callers need the current open shard set from a historical timestamp forward.

## Behaviour differences

- Kinesis shards map 1:1 to Event Hubs partitions; shard ids are synthesised as shardId-<partitionId.PadLeft(12,'0')>. [conformance:field-value:Shards[].ShardId]
- HashKeyRange values are a uniform even split of the 128-bit Kinesis hash space; Event Hubs does not expose AWS-compatible hash-key assignments, and those advertised ranges do not predict the proxy's actual modulo-based PutRecord/PutRecords routing.
- NextToken is an aws2azure-specific cursor, not an AWS-issued token; it encodes stream name + last shard id and expires after 5 minutes. [conformance:field-value:NextToken]
- Real-AWS-vs-real-Azure pagination diffs can arise when the compared streams do not expose the same shard/partition count. The proxy only emits NextToken when additional mapped Event Hubs partitions remain; if the Azure-backed stream has fewer partitions than the AWS capture stream, later AWS pages may not exist and their expected NextToken will be absent. [conformance:missing-field:NextToken]
- AT_TRIM_HORIZON and AT_TIMESTAMP remain unsupported because Event Hubs can add partitions after creation in Premium/Dedicated tiers, but its APIs do not expose the per-partition open timestamps needed to answer those historical shard-topology queries.
- Core shard listing and pagination are validated against a live Azure Event Hubs namespace.
- Stream lifecycle (CreateStream / DeleteStream / IncreaseStreamRetentionPeriod) is out of scope — Event Hubs entities are provisioned out-of-band via ARM.

## References

- <https://docs.aws.amazon.com/kinesis/latest/APIReference/API_ListShards.html>
- <https://learn.microsoft.com/en-us/rest/api/eventhub/>
- <https://learn.microsoft.com/en-us/azure/event-hubs/dynamically-add-partitions>

