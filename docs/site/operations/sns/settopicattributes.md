# sns / SetTopicAttributes {#operation-sns-settopicattributes}

[← sns operation index](../../sns.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:sns:settopicattributes`
- **Status:** 🟡 partial
- **Disposition:** 🔵 by design
- **Azure equivalent:** `Azure Service Bus topic description`
- **Real-Azure verified:** ✅ 2026-08-20 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/32322056873) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/32322056873)

## Sub-features

### Topic metadata-backed attribute updates {#sub-feature-topic-metadata-backed-attribute-updates}

- **Capability ID:** `sub-feature:sns:settopicattributes:topic-metadata-backed-attribute-updates`
- **Status:** ✅ implemented

Performs a GET → conditional PUT cycle against the Service Bus topic description and persists DisplayName, Policy, and DeliveryPolicy inside TopicDescription.UserMetadata for later GetTopicAttributes projection.

### Compatibility no-ops {#sub-feature-compatibility-no-ops}

- **Capability ID:** `sub-feature:sns:settopicattributes:compatibility-no-ops`
- **Status:** ✅ implemented

Treats EffectiveDeliveryPolicy, KmsMasterKeyId, SignatureVersion, and TracingConfig as successful no-ops because this slice has no faithful Service Bus topic equivalent for those AWS attributes.

### Content-based deduplication validation {#sub-feature-content-based-deduplication-validation}

- **Capability ID:** `sub-feature:sns:settopicattributes:content-based-deduplication-validation`
- **Status:** ✅ implemented

Reads the current Service Bus topic description and rejects attempts to change RequiresDuplicateDetection after topic creation. Re-applying the existing value returns success.

## Behaviour differences

- DisplayName, Policy, and DeliveryPolicy are stored in TopicDescription.UserMetadata for round-tripping. Azure Service Bus does not evaluate SNS IAM-style topic policies or SNS delivery retry JSON, so those attributes remain metadata-only compatibility state rather than native enforcement.
- ContentBasedDeduplication is backed by RequiresDuplicateDetection, but Service Bus does not allow changing that property after topic creation. aws2azure returns InvalidParameter instead of attempting an in-place update.
- EffectiveDeliveryPolicy, KmsMasterKeyId, SignatureVersion, and TracingConfig remain no-ops because the Service Bus-backed profile has no faithful equivalent AWS topic-level behavior to apply.
- Updates whose serialized topic metadata would exceed the Azure Service Bus UserMetadata 1024-character limit are rejected with InvalidParameter.
- Unknown AWS attribute names return InvalidParameter.
- Real-Azure verification confirms mutable metadata-backed DisplayName updates persist onto the live Service Bus topic description and are observable through a follow-up GetTopicAttributes call (see SnsRealAzureConformanceTests.Topic_metadata_attributes_round_trip_against_real_service_bus).

## References

- <https://docs.aws.amazon.com/sns/latest/api/API_SetTopicAttributes.html>
- <https://learn.microsoft.com/azure/service-bus-messaging/service-bus-resource-manager-rest>

