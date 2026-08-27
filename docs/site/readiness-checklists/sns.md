# Before you migrate SNS {#before-you-migrate-sns}

[← Workload compatibility](../workload-compatibility.md#sns) · [Design gaps](../design-gaps.md#sns)

Answer each question with **yes** or **no**.
If you answer **yes**, read the linked design gap and confirm its workaround
fits your workload before migrating.

1. **Do different SNS topics need separate Azure Event Grid resources for isolation, quotas, or RBAC by default?** → [Default Event Grid routing multiplexes multiple SNS topics](../design-gaps/sns/default-event-grid-routing-multiplexes-multiple-sns-topics.md)
2. **Do you require rapid, built-in regional failover for Event Grid-backed SNS topics?** → [Event Grid custom-topic failover remains best-effort and external](../design-gaps/sns/event-grid-custom-topic-failover-remains-best-effort-and-external.md)
3. **Do you need the SNS Event Grid backend to accept or mint time-bounded Event Grid SAS publish tokens?** → [Event Grid publish auth omits SAS-token mode](../design-gaps/sns/event-grid-publish-auth-omits-sas-token-mode.md)
4. **Do you need SNS Subscribe and Unsubscribe APIs to create or manage Azure Event Grid event subscriptions?** → [Event Grid subscription management is excluded](../design-gaps/sns/event-grid-subscription-management-is-excluded.md)
5. **Do you expect CreateTopic or DeleteTopic to create or delete the underlying Azure Event Grid topic?** → [Event Grid topic lifecycle remains external](../design-gaps/sns/event-grid-topic-lifecycle-remains-external.md)
6. **Do you require full SNS FIFO parity, including consumer-side session provisioning and unbounded deduplication semantics?** → [FIFO topics are deferred](../design-gaps/sns/fifo-topics-are-deferred.md)
7. **Do your callers depend on real AWS account or region values inside SNS ARNs?** → [No AWS region / account namespace](../design-gaps/sns/no-aws-region---account-namespace.md)
8. **Do you rely on SNS policy attributes such as DeliveryPolicy, RedrivePolicy, or SubscriptionRoleArn being enforced?** → [No IAM-backed policy surface](../design-gaps/sns/no-iam-backed-policy-surface.md)
9. **Do you need Service Bus-backed SNS failover without preconfiguring and rehearsing the Geo-DR alias?** → [Service Bus Geo-DR requires alias-based failover planning](../design-gaps/sns/service-bus-geo-dr-requires-alias-based-failover-planning.md)
10. **Do you need Publish or PublishBatch semantics to stay identical across the Service Bus and Event Grid backends?** → [Two backends with different fidelity](../design-gaps/sns/two-backends-with-different-fidelity.md)
