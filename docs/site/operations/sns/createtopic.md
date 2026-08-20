# sns / CreateTopic {#operation-sns-createtopic}

[← sns operation index](../../sns.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:sns:createtopic`
- **Status:** 🟡 partial
- **Disposition:** 🛠️ feasible backlog
- **Tracking issue:** [#800](https://github.com/pedrosakuma/aws2azure/issues/800)
- **Azure equivalent:** `Azure Service Bus Topics management REST API`
- **Real-Azure verified:** ✅ 2026-07-16 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/29473539261) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/29473539261)

## Sub-features

### Basic topic create over Service Bus Topics REST {#sub-feature-basic-topic-create-over-service-bus-topics-rest}

- **Capability ID:** `sub-feature:sns:createtopic:basic-topic-create-over-service-bus-topics-rest`
- **Status:** ✅ implemented

Maps CreateTopic(Name) to PUT https://{namespace}.servicebus.windows.net/{topic}?api-version=2021-05 with an empty TopicDescription Atom entry. 200/201 both succeed so create remains idempotent from the SNS caller's perspective.

### Attribute translation {#sub-feature-attribute-translation}

- **Capability ID:** `sub-feature:sns:createtopic:attribute-translation`
- **Status:** ✅ implemented

CreateTopic persists DisplayName, Policy, DeliveryPolicy, and the FIFO-only ContentBasedDeduplication flag inside TopicDescription.UserMetadata for later GetTopicAttributes / Publish projection.

### Service Bus-backed FIFO topic provisioning {#sub-feature-service-bus-backed-fifo-topic-provisioning}

- **Capability ID:** `sub-feature:sns:createtopic:service-bus-backed-fifo-topic-provisioning`
- **Status:** ✅ implemented

Names ending in .fifo are accepted on the Service Bus backend. aws2azure enables Service Bus duplicate detection, sets DuplicateDetectionHistoryTimeWindow=PT5M to match SNS's 5-minute dedup window, and uses the stored ContentBasedDeduplication flag later during Publish / PublishBatch when MessageDeduplicationId is omitted.

## Behaviour differences

- TopicArn is proxy-synthesised as arn:aws:sns:{sigv4-region}:000000000000:{topicName}. The account id is a stable placeholder because the proxy is not backed by an AWS account namespace.
- DisplayName, Policy, and DeliveryPolicy are stored in Service Bus TopicDescription.UserMetadata for round-tripping. Azure does not evaluate SNS IAM-style topic policies or SNS delivery retry JSON, so those attributes are metadata-only compatibility state rather than native enforcement.
- FIFO topics are recognised only when the SNS name ends in .fifo and the request explicitly sets FifoTopic=true. FifoTopic=true without a .fifo suffix, omitting FifoTopic on a .fifo name, FifoTopic=false on a .fifo name, and ContentBasedDeduplication on a non-FIFO name are rejected with InvalidParameter.
- For FIFO topics aws2azure always enables Service Bus duplicate detection because the supported subset maps SNS MessageDeduplicationId or content-based deduplication onto the broker MessageId within Service Bus's duplicate-detection window.
- ContentBasedDeduplication controls publish-time fallback when MessageDeduplicationId is omitted; it does not imply full SNS FIFO parity beyond the Service Bus-backed subset documented in Publish / PublishBatch / _design.
- FIFO CreateTopic requests are rejected when the resolved SNS backend is Event Grid because that backend cannot honor SNS FIFO ordering or deduplication semantics. Non-FIFO topics still create the backing Service Bus topic because subscription metadata continues to live there while Event Grid handles Publish / PublishBatch.
- Topic metadata is constrained by the Azure Service Bus UserMetadata 1024-character limit. Requests whose serialized DisplayName/Policy/DeliveryPolicy payload would exceed that ceiling are rejected with InvalidParameter.
- Service Bus duplicate detection remains time-windowed. aws2azure creates FIFO topics with the SNS-sized 5-minute window, but sends retried after that window expire are accepted as new messages.
- Service Bus topic names are further constrained by Azure. The proxy currently validates the AWS-side subset above and does not yet surface Azure's narrower length/character restrictions separately.

## References

- <https://docs.aws.amazon.com/sns/latest/api/API_CreateTopic.html>
- <https://learn.microsoft.com/en-us/rest/api/servicebus/create-topic>
- <https://learn.microsoft.com/en-us/azure/service-bus-messaging/duplicate-detection>

