# sqs / GetQueueAttributes {#operation-sqs-getqueueattributes}

[← sqs operation index](../../sqs.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:sqs:getqueueattributes`
- **Status:** 🟡 partial
- **Disposition:** 🔵 by design
- **Azure equivalent:** `GET https://{namespace}.servicebus.windows.net/{queue}?api-version=2021-05 (Atom QueueDescription)`

## Sub-features

### Attribute.VisibilityTimeout {#sub-feature-attributevisibilitytimeout}

- **Capability ID:** `sub-feature:sqs:getqueueattributes:attributevisibilitytimeout`
- **Status:** ✅ implemented

Translated from Service Bus LockDuration.

### Attribute.MessageRetentionPeriod {#sub-feature-attributemessageretentionperiod}

- **Capability ID:** `sub-feature:sqs:getqueueattributes:attributemessageretentionperiod`
- **Status:** ✅ implemented

Translated from DefaultMessageTimeToLive.

### Attribute.MaximumMessageSize {#sub-feature-attributemaximummessagesize}

- **Capability ID:** `sub-feature:sqs:getqueueattributes:attributemaximummessagesize`
- **Status:** ✅ implemented

Derived from MaxMessageSizeInKilobytes; defaults to 1 MiB (1048576 bytes) when absent — matches the current SQS default (raised from 256 KiB to 1 MiB in August 2025).

### Attribute.DelaySeconds {#sub-feature-attributedelayseconds}

- **Capability ID:** `sub-feature:sqs:getqueueattributes:attributedelayseconds`
- **Status:** ✅ implemented

Returned from aws2azure's QueueDescription.UserMetadata blob when a queue-default DelaySeconds value was created or updated through the proxy.

### Attribute.ReceiveMessageWaitTimeSeconds {#sub-feature-attributereceivemessagewaittimeseconds}

- **Capability ID:** `sub-feature:sqs:getqueueattributes:attributereceivemessagewaittimeseconds`
- **Status:** ✅ implemented

Returned from aws2azure's QueueDescription.UserMetadata blob when a queue-default ReceiveMessageWaitTimeSeconds value was created or updated through the proxy.

### Attribute.ApproximateNumberOfMessages {#sub-feature-attributeapproximatenumberofmessages}

- **Capability ID:** `sub-feature:sqs:getqueueattributes:attributeapproximatenumberofmessages`
- **Status:** ✅ implemented

Mapped from Service Bus MessageCount when the property is present in the Atom response.

### Attribute.ApproximateNumberOfMessagesNotVisible / Delayed {#sub-feature-attributeapproximatenumberofmessagesnotvisible---delayed}

- **Capability ID:** `sub-feature:sqs:getqueueattributes:attributeapproximatenumberofmessagesnotvisible---delayed`
- **Status:** ✅ implemented

ApproximateNumberOfMessagesDelayed maps from ScheduledMessageCount. ApproximateNumberOfMessagesNotVisible is derived best-effort from MessageCount minus visible, scheduled, and transfer/dead-letter counts.

### Attribute.CreatedTimestamp / LastModifiedTimestamp {#sub-feature-attributecreatedtimestamp---lastmodifiedtimestamp}

- **Capability ID:** `sub-feature:sqs:getqueueattributes:attributecreatedtimestamp---lastmodifiedtimestamp`
- **Status:** ✅ implemented

Surfaced from the Atom entry's published/updated timestamps.

### Attribute.QueueArn {#sub-feature-attributequeuearn}

- **Capability ID:** `sub-feature:sqs:getqueueattributes:attributequeuearn`
- **Status:** ✅ implemented

Synthesized as arn:aws:sqs:us-east-1:000000000000:{queue}, matching the proxy's placeholder account convention used elsewhere in the SQS surface.

### Attribute.RedrivePolicy {#sub-feature-attributeredrivepolicy}

- **Capability ID:** `sub-feature:sqs:getqueueattributes:attributeredrivepolicy`
- **Status:** ✅ implemented

Emitted as JSON {deadLetterTargetArn, maxReceiveCount} when the SB queue has ForwardDeadLetteredMessagesTo set. The synthetic ARN uses arn:aws:sqs:us-east-1:000000000000:{queue}, consistent with the proxy placeholder account and DLQ attribution.

### Attribute.RedriveAllowPolicy {#sub-feature-attributeredriveallowpolicy}

- **Capability ID:** `sub-feature:sqs:getqueueattributes:attributeredriveallowpolicy`
- **Status:** ⛔ unsupported
- **Disposition:** 🔵 by design

**Gap.** SB has no per-queue ACL controlling which sources may forward into a DLQ.

### AttributeNames=All {#sub-feature-attributenamesall}

- **Capability ID:** `sub-feature:sqs:getqueueattributes:attributenamesall`
- **Status:** ✅ implemented

## Behaviour differences

- AttributeNames filtering happens proxy-side after the full Atom response is parsed.
- Service Bus has no native queue-level DelaySeconds or ReceiveMessageWaitTimeSeconds fields. aws2azure returns the proxy-owned defaults it persisted in QueueDescription.UserMetadata.
- ApproximateNumberOfMessagesNotVisible is a best-effort projection derived from Service Bus aggregate counters; Service Bus does not expose an exact SQS-style in-flight-only count.
- Real-Azure conformance coverage exists in Aws2Azure.IntegrationTests.Sqs.SqsRealAzureConformanceTests.Queue_metadata_and_tags_round_trip_against_real_service_bus; it was not executed in this environment.

## References

- <https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_GetQueueAttributes.html>

