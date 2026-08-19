# sns design gap / Two backends with different fidelity {#design-gap-sns-two-backends-with-different-fidelity}

[← Design-gap index](../../design-gaps.md)

- **Capability ID:** `design-gap:sns:two-backends-with-different-fidelity`
- **Status:** 🔵 by design

A topic can be backed by Service Bus Topics (AMQP) or Event Grid, and the two backends do not offer identical semantics. On Event Grid the proxy emits the classic Event Grid schema (eventType=aws.sns.Message), the subject is always the TopicArn, and PublishBatch uses proxied per-entry outcomes; on Service Bus the delivery model and partial-failure shape differ.

**Impact.** The same SNS Publish/PublishBatch can behave differently depending on the configured backend; partial-failure semantics may diverge from SNS.

**Workaround.** Pick the backend per topic based on the delivery semantics required and test partial-failure handling against it.

## References

- <https://learn.microsoft.com/azure/event-grid/post-to-custom-topic>

