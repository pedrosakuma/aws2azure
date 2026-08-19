# sns / ListSubscriptions {#operation-sns-listsubscriptions}

[← sns operation index](../../sns.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:sns:listsubscriptions`
- **Status:** 🟡 partial
- **Disposition:** 🔵 by design
- **Azure equivalent:** `Azure Service Bus topic subscriptions`
- **Real-Azure verified:** ✅ 2026-07-16 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/29473539261) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/29473539261)

## Sub-features

### Cross-topic subscription enumeration {#sub-feature-cross-topic-subscription-enumeration}

- **Capability ID:** `sub-feature:sns:listsubscriptions:cross-topic-subscription-enumeration`
- **Status:** ✅ implemented

Enumerates Service Bus topics first, then pages each topic's subscriptions and flattens the results into SNS member entries.

## Behaviour differences

- NextToken is a versioned, HMAC-SHA256-signed opaque cursor containing the current topic and subscription offsets. The AWS binding secret supplies the stable signing key, so tokens survive proxy restart while forged, tampered, and wrong-operation tokens are rejected.
- Listing all subscriptions requires cross-topic enumeration over the Service Bus management plane and can be more expensive than native SNS ListSubscriptions.
- Only Azure Service Bus topic subscriptions are enumerated. Azure Event Grid event subscriptions are explicitly excluded.

## References

- <https://docs.aws.amazon.com/sns/latest/api/API_ListSubscriptions.html>
- <https://learn.microsoft.com/azure/service-bus-messaging/service-bus-resource-manager-rest>

