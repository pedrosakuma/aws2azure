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
- **Tracking issue:** [#801](https://github.com/pedrosakuma/aws2azure/issues/801)

Cross-replica coordination is now implemented: the cool-down deadline is persisted in Service Bus QueueDescription.UserMetadata (the same compact envelope used for queue tags/defaults) and started with a GET + PUT If-Match compare-and-swap — a losing replica's PUT gets 412 PreconditionFailed and re-reads, at which point it observes either the winner's already-active deadline (PurgeQueueInProgress) or an expired one it can now claim. A bounded in-process tracker layers a same-replica fast reject in front of this (no network round trip for an immediate repeat). The remaining gap is a narrow, explicit fallback to single-replica, in-process-only coordination when the queue's UserMetadata is already owned by non-aws2azure content or too full (near the ~1024 character Service Bus UserMetadata limit) to fit the cool-down marker alongside existing tags; that fallback path is still tracked pending a genuinely dependency-free way to signal cooldown when UserMetadata is unavailable for aws2azure's use.

### Idempotency on empty queue {#sub-feature-idempotency-on-empty-queue}

- **Capability ID:** `sub-feature:sqs:purgequeue:idempotency-on-empty-queue`
- **Status:** ✅ implemented

Returns 200 with empty body, like SQS.

## Behaviour differences

- SB has no native purge. The proxy emulates it by draining peek-locked messages and deleting them. With a long LockDuration the drain may not be able to keep up if producers are sending faster than the proxy can delete — the 60s budget bounds wall-clock cost; the queue is therefore best-effort empty rather than guaranteed empty at the end of the call. The SQS contract guarantees a 60-second 'all messages enqueued at the time of the call will be deleted' window, which we approximate.
- Real-Azure instrumentation now includes both the shared Tier-3 happy-path evidence export (`purge-queue-roundtrip`) and the dedicated `purge-queue-lifecycle` conformance scenario, which seeds multiple messages, calls PurgeQueue once, and verifies the follow-up ReceiveMessage is empty. Leave `verified_real_azure` unset until a live-Azure CI run executes those scenarios on a commit that contains the new test.
- The 60-second cool-down (PurgeQueueInProgress) is now cross-replica — the deadline lives in Service Bus QueueDescription.UserMetadata and is claimed with an ETag compare-and-swap, so every proxy replica observes the same window instead of only the replica that received the call. The one remaining single-replica-only fallback is when the queue's UserMetadata already holds non-aws2azure content or is too full to fit the cool-down marker (see the sub-feature note); that narrow edge is tracked in #801 rather than silently degrading everywhere.
- Cooldown state is hard-capped and failed/nonexistent-queue attempts release their local reservation. When the local cap is occupied by active purges, new queue keys receive a retryable ServiceUnavailable response rather than growing process state; this local cap is independent of, and layered in front of, the distributed Service-Bus-persisted deadline.
- Starting the distributed cool-down costs one extra GET + PUT round trip to Service Bus before the drain begins (bounded retries on 412 PreconditionFailed, same pattern as TagQueue/UntagQueue/SetQueueAttributes). This is judged acceptable against the sidecar footprint budget because PurgeQueue is an infrequent, administrative operation already bounded by a 60s wall-clock budget, and it avoids introducing any new dependency (no Redis/Cosmos) for cross-replica coordination.
- Hard purge remains conditional: pause producers, verify the queue is quiescent, and accept that the bounded REST drain cannot guarantee the native SQS purge contract.

## References

- <https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_PurgeQueue.html>
- <https://learn.microsoft.com/rest/api/servicebus/peek-lock-message-non-destructive-read>
- <https://learn.microsoft.com/rest/api/servicebus/delete-message>
- <https://learn.microsoft.com/azure/service-bus-messaging/service-bus-xml-management-api>

