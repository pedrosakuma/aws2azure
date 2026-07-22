# SQS FIFO messaging over AMQP profile

This profile is separate from `sqs-standard-messaging`. Its failures and
qualification evidence must never block or strengthen the standard-queue GA
profile.

FIFO queues require `transport: Amqp`. `MessageGroupId` maps to a Service Bus
session and `MessageDeduplicationId` maps to the broker message id. FIFO batch
transfers are written in request order rather than launched concurrently.

Receive and settlement are connection-affine. Receipt handles carry the bound
session id and can only settle through the live session receiver that issued
them. After proxy restart or session-link eviction, wait for the Service Bus
lock to expire and receive again; the prior receipt handle is stale.

The AMQP pool uses the Service Bus described session-filter shape, sweeps idle
session links opportunistically without a background thread, and enforces a
hard per-connection session-link cap. Capacity exhaustion is retryable rather
than allowing MessageGroupId cardinality to grow sidecar state without bound.

REST FIFO strict ordering remains structurally unsupported because the Service
Bus REST receive API cannot acquire or hold a session.
