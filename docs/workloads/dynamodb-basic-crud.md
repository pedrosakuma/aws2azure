# DynamoDB basic CRUD profile

This profile covers `CreateTable`, `DescribeTable`, `PutItem`, `GetItem`,
`UpdateItem`, `DeleteItem`, and `DeleteTable` against Azure Cosmos DB for NoSQL.
It is `ga`: operation compatibility, real-Azure seals, and a reviewed
production-shaped load/rollback qualification (issue #627) all pass against
three comparable sealed real-Azure runs.

## Required configuration

Use a proxy-owned Cosmos database and set
`services.dynamodb.consistencyCheck` to `Required`. At startup the proxy reads
the Cosmos account policy and fails with actionable guidance unless the default
consistency is `Strong`; Cosmos cannot strengthen a weaker account per request,
so this is required for deterministic `ConsistentRead`, read-after-write, and
conditional-write behavior.

Size Cosmos RU/s or serverless capacity for the actual request mix. DynamoDB
RCU/WCU, consumed-capacity reporting, burst, and adaptive-capacity semantics are
not reproduced. Cosmos 429 responses map to retryable
`ProvisionedThroughputExceededException`; keep AWS SDK retries bounded and
jittered.

## Keys, item limits, and stored data

String and binary keys are hex-encoded into the Cosmos `id` and partition key;
numeric keys use an order-preserving digit encoding. This accepts characters
that Cosmos forbids in raw IDs but limits string/binary key payloads to about
127 bytes after UTF-8/base64 decoding. Validate the application's largest keys
before migration. DynamoDB's wider item, expression, and numeric limits remain
subject to the operation gap docs.

The backing container is not a general-purpose Cosmos model. Item documents,
the `__aws2azure_table_meta__` sidecar, shadow/index fields, native `ttl`, and
versioned stored procedures are proxy-owned durable formats. Do not mutate them
with a raw Cosmos client. Direct writes can break key decoding, conditions,
indexes, tags, TTL, and rollback compatibility.

## Concurrency and consistency

Conditional writes are optimistic or stored-procedure-backed depending on
configuration and expression support. Real-Azure conformance requires exactly
one winner when concurrent `UpdateItem` requests race on the same condition.
Sustained contention can exhaust the bounded retry loop and surface an
`InternalServerError`; applications should retry only when their operation is
idempotent.

The profile requires read-after-write checks with `ConsistentRead=true`, proxy
restart with retained Cosmos state, deterministic throttling/timeout/503 and
retry-exhaustion checks, and sealed candidate-to-prior rollback. GSI reads are
eventually consistent and are outside this basic CRUD profile.

## Upgrade and migration procedure

For supported adjacent minor versions, follow the repository compatibility
policy: both versions must read each other's durable documents, and a writer
must not make rollback unreadable. Deploy the candidate to staging against a
clone or isolated database, run all seven operations, conditional concurrency,
restart, and rollback before production traffic moves.

Do not perform a rolling upgrade from historical builds that predate the
current encoded-key/storage layout. Export items through the old proxy's
DynamoDB wire API, create fresh tables through the new proxy, import through
`PutItem`, and verify counts and representative reads while writes remain
frozen. Cut over only after validation. If writes resume on the new tables,
rollback requires a tested reverse export/import or synchronization step before
traffic returns to the old containers; retaining stale old containers alone is
not a safe rollback. Never rewrite encoded IDs or sidecar documents in place.
A future incompatible format change must ship a separately documented,
reversible migration or a new opt-in format.

## Capabilities outside version 1

Cross-table or cross-partition transactions are not supported; only
single-table, single-partition transaction semantics can map to Cosmos stored
procedures. Streams, DAX, global tables, point-in-time recovery/on-demand
backups, auto-scaling control-plane calls, and DynamoDB RCU/WCU accounting are
outside this profile.

## SLO contract

The qualified contract requires a 300-second production-shaped real-Azure run
with at least 300 successful samples per required scenario, zero accepted
failures, and three distinct comparable runs before a capacity threshold can be
reviewed. Representative load reports throughput, p95, p99, and throttle
rate alongside the fixed operation mix and Cosmos capacity/topology.

The reviewed `representative-load-throughput` floor is **17 completions/s**
(issue #627), derived from three comparable sealed real-Azure runs against the
exact candidate sha `9fa2ec1b` / rollback baseline sha `72b093b9`: 44.55,
105.36, and 19.50 GetItem completions/s, all with zero failures across every
required scenario including rollback. `workload-load-real-azure.yml`
provisions an isolated ephemeral Cosmos DB serverless SQL account
(`deploy/realazure/dynamodb-load.bicep`) and drives the seven-operation CRUD
mix, conditional-write concurrency, Strong-consistency read-after-write,
deterministic throttling/timeout/service-unavailable/retry-exhaustion, restart,
and sealed candidate-to-prior rollback through
`DynamoDbRealAzureLoadQualificationTests`. The reviewed qualification artifact
is committed at `docs/workloads/evidence/dynamodb-basic-crud.yaml`, and the
profile's approved-runtime ledger
(`docs/workloads/approved-runtimes/dynamodb-basic-crud.yaml`) is `approved`,
rolling back to the original bootstrap baseline. Do not copy emulator
baselines or feature-specific A/B measurements into this SLO, and do not
raise the floor without a fresh set of comparable reviewed runs.

