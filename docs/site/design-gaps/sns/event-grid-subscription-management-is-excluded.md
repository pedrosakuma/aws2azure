# sns design gap / Event Grid subscription management is excluded {#design-gap-sns-event-grid-subscription-management-is-excluded}

[← Design-gap index](../../design-gaps.md)

- **Capability ID:** `design-gap:sns:event-grid-subscription-management-is-excluded`
- **Status:** ⛔ unsupported
- **Disposition:** 🔵 by design

Subscribe, ConfirmSubscription, ListSubscriptions, ListSubscriptionsByTopic, GetSubscriptionAttributes, SetSubscriptionAttributes, and Unsubscribe translate only to Azure Service Bus topic subscriptions. They never create, enumerate, mutate, confirm, or delete Azure Event Grid event subscriptions.

**Impact.** An SNS topic whose publish backend is Event Grid does not gain Event Grid delivery fan-out from the SNS subscription-management APIs, and its events are not delivered into Service Bus subscriptions created by those APIs.

**Workaround.** Provision and operate Event Grid event subscriptions with Azure-native tooling, or select the Service Bus Topics publish backend when this profile is required.

## References

- <https://learn.microsoft.com/azure/event-grid/manage-event-delivery>

