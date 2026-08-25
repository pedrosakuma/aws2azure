# sns design gap / Default Event Grid routing multiplexes multiple SNS topics {#design-gap-sns-default-event-grid-routing-multiplexes-multiple-sns-topics}

[← Design-gap index](../../design-gaps.md)

- **Capability ID:** `design-gap:sns:default-event-grid-routing-multiplexes-multiple-sns-topics`
- **Status:** 🔵 by design
- **Disposition:** 🔵 by design

Without a per-topic Event Grid endpoint override, all SNS topics that resolve to Event Grid publish into the same configured Azure Event Grid custom topic endpoint. Topic identity is preserved only inside the emitted envelope (subject / data.TopicArn), not as distinct Azure topic resources.

**Impact.** Azure-side isolation, quotas, monitoring, and RBAC apply at the shared Event Grid topic unless callers deliberately configure separate Event Grid endpoints for different SNS topics or topic-patterns.

**Workaround.** Configure per-topic eventGridTopicEndpoint overrides whenever separate Azure Event Grid resources are required for isolation or lifecycle reasons.

## References

- <https://learn.microsoft.com/azure/event-grid/post-to-custom-topic>

