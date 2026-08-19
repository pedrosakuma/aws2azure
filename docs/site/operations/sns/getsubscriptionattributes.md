# sns / GetSubscriptionAttributes {#operation-sns-getsubscriptionattributes}

[← sns operation index](../../sns.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:sns:getsubscriptionattributes`
- **Status:** 🟡 partial
- **Disposition:** 🔵 by design
- **Azure equivalent:** `Azure Service Bus subscription description`
- **Real-Azure verified:** ✅ 2026-07-22 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/29941293719) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/29941293719)

## Sub-features

### Subscription metadata projection {#sub-feature-subscription-metadata-projection}

- **Capability ID:** `sub-feature:sns:getsubscriptionattributes:subscription-metadata-projection`
- **Status:** ✅ implemented

Fetches the Service Bus subscription Atom entry, parses SubscriptionDescription with XmlReader, and projects aws2azure's UserMetadata JSON back into SNS protocol, endpoint, filter, and raw-delivery attributes.

## Behaviour differences

- Protocol and Endpoint come from aws2azure's UserMetadata blob rather than native Service Bus subscription fields. Missing or invalid UserMetadata falls back to empty strings and RawMessageDelivery=false.
- ConfirmationWasAuthenticated is always true and PendingConfirmation is always false because this slice auto-confirms subscriptions.
- FilterPolicy and FilterPolicyScope are returned from aws2azure's stored UserMetadata and correspond to the Service Bus rule currently programmed for the subscription. FilterPolicyScope defaults to MessageAttributes when legacy stored metadata has no explicit scope.
- MessageBody-scope filters are enforced by projecting scalar JSON body fields into reserved Service Bus application properties during Publish / PublishBatch. Non-JSON bodies, array-valued fields, and unsupported SNS operators do not match those rules.
- DeliveryPolicy, EffectiveDeliveryPolicy, and RedrivePolicy are omitted because Service Bus delivery and dead-letter settings do not match the SNS attribute shapes exposed by this API.
- Attributes are read from Azure Service Bus subscriptions only; Azure Event Grid event-subscription properties are explicitly outside this profile.

## References

- <https://docs.aws.amazon.com/sns/latest/api/API_GetSubscriptionAttributes.html>
- <https://learn.microsoft.com/azure/service-bus-messaging/service-bus-resource-manager-rest>

