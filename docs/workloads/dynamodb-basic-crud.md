# DynamoDB basic CRUD profile

This profile covers `CreateTable`, `DescribeTable`, `PutItem`, `GetItem`,
`UpdateItem`, `DeleteItem`, and `DeleteTable` against Azure Cosmos DB for NoSQL.
It is a `candidate`: operation compatibility and real-Azure seals pass, while
production-shaped load, rollback, and SLO qualification remain outstanding.

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

## Initial SLO contract

The candidate contract requires a 300-second production-shaped real-Azure run
with at least 300 successful samples per required scenario, zero accepted
failures, and three distinct comparable runs before a capacity threshold can be
reviewed. Representative load must report throughput, p95, p99, and throttle
rate alongside the fixed operation mix and Cosmos capacity/topology.

The blocking throughput or latency threshold is intentionally unresolved.
The repository does not yet expose a DynamoDB sealed-load runner, so the
profile must remain `candidate` and no qualification artifact may be committed.
Do not copy emulator baselines or feature-specific A/B measurements into an
SLO. A future qualification change must add the production-shaped runner and
review at least three comparable sealed real-Azure runs before resolving the
capacity gate.
