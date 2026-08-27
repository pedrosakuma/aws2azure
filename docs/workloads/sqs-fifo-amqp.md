# SQS FIFO messaging over AMQP profile

This profile is separate from `sqs-standard-messaging`. Its failures and
qualification evidence must never block or strengthen that profile's own
certification, whose current live verdict is `ga` (see
[`workload-ga.json`](../site/workload-ga.json)).

FIFO queues require `transport: Amqp`. `MessageGroupId` maps to a Service Bus
session and `MessageDeduplicationId` maps to the broker message id. FIFO batch
transfers are written in request order rather than launched concurrently. A
single `ReceiveMessage` call may accumulate messages from several unlocked
`MessageGroupId`s, best-effort up to `MaxNumberOfMessages` and bounded by the
request's wait-time budget.

Receive and settlement are connection-affine. Receipt handles carry the bound
session id and can only settle through the live session receiver that issued
them. After proxy restart or session-link eviction, wait for the Service Bus
lock to expire and receive again; the prior receipt handle is stale.

The AMQP pool uses the Service Bus described session-filter shape, sweeps idle
session links opportunistically without a background thread, and enforces a
hard per-connection session-link cap. If that cap is already full before the
first acquire, `ReceiveMessage` remains a retryable capacity error; if the cap
is hit after at least one session has already been drained, the proxy returns
the partial batch rather than failing the whole call.

REST FIFO strict ordering remains structurally unsupported because the Service
Bus REST receive API cannot acquire or hold a session.
