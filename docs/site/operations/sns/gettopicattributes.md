# sns / GetTopicAttributes {#operation-sns-gettopicattributes}

[← sns operation index](../../sns.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:sns:gettopicattributes`
- **Status:** 🟡 partial
- **Disposition:** 🔵 by design
- **Azure equivalent:** `Azure Service Bus topic description`

## Sub-features

### Topic property projection {#sub-feature-topic-property-projection}

- **Capability ID:** `sub-feature:sns:gettopicattributes:topic-property-projection`
- **Status:** ✅ implemented

Fetches the Service Bus topic Atom entry, parses TopicDescription with XmlReader, maps SubscriptionCount / RequiresDuplicateDetection into the closest SNS attribute surface, and projects DisplayName / Policy / DeliveryPolicy from TopicDescription.UserMetadata.

## Behaviour differences

- DisplayName, Policy, DeliveryPolicy, and EffectiveDeliveryPolicy come from aws2azure metadata stored in Service Bus TopicDescription.UserMetadata rather than native Service Bus topic fields.
- Policy and DeliveryPolicy remain metadata-only compatibility state. Azure Service Bus does not evaluate SNS IAM-style topic policies or SNS delivery retry policies, so GetTopicAttributes surfaces what aws2azure stored rather than a Service Bus-native enforcement model.
- SubscriptionsConfirmed is populated from Service Bus SubscriptionCount. Pending and deleted counts are always reported as 0 because aws2azure auto-confirms subscriptions and Service Bus does not expose the SNS lifecycle split.
- KmsMasterKeyId is returned empty because Service Bus encryption is configured at the namespace level, not per topic.
- FifoTopic is surfaced only for SNS topic names ending in .fifo. ContentBasedDeduplication is read from aws2azure metadata stored at create time; legacy FIFO topics without that metadata fall back to the raw Service Bus RequiresDuplicateDetection flag for backward compatibility.
- AWS-only attributes such as SignatureVersion and TracingConfig are omitted.

## References

- <https://docs.aws.amazon.com/sns/latest/api/API_GetTopicAttributes.html>
- <https://learn.microsoft.com/azure/service-bus-messaging/service-bus-resource-manager-rest>

