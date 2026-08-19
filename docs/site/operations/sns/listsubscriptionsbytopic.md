# sns / ListSubscriptionsByTopic {#operation-sns-listsubscriptionsbytopic}

[← sns operation index](../../sns.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:sns:listsubscriptionsbytopic`
- **Status:** 🟡 partial
- **Disposition:** 🔵 by design
- **Azure equivalent:** `Azure Service Bus topic subscriptions`
- **Real-Azure verified:** ✅ 2026-07-22 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/29941293719) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/29941293719)

## Sub-features

### Per-topic subscription enumeration {#sub-feature-per-topic-subscription-enumeration}

- **Capability ID:** `sub-feature:sns:listsubscriptionsbytopic:per-topic-subscription-enumeration`
- **Status:** ✅ implemented

Lists Azure Service Bus subscriptions for a single topic and projects stored UserMetadata back into SNS protocol/endpoint fields.

## Behaviour differences

- NextToken is a versioned, HMAC-SHA256-signed opaque cursor bound to ListSubscriptionsByTopic and the exact topic name. Tokens survive restart with the same AWS binding secret; tampering, cross-operation use, and reuse for another topic are rejected.
- Only Azure Service Bus topic subscriptions are enumerated. Azure Event Grid event subscriptions are explicitly excluded.

## References

- <https://docs.aws.amazon.com/sns/latest/api/API_ListSubscriptionsByTopic.html>
- <https://learn.microsoft.com/azure/service-bus-messaging/service-bus-resource-manager-rest>

