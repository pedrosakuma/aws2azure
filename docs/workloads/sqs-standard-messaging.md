# SQS standard messaging profile

This profile covers `CreateQueue`, `GetQueueUrl`, `ListQueues`, `SendMessage`,
`ReceiveMessage`, `DeleteMessage`, and `DeleteQueue` for standard queues backed
by Azure Service Bus. `AddPermission`, `RemovePermission`, FIFO ordering, hard
purge, and batch/DLQ extensions are not required by profile version 1.

## Required deployment contract

- Use a Service Bus tier whose message-size and throughput limits cover the
  workload. SQS accepts up to 1 MiB, but Service Bus Standard has a lower
  effective message-size limit.
- Set the queue `VisibilityTimeout` to the required Service Bus `LockDuration`.
  Per-call `ReceiveMessage.VisibilityTimeout` is validated but cannot override
  the broker lock; use `ChangeMessageVisibility` when a message needs a
  different timeout after receive.
- Keep receive and settlement on the same configured transport. REST and AMQP
  receipt handles are transport-specific and cannot be exchanged.
- Use bounded AWS SDK retries for retryable throttling, timeout, and
  service-unavailable responses. Sends are not generally safe to retry without
  an application idempotency strategy.

## Behavioral qualification

Long polling uses the Service Bus server-side wait for the first message.
`MaxNumberOfMessages > 1` is a bounded proxy loop on REST and a native batch
receive on AMQP, so callers must accept fewer messages than requested.

An unacknowledged message is redelivered after its Service Bus lock expires.
`DeleteMessage` settles the exact receipt handle returned by
`ReceiveMessage`; an expired or cross-transport handle returns
`ReceiptHandleIsInvalid`. Queue topology and queued messages live in Service
Bus, so restarting the proxy does not remove them. In-flight AMQP settlement
state is connection-affine: after restart, wait for lock expiry and receive the
message again rather than reusing the old receipt handle.

Before adoption, exercise representative concurrent producers and consumers,
long-poll duration, lock expiry/redelivery, settlement, proxy restart, bounded
retry exhaustion, and the selected Service Bus capacity. The repository's
real-Azure conformance seals establish operation correctness. A sealed
real-Azure load runner (`SqsRealAzureLoadQualificationTests`, issue #626)
exercises long polling, forced redelivery, concurrent multi-consumer settlement,
throttling/timeout/service-unavailable/retry-exhaustion, restart, and rollback
— separating REST and AMQP evidence rather than blending them, and never
depending on FIFO. The profile remains `candidate` until at least three
comparable production-shaped real-Azure load runs against the exact sealed
candidate are reviewed and a production-shaped SLO is committed from that
evidence.

## Transport and excluded capabilities

REST is the simpler standard-queue path. AMQP adds native batch receive,
`VisibilityTimeout=0` abandon behavior, richer DLQ attribution, and
connection-affine settlement. Select one transport per queue and validate it in
the deployment topology.

AWS IAM queue-policy administration is not reproduced; use Azure RBAC, SAS,
network controls, and binding isolation. `PurgeQueue` is a bounded best-effort
drain rather than a hard purge. FIFO session receive is tracked separately and
is not part of this standard profile.
