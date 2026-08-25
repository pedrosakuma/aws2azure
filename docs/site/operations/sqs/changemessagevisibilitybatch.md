# sqs / ChangeMessageVisibilityBatch {#operation-sqs-changemessagevisibilitybatch}

[← sqs operation index](../../sqs.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:sqs:changemessagevisibilitybatch`
- **Status:** 🟡 partial
- **Disposition:** 🔵 by design
- **Azure equivalent:** `Azure Service Bus queue runtime REST API — bounded parallel PUT Unlock calls for VisibilityTimeout=0 and POST RenewLock calls for positive values.`

## Sub-features

### 1..10 entries per batch {#sub-feature-110-entries-per-batch}

- **Capability ID:** `sub-feature:sqs:changemessagevisibilitybatch:110-entries-per-batch`
- **Status:** ✅ implemented

### Per-entry Id validation (alnum/_/-, 1..80 chars, unique) {#sub-feature-per-entry-id-validation--alnum----180-chars-unique}

- **Capability ID:** `sub-feature:sqs:changemessagevisibilitybatch:per-entry-id-validation--alnum----180-chars-unique`
- **Status:** ✅ implemented

### Per-entry VisibilityTimeout 0..43200 validation {#sub-feature-per-entry-visibilitytimeout-043200-validation}

- **Capability ID:** `sub-feature:sqs:changemessagevisibilitybatch:per-entry-visibilitytimeout-043200-validation`
- **Status:** ✅ implemented

Non-integer / out-of-range entries fail with SenderFault InvalidParameterValue while siblings succeed.

### Bounded parallelism {#sub-feature-bounded-parallelism}

- **Capability ID:** `sub-feature:sqs:changemessagevisibilitybatch:bounded-parallelism`
- **Status:** ✅ implemented

5-way concurrency cap; lock-renew calls are individually short.

### VisibilityTimeout=0 (immediate release) {#sub-feature-visibilitytimeout0--immediate-release}

- **Capability ID:** `sub-feature:sqs:changemessagevisibilitybatch:visibilitytimeout0--immediate-release`
- **Status:** 🟡 partial
- **Disposition:** 🔵 by design

AMQP entries use Abandon and immediately re-expose the delivery. REST entries use Service Bus Unlock Message (PUT), but on real Azure a mixed batch can still delay rediscovery of the zero-timeout message until a sibling renewed entry ages out; the dedicated real-Azure REST-lane scenario locks that divergence.

### Renew semantics {#sub-feature-renew-semantics}

- **Capability ID:** `sub-feature:sqs:changemessagevisibilitybatch:renew-semantics`
- **Status:** 🟡 partial
- **Disposition:** 🔵 by design

SB renewlock extends the lock by the queue's configured LockDuration, ignoring the requested VisibilityTimeout — see behavior_differences.

## Behaviour differences

- SB has no per-call visibility override on REST. The proxy validates and accepts the VisibilityTimeout value but SB always extends by the queue's LockDuration. Callers needing an arbitrary new visibility must Delete+Send or rely on SetQueueAttributes to change the queue-wide LockDuration.
- VisibilityTimeout=0 is immediate only on the AMQP transport, where the proxy abandons the live delivery. On the REST transport the proxy can only call Unlock Message (PUT); in a mixed batch against real Azure Service Bus, the unlocked message may not be rediscoverable until a sibling renewed entry leaves the head-of-line window, so prompt zero-timeout redelivery is not guaranteed there.
- The dedicated `batch-visibility-timeout` real-Azure scenario targets the REST lane directly, mixing zero-timeout, positive-timeout, and invalid-handle entries so nightly live-Azure runs can verify per-entry outcomes plus redelivery timing across the original lock-expiry boundary.
- AMQP transport (Phase 2.5): when a queue is configured with `transport: Amqp`, ChangeMessageVisibilityBatch routes to the AMQP path — each entry with VisibilityTimeout=0 abandons via the cached (session) receiver, restoring the SQS 'immediately re-visible' semantics on this path (closing the divergence above for AMQP queues). Positive VisibilityTimeout values RenewLock via the SB `$management` link (session-aware for v3 receipt handles); SB clamping is silent in the batch shape (the singular CMV emits the `Aws2Azure-VisibilityClamped` header but the batch response has no per-entry place to carry it).

## References

- <https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_ChangeMessageVisibilityBatch.html>
- <https://learn.microsoft.com/rest/api/servicebus/unlock-message>
- <https://learn.microsoft.com/rest/api/servicebus/renew-lock>

