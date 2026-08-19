# sqs / DeleteMessageBatch {#operation-sqs-deletemessagebatch}

[← sqs operation index](../../sqs.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:sqs:deletemessagebatch`
- **Status:** ✅ implemented
- **Azure equivalent:** `Azure Service Bus queue runtime REST API — N parallel DELETE /{queue}/messages/{messageId}/{lockToken}?api-version=2021-05`
- **Real-Azure verified:** ✅ 2026-07-16 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/29473539261) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/29473539261)

## Sub-features

### 1..10 entries per batch {#sub-feature-110-entries-per-batch}

- **Capability ID:** `sub-feature:sqs:deletemessagebatch:110-entries-per-batch`
- **Status:** ✅ implemented

Matches SQS limit; enforced before any SB call.

### Per-entry Id validation (alnum/_/-, 1..80 chars, unique) {#sub-feature-per-entry-id-validation--alnum----180-chars-unique}

- **Capability ID:** `sub-feature:sqs:deletemessagebatch:per-entry-id-validation--alnum----180-chars-unique`
- **Status:** ✅ implemented

Returns the AWS-shaped EmptyBatchRequest / TooManyEntriesInBatchRequest / BatchEntryIdsNotDistinct / InvalidBatchEntryId on the whole call.

### Partial failure response shape {#sub-feature-partial-failure-response-shape}

- **Capability ID:** `sub-feature:sqs:deletemessagebatch:partial-failure-response-shape`
- **Status:** ✅ implemented

Per-entry Successful / Failed rows preserve the caller's Id ordering and carry SenderFault=true on rejects.

### Bounded parallelism {#sub-feature-bounded-parallelism}

- **Capability ID:** `sub-feature:sqs:deletemessagebatch:bounded-parallelism`
- **Status:** ✅ implemented

SemaphoreSlim cap of 5 concurrent SB DELETEs per batch to avoid throttling small SB Standard namespaces.

### ReceiptHandle round-trip {#sub-feature-receipthandle-round-trip}

- **Capability ID:** `sub-feature:sqs:deletemessagebatch:receipthandle-round-trip`
- **Status:** ✅ implemented

Same length-prefixed base64 ReceiptHandle as DeleteMessage; decoded per-entry.

## Behaviour differences

- SB REST has no native batch-delete; the proxy fans out parallel DELETEs. A failing entry never aborts the batch — callers see per-entry results matching SQS semantics.
- Expired-lock vs already-deleted ambiguity from DeleteMessage applies per entry (see DeleteMessage.yaml behavior_differences).
- Standard-queue batch deletion is validated against real Azure Service Bus with per-entry success and failure evidence; FIFO AMQP settlement remains subject to the separate FIFO gap.
- AMQP transport (Phase 2.5): when a queue is configured with `transport: Amqp`, DeleteMessageBatch routes to the AMQP path — each entry decodes the v2/v3 AMQP receipt handle minted by AMQP ReceiveMessage, looks up the cached (session) receiver via the lock-token cache, and calls `ServiceBusReceiver.CompleteAsync`. FIFO-aware: entries with different session-ids fan out to their own cached session receivers in parallel. Per-entry failures (stale handle, queue mismatch, cache miss, transport error) are surfaced as BatchResultErrorEntry items just like the REST path.

## References

- <https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_DeleteMessageBatch.html>
- <https://learn.microsoft.com/rest/api/servicebus/delete-message>

