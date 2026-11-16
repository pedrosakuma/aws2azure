# sqs / SendMessageBatch {#operation-sqs-sendmessagebatch}

[← sqs operation index](../../sqs.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:sqs:sendmessagebatch`
- **Status:** ✅ implemented
- **Azure equivalent:** `Azure Service Bus queue runtime REST API — POST /{queue}/messages with Content-Type: application/vnd.microsoft.servicebus.json`
- **Real-Azure verified:** ✅ 2026-07-16 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/29473539261) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/29473539261)

## Sub-features

### 1..10 entries per batch {#sub-feature-110-entries-per-batch}

- **Capability ID:** `sub-feature:sqs:sendmessagebatch:110-entries-per-batch`
- **Status:** ✅ implemented

### Aggregate payload cap (≤1 MiB) {#sub-feature-aggregate-payload-cap--1-mib}

- **Capability ID:** `sub-feature:sqs:sendmessagebatch:aggregate-payload-cap--1-mib`
- **Status:** ✅ implemented

SQS counts each entry's body + message attributes (name + data type + value bytes) and rejects the batch when the sum exceeds 1 MiB (1,048,576 bytes). The proxy enforces the same rule.

### Unique entry Id validation (1..80 alnum/-/_) {#sub-feature-unique-entry-id-validation--180-alnum}

- **Capability ID:** `sub-feature:sqs:sendmessagebatch:unique-entry-id-validation--180-alnum`
- **Status:** ✅ implemented

### Per-entry MessageAttributes (String/Number/Binary) {#sub-feature-per-entry-messageattributes--string-number-binary}

- **Capability ID:** `sub-feature:sqs:sendmessagebatch:per-entry-messageattributes--string-number-binary`
- **Status:** ✅ implemented

### Per-entry DelaySeconds → ScheduledEnqueueTimeUtc {#sub-feature-per-entry-delayseconds--scheduledenqueuetimeutc}

- **Capability ID:** `sub-feature:sqs:sendmessagebatch:per-entry-delayseconds--scheduledenqueuetimeutc`
- **Status:** ✅ implemented

### Per-entry MessageDeduplicationId / MessageGroupId (FIFO) {#sub-feature-per-entry-messagededuplicationid---messagegroupid--fifo}

- **Capability ID:** `sub-feature:sqs:sendmessagebatch:per-entry-messagededuplicationid---messagegroupid--fifo`
- **Status:** ✅ implemented
- **Real-Azure verified:** ✅ 2026-07-28 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/30333267557) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/30333267557)

Reviewed real-Azure evidence covers ordered FIFO batch send plus replay of the identical entry set, confirming per-entry MessageGroupId routing and deduplication before the session-bound receive path drains the group.

### Per-entry MessageGroupId (standard fair-queue wire compatibility) {#sub-feature-per-entry-messagegroupid--standard-fair-queue-wire-compatibility}

- **Capability ID:** `sub-feature:sqs:sendmessagebatch:per-entry-messagegroupid--standard-fair-queue-wire-compatibility`
- **Status:** ✅ implemented

Accepted per entry and stored in the reserved AWS.Aws2Azure.MessageGroupId application property without enabling Service Bus sessions. Covered by Aws2Azure.UnitTests.Sqs.SqsFairQueueCompatibilityTests.Rest_SendMessageBatch_carries_each_standard_group_without_session_id and Aws2Azure.UnitTests.Sqs.AmqpSendMessageBatchHandlersTests.SendMessageBatch_standard_queue_forwards_each_group_without_session_ordering.

### Successful / Failed result partitioning {#sub-feature-successful---failed-result-partitioning}

- **Capability ID:** `sub-feature:sqs:sendmessagebatch:successful---failed-result-partitioning`
- **Status:** ✅ implemented

See behavior_differences — SB batch is atomic.

## Behaviour differences

- Per-queue transport selection (Phase 2.7 Slice 3): when the credential's serviceBus.transport (or per-queue override) is set to 'amqp', SendMessageBatch dispatches each entry over a single AMQP sender link and aggregates the per-transfer dispositions. Unlike the REST path, this gives real per-entry partial-failure granularity — one rejected entry surfaces in Failed[] while the others remain Successful. Validation, FIFO interlock, idempotency-key minting, MD5 and response shape are otherwise identical to REST.
- AMQP transport does not inspect the Premium-namespace sender-attach vendor property `com.microsoft:max-message-batch-size`. This is currently non-observable for supported SQS batches because the proxy caps aggregate batch payload size at 1 MiB, well below any Service Bus limit that property would raise.
- FIFO AMQP batches are dispatched sequentially in request order. Standard AMQP batches remain pipelined. This avoids relying on SemaphoreSlim waiter ordering for same-MessageGroupId transfer order.
- SB's runtime batch send is atomic: the whole batch either succeeds or fails together (no AMQP-style per-message ack). The proxy preserves SQS's BatchResultErrorEntry shape: on a batch-level SB error every entry surfaces in Failed[] with the mapped SQS error code (SenderFault=true for client-side rejections, false for server-side). Genuine per-entry partial success (one bad entry mixed with good ones) is not available over SB REST.
- MessageGroupId validation and queue semantics: on a .fifo queue, every entry must carry a MessageGroupId and maps to Service Bus SessionId; the proxy rejects the whole batch with MissingParameter on the first violating entry before the SB call. Standard entries may carry MessageGroupId, which is preserved only as AWS.Aws2Azure.MessageGroupId metadata and does not switch the entity or receive path to sessions. Standard-queue MessageDeduplicationId remains invalid. Covered by Aws2Azure.UnitTests.Sqs.FifoSendValidationTests.SendMessageBatch_on_fifo_queue_rejects_entry_without_group_id, Aws2Azure.UnitTests.Sqs.SqsFairQueueCompatibilityTests.Rest_SendMessageBatch_carries_each_standard_group_without_session_id, and Aws2Azure.UnitTests.Sqs.AmqpSendMessageBatchHandlersTests.SendMessageBatch_standard_queue_forwards_each_group_without_session_ordering.
- Azure Service Bus does not expose AWS fair queues' noisy-neighbor detection or quiet-group prioritization. Standard batch compatibility is limited to accepting and round-tripping each tenant identifier; delivery fairness and dwell-time isolation are not guaranteed. Aws2Azure.UnitTests.Sqs.SqsFairQueueCompatibilityTests.Rest_SendMessageBatch_carries_each_standard_group_without_session_id and Aws2Azure.UnitTests.Sqs.AmqpSendMessageBatchHandlersTests.SendMessageBatch_standard_queue_forwards_each_group_without_session_ordering assert that standard entries remain non-session messages.
- Same attribute-flattening + Aws2Azure-AttrTypes side-channel as SendMessage. The 1 MiB aggregate cap is computed over body + attribute name/type/value bytes per the SQS quota docs (raised from 256 KiB in August 2025).
- Payloads larger than 1 MiB require the AWS Extended Client Library (S3-backed pointer); the same caveat as SendMessage applies — the pointer flows through unchanged and resolves against the proxy's S3 → Blob translation.
- Entry MessageId returned to the caller is proxy-synthesised (Guid, or MessageDeduplicationId for FIFO).
- Standard-queue batch send is validated against real Azure Service Bus with per-entry result evidence; FIFO batch send and downstream session receive are sealed by the sqs-fifo-amqp fifo-amqp-boundaries scenario.

## References

- <https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_SendMessageBatch.html>
- <https://docs.aws.amazon.com/AWSSimpleQueueService/latest/SQSDeveloperGuide/sqs-fair-queues.html>
- <https://learn.microsoft.com/rest/api/servicebus/send-message-batch>

