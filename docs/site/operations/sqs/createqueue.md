# sqs / CreateQueue {#operation-sqs-createqueue}

[← sqs operation index](../../sqs.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:sqs:createqueue`
- **Status:** ✅ implemented
- **Azure equivalent:** `PUT https://{namespace}.servicebus.windows.net/{queue}?api-version=2021-05 (Atom QueueDescription)`
- **Real-Azure verified:** ✅ 2026-07-16 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/29473539261) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/29473539261)

## Sub-features

### Attribute.VisibilityTimeout {#sub-feature-attributevisibilitytimeout}

- **Capability ID:** `sub-feature:sqs:createqueue:attributevisibilitytimeout`
- **Status:** ✅ implemented

Maps to Service Bus LockDuration (ISO-8601 duration).

### Attribute.MessageRetentionPeriod {#sub-feature-attributemessageretentionperiod}

- **Capability ID:** `sub-feature:sqs:createqueue:attributemessageretentionperiod`
- **Status:** ✅ implemented

Maps to DefaultMessageTimeToLive.

### Attribute.MaximumMessageSize {#sub-feature-attributemaximummessagesize}

- **Capability ID:** `sub-feature:sqs:createqueue:attributemaximummessagesize`
- **Status:** ✅ implemented

Recorded as MaxMessageSizeInKilobytes (1024..1048576 bytes / 1 KiB..1 MiB). SQS raised its hard cap from 256 KiB to 1 MiB in August 2025; the proxy now mirrors that range. Backing Service Bus tier still constrains the *effective* limit: SB Standard rejects anything over 256 KiB, SB Premium honours up to 100 MiB (configurable). Per-queue MaximumMessageSize is set at create time but not re-validated per send — SB itself rejects oversized payloads on the runtime POST.

### Attribute.DelaySeconds {#sub-feature-attributedelayseconds}

- **Capability ID:** `sub-feature:sqs:createqueue:attributedelayseconds`
- **Status:** ✅ implemented

Persisted in aws2azure's QueueDescription.UserMetadata blob and applied as the default per-message ScheduledEnqueueTimeUtc when SendMessage/SendMessageBatch omit DelaySeconds.

### Attribute.ReceiveMessageWaitTimeSeconds {#sub-feature-attributereceivemessagewaittimeseconds}

- **Capability ID:** `sub-feature:sqs:createqueue:attributereceivemessagewaittimeseconds`
- **Status:** ✅ implemented

Persisted in aws2azure's QueueDescription.UserMetadata blob and applied as the default long-poll wait when ReceiveMessage omits WaitTimeSeconds.

### Attribute.FifoQueue / ContentBasedDeduplication {#sub-feature-attributefifoqueue---contentbaseddeduplication}

- **Capability ID:** `sub-feature:sqs:createqueue:attributefifoqueue---contentbaseddeduplication`
- **Status:** 🟡 partial
- **Disposition:** 🔵 by design

Maps to RequiresSession + RequiresDuplicateDetection. FIFO routing is implemented end-to-end on the AMQP transport (MessageGroupId -> SB SessionId on send; session-aware receive pins one consumer per group for strict per-group ordering — see ReceiveMessage). Strict ordering requires `transport: Amqp`; the REST transport cannot express session-receive and therefore does not provide strict per-group ordering (won't implement — inherent SB REST limitation).

### Attribute.RedrivePolicy {#sub-feature-attributeredrivepolicy}

- **Capability ID:** `sub-feature:sqs:createqueue:attributeredrivepolicy`
- **Status:** 🟡 partial
- **Disposition:** 🔵 by design

SQS RedrivePolicy JSON ({deadLetterTargetArn,maxReceiveCount}) is parsed and mapped to SB ForwardDeadLetteredMessagesTo (queue-name segment of the ARN) + MaxDeliveryCount. maxReceiveCount is bounded to 1..1000 per SQS. Because Service Bus does not apply ForwardDeadLetteredMessagesTo to an explicit dead-letter settlement, the AMQP receive path forwards an over-limit copy to the configured target and then completes the source delivery before it can be returned to the AWS client. The legacy REST receive transport retains Service Bus's native additional delivery. The target DLQ must already exist (auto-provisioning is intentionally not implemented; client owns DLQ lifecycle).

### Attribute.RedriveAllowPolicy {#sub-feature-attributeredriveallowpolicy}

- **Capability ID:** `sub-feature:sqs:createqueue:attributeredriveallowpolicy`
- **Status:** ⛔ unsupported
- **Disposition:** 🔵 by design

**Gap.** Accepted silently; SB has no per-queue ACL controlling which sources may forward into a DLQ.

### Attribute.KmsMasterKeyId / KmsDataKeyReusePeriodSeconds / SqsManagedSseEnabled {#sub-feature-attributekmsmasterkeyid---kmsdatakeyreuseperiodseconds---sqsmanagedsseenabled}

- **Capability ID:** `sub-feature:sqs:createqueue:attributekmsmasterkeyid---kmsdatakeyreuseperiodseconds---sqsmanagedsseenabled`
- **Status:** ⛔ unsupported
- **Disposition:** 🔵 by design

**Gap.** Service Bus encryption is namespace-level (Microsoft-managed by default; customer-managed via Key Vault out of band).

### Attribute.Policy {#sub-feature-attributepolicy}

- **Capability ID:** `sub-feature:sqs:createqueue:attributepolicy`
- **Status:** ⛔ unsupported
- **Disposition:** 🔵 by design

**Gap.** Resource-based access policies are AWS IAM; no Service Bus equivalent on REST.

### tags {#sub-feature-tags}

- **Capability ID:** `sub-feature:sqs:createqueue:tags`
- **Status:** ✅ implemented

SQS tags are stored in aws2azure's QueueDescription.UserMetadata blob alongside any persisted queue-default DelaySeconds / ReceiveMessageWaitTimeSeconds values.

## Behaviour differences

- Service Bus has no native queue-level DelaySeconds or ReceiveMessageWaitTimeSeconds fields. aws2azure persists those SQS defaults in its UserMetadata blob and applies them when send/receive calls omit the per-request values.
- RedrivePolicy maps to Service Bus MaxDeliveryCount and ForwardDeadLetteredMessagesTo. Because Service Bus may transfer the first over-limit delivery and manual dead-lettering bypasses automatic forwarding, the AMQP receive path sends a copy to the configured target before completing the source. This ordering is loss-averse and preserves SQS at-least-once semantics: a source-complete failure after a successful target send can produce a duplicate on retry. The legacy REST receive transport retains the native additional delivery.
- Queue name validation enforces SQS rules (1-80 alnum/-_, '.fifo' suffix) before reaching Azure; Azure container names are stricter on some characters.
- Idempotency: an existing queue with matching attributes returns the same QueueUrl; mismatched attributes surface QueueNameExists. The comparison includes LockDuration, TTL, MaxMessageSize, RequiresSession, RequiresDuplicateDetection, and the persisted DelaySeconds / ReceiveMessageWaitTimeSeconds defaults stored in UserMetadata.
- Core standard-queue creation is validated against real Azure Service Bus; FIFO session receive remains a separately documented gap and blocks SendMessage/ReceiveMessage seals.

## References

- <https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_CreateQueue.html>
- <https://learn.microsoft.com/rest/api/servicebus/create-queue>

