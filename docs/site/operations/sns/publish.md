# sns / Publish {#operation-sns-publish}

[← sns operation index](../../sns.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:sns:publish`
- **Status:** 🟡 partial
- **Disposition:** 🔵 by design
- **Azure equivalent:** `Azure Service Bus Topics / Azure Event Grid`
- **Real-Azure verified:** ✅ 2026-07-16 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/29473539261) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/29473539261)

## Sub-features

### AMQP publish path {#sub-feature-amqp-publish-path}

- **Capability ID:** `sub-feature:sns:publish:amqp-publish-path`
- **Status:** ✅ implemented
- **Real-Azure verified:** ✅ 2026-07-16 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/29473539261) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/29473539261)

Sends SNS Publish requests to Azure Service Bus Topics over AMQP 1.0 using SAS or Entra ID CBS authentication.

### Event Grid publish path {#sub-feature-event-grid-publish-path}

- **Capability ID:** `sub-feature:sns:publish:event-grid-publish-path`
- **Status:** ✅ implemented
- **Real-Azure verified:** ✅ 2026-07-21 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/29789050325) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/29789050325)

Sends SNS Publish requests to Azure Event Grid custom topics over the classic Event Grid schema using a per-topic backend switch.

### Service Bus FIFO subset {#sub-feature-service-bus-fifo-subset}

- **Capability ID:** `sub-feature:sns:publish:service-bus-fifo-subset`
- **Status:** ✅ implemented

For Service Bus-backed topics whose SNS names end in .fifo, Publish requires MessageGroupId, maps it to the AMQP group-id/Service Bus SessionId, maps MessageDeduplicationId to the broker MessageId, and falls back to a SHA-256-of-message-body broker MessageId when the topic was created with ContentBasedDeduplication=true. The underlying Service Bus topic must have duplicate detection enabled.

## Behaviour differences

- MessageId is a proxy-generated GUID, not an AWS-generated SNS identifier.
- SequenceNumber is returned empty because neither Azure Service Bus nor Azure Event Grid exposes an SNS-compatible sequence number on publish.
- MessageStructure=json is passed through as-is; the proxy does not filter per-protocol payloads yet.
- On the Service Bus Topics backend, MessageAttributes encode DataType in a parallel application property named '{Name}.DataType' so AWS-style attributes can be reconstructed by downstream consumers.
- On the Event Grid backend, the proxy emits the classic Event Grid schema with eventType=aws.sns.Message; CloudEvents-formatted Event Grid topics are not supported in this slice.
- On the Event Grid backend, MessageAttributes are emitted inside data.MessageAttributes as { Type, Value } objects.
- On the Event Grid backend, the Event Grid envelope subject is always the SNS TopicArn; the AWS Subject parameter is copied into data.Subject.
- On the Event Grid backend, HTTP-level publish failures are mapped to SNS per-message failure semantics by the proxy; Publish returns an SNS error while PublishBatch marks each affected entry failed.
- Subject is exposed both as the AMQP subject property and as the 'aws.sns.Subject' application property on the Service Bus Topics backend.
- For Service Bus-backed FIFO topics, broker-side duplicate detection is limited to Service Bus's duplicate-detection window. aws2azure provisions new FIFO topics with a 5-minute window, but out-of-band topic changes or publishes outside that window are treated as new messages.
- For Service Bus-backed FIFO topics, the proxy does not synthesize or return an SNS FIFO SequenceNumber because Service Bus does not expose an SNS-compatible publish sequence identifier on send.
- For standard (non-.fifo) SNS topic names, MessageGroupId and MessageDeduplicationId are rejected with InvalidParameter instead of being silently approximated.
- FIFO topics are unsupported on the Event Grid backend. Publish rejects .fifo topics and FIFO-only request parameters there with InvalidParameter instead of dropping them.
- aws2azure sets Service Bus SessionId on published FIFO messages, but the current SNS subscription-management APIs still create regular Service Bus subscriptions. Guaranteed ordered processing therefore requires consumers to use Service Bus-native session-aware subscriptions provisioned outside the SNS compatibility APIs.
- Azure Service Bus and Event Grid message size limits differ from SNS; Event Grid classic schema also enforces 1 MB per event and 1 MB per HTTP batch.
- Publish to a nonexistent Service Bus-backed topic: attaching an AMQP sender link to a missing topic answers on the wire with amqp:unauthorized-access rather than amqp:not-found. Since this deployment always authenticates AMQP sends with a namespace-scoped, full-rights credential, a link-level (post-CBS) unauthorized-access rejection can only mean the topic is missing, so the proxy renders SNS's native NotFoundException instead of an authorization error. Confirmed against real Azure by SnsRealAzureErrorPathTests.Publish_to_nonexistent_topic_returns_native_not_found_error.

## References

- <https://docs.aws.amazon.com/sns/latest/api/API_Publish.html>
- <https://learn.microsoft.com/azure/service-bus-messaging/service-bus-amqp-protocol-guide>
- <https://learn.microsoft.com/en-us/azure/service-bus-messaging/message-sessions>
- <https://learn.microsoft.com/en-us/azure/service-bus-messaging/duplicate-detection>
- <https://learn.microsoft.com/azure/event-grid/post-to-custom-topic>

