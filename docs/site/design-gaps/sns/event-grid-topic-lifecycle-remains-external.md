# sns design gap / Event Grid topic lifecycle remains external {#design-gap-sns-event-grid-topic-lifecycle-remains-external}

[← Design-gap index](../../design-gaps.md)

- **Capability ID:** `design-gap:sns:event-grid-topic-lifecycle-remains-external`
- **Status:** 🔵 by design
- **Disposition:** 🔵 by design

When SNS routing resolves to Event Grid, Publish and PublishBatch use the configured Event Grid endpoint/credentials directly. CreateTopic and DeleteTopic still manage only the Service Bus compatibility topic used for SNS metadata/subscriptions; they do not create, delete, or existence-check the Azure Event Grid custom topic.

**Impact.** Deleting the SNS topic through this compatibility layer does not prevent later Event Grid publishes to the same TopicArn while the configured Event Grid topic still exists and remains reachable.

**Workaround.** Provision, validate, and delete Azure Event Grid custom topics with Azure-native tooling; treat SNS CreateTopic / DeleteTopic as Service Bus compatibility-state management only when Event Grid is the publish backend.

## References

- <https://learn.microsoft.com/azure/event-grid/post-to-custom-topic>

