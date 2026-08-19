# sqs / SetQueueAttributes {#operation-sqs-setqueueattributes}

[← sqs operation index](../../sqs.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:sqs:setqueueattributes`
- **Status:** 🟡 partial
- **Disposition:** 🔵 by design
- **Azure equivalent:** `Azure Service Bus management REST API — PUT /{queue}?api-version=2021-05 with If-Match: * (whole-entity replace)`

## Sub-features

### VisibilityTimeout → LockDuration {#sub-feature-visibilitytimeout--lockduration}

- **Capability ID:** `sub-feature:sqs:setqueueattributes:visibilitytimeout--lockduration`
- **Status:** ✅ implemented

### MessageRetentionPeriod → DefaultMessageTimeToLive {#sub-feature-messageretentionperiod--defaultmessagetimetolive}

- **Capability ID:** `sub-feature:sqs:setqueueattributes:messageretentionperiod--defaultmessagetimetolive`
- **Status:** ✅ implemented

### MaximumMessageSize → MaxMessageSizeInKilobytes {#sub-feature-maximummessagesize--maxmessagesizeinkilobytes}

- **Capability ID:** `sub-feature:sqs:setqueueattributes:maximummessagesize--maxmessagesizeinkilobytes`
- **Status:** ✅ implemented

Bounded by SQS 1 MiB cap (Aug-2025) and the SB tier ceiling (Standard 256 KiB, Premium up to 100 MiB).

### DelaySeconds (queue default) {#sub-feature-delayseconds--queue-default}

- **Capability ID:** `sub-feature:sqs:setqueueattributes:delayseconds--queue-default`
- **Status:** ✅ implemented

Stored in aws2azure's QueueDescription.UserMetadata blob and applied as the default per-message ScheduledEnqueueTimeUtc when SendMessage/SendMessageBatch omit DelaySeconds.

### ReceiveMessageWaitTimeSeconds (queue default for long-poll) {#sub-feature-receivemessagewaittimeseconds--queue-default-for-long-poll}

- **Capability ID:** `sub-feature:sqs:setqueueattributes:receivemessagewaittimeseconds--queue-default-for-long-poll`
- **Status:** ✅ implemented

Stored in aws2azure's QueueDescription.UserMetadata blob and applied as the default long-poll wait when ReceiveMessage omits WaitTimeSeconds.

### ContentBasedDeduplication / RequiresDuplicateDetection toggle {#sub-feature-contentbaseddeduplication---requiresduplicatedetection-toggle}

- **Capability ID:** `sub-feature:sqs:setqueueattributes:contentbaseddeduplication---requiresduplicatedetection-toggle`
- **Status:** ✅ implemented

Only on FIFO queues; SB rejects flipping the flag on Standard queues.

### RedrivePolicy → ForwardDeadLetteredMessagesTo {#sub-feature-redrivepolicy--forwarddeadletteredmessagesto}

- **Capability ID:** `sub-feature:sqs:setqueueattributes:redrivepolicy--forwarddeadletteredmessagesto`
- **Status:** 🟡 partial
- **Disposition:** 🔵 by design

JSON is parsed and mapped to ForwardDeadLetteredMessagesTo + MaxDeliveryCount. SB read-merge-write replaces the whole queue entity so the patch is preserved across subsequent SetQueueAttributes calls. On redelivery, the AMQP receive path checks the persisted limit, forwards an over-limit copy to the target, and completes the source before exposing it to the AWS client. The legacy REST receive transport retains the native Service Bus boundary.

### Policy / KmsMasterKeyId / KmsDataKeyReusePeriodSeconds / SqsManagedSseEnabled {#sub-feature-policy---kmsmasterkeyid---kmsdatakeyreuseperiodseconds---sqsmanagedsseenabled}

- **Capability ID:** `sub-feature:sqs:setqueueattributes:policy---kmsmasterkeyid---kmsdatakeyreuseperiodseconds---sqsmanagedsseenabled`
- **Status:** ⛔ unsupported
- **Disposition:** 🔵 by design

Returned as InvalidAttributeName for the unsupported attribute. SB has its own SAS/MSI/CMK story that does not translate 1:1.

### Read-merge-write semantics {#sub-feature-read-merge-write-semantics}

- **Capability ID:** `sub-feature:sqs:setqueueattributes:read-merge-write-semantics`
- **Status:** ✅ implemented

SB management is whole-entity replace — the proxy first GETs the queue, overlays only the patched fields, then PUTs with If-Match: * to avoid clobbering immutable / unmanaged fields.

## Behaviour differences

- Service Bus has no native queue-level DelaySeconds or ReceiveMessageWaitTimeSeconds fields. aws2azure persists those defaults in QueueDescription.UserMetadata and rejects updates if that field already contains non-aws2azure content.
- Service Bus may transfer the delivery whose count first exceeds MaxDeliveryCount, and explicit dead-letter settlement does not invoke ForwardDeadLetteredMessagesTo. The AMQP transport closes that boundary by reading queue metadata only on redelivery, sending an attributed copy to the target, and then completing the source. Send-before-complete preserves at-least-once semantics and may duplicate after an indeterminate source completion. The legacy REST receive transport retains the additional delivery.
- Updates prefer the Service Bus management ETag when it is surfaced and retry bounded 412 Precondition Failed responses. Backends that omit ETag still fall back to If-Match: *, so last-write-wins remains possible on those surfaces.
- Several SQS-only attributes have no SB equivalent (Policy, KmsMasterKeyId, KmsDataKeyReusePeriodSeconds, SqsManagedSseEnabled, RedriveAllowPolicy). The proxy rejects them with InvalidAttributeName until Slice 5 (RedrivePolicy) and the security-encryption pass land.
- FIFO-only attributes (FifoQueue, FifoThroughputLimit, DeduplicationScope) cannot be flipped on an existing queue (SB rejects the change); the proxy returns InvalidAttributeName.
- Real-Azure conformance coverage exists in Aws2Azure.IntegrationTests.Sqs.SqsRealAzureConformanceTests.Queue_metadata_and_tags_round_trip_against_real_service_bus; it was not executed in this environment.

## References

- <https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_SetQueueAttributes.html>
- <https://learn.microsoft.com/rest/api/servicebus/update-queue>

