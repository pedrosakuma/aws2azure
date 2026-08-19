# sqs / PurgeQueue {#operation-sqs-purgequeue}

[← sqs operation index](../../sqs.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:sqs:purgequeue`
- **Status:** 🟡 partial
- **Disposition:** 🔵 by design
- **Azure equivalent:** `Azure Service Bus queue runtime REST API — emulated via drain-loop of POST /{queue}/messages/head + DELETE /{queue}/messages/{id}/{lockToken}`

## Sub-features

### Drain-loop receive+delete {#sub-feature-drain-loop-receivedelete}

- **Capability ID:** `sub-feature:sqs:purgequeue:drain-loop-receivedelete`
- **Status:** ✅ implemented

Peek-locks messages in bursts and DELETEs them; bounded by a 60s wall-clock budget per call.

### 60s cool-down (PurgeQueueInProgress) {#sub-feature-60s-cool-down--purgequeueinprogress}

- **Capability ID:** `sub-feature:sqs:purgequeue:60s-cool-down--purgequeueinprogress`
- **Status:** 🟡 partial
- **Disposition:** 🛠️ feasible backlog
- **Tracking issue:** [#693](https://github.com/pedrosakuma/aws2azure/issues/693)

Enforced by a bounded in-process tracker keyed by namespace+queue. Expired and failed attempts are removed opportunistically; cross-replica coordination remains unsupported.

### Idempotency on empty queue {#sub-feature-idempotency-on-empty-queue}

- **Capability ID:** `sub-feature:sqs:purgequeue:idempotency-on-empty-queue`
- **Status:** ✅ implemented

Returns 200 with empty body, like SQS.

## Behaviour differences

- SB has no native purge. The proxy emulates it by draining peek-locked messages and deleting them. With a long LockDuration the drain may not be able to keep up if producers are sending faster than the proxy can delete — the 60s budget bounds wall-clock cost; the queue is therefore best-effort empty rather than guaranteed empty at the end of the call. The SQS contract guarantees a 60-second 'all messages enqueued at the time of the call will be deleted' window, which we approximate.
- The 60-second cool-down (PurgeQueueInProgress) is in-process only. Other replicas of the proxy will not observe the cool-down — a horizontally scaled deployment could allow multiple concurrent drains. Tracked for the NFR phase (shared coordination cache).
- Cooldown state is hard-capped and failed/nonexistent-queue attempts release their reservation. When the cap is occupied by active purges, new queue keys receive a retryable ServiceUnavailable response rather than growing process state.
- Hard purge remains conditional: pause producers, verify the queue is quiescent, and accept that the bounded REST drain cannot guarantee the native SQS purge contract.

## References

- <https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_PurgeQueue.html>
- <https://learn.microsoft.com/rest/api/servicebus/peek-lock-message-non-destructive-read>
- <https://learn.microsoft.com/rest/api/servicebus/delete-message>

