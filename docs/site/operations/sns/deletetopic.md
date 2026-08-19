# sns / DeleteTopic {#operation-sns-deletetopic}

[← sns operation index](../../sns.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:sns:deletetopic`
- **Status:** 🟡 partial
- **Disposition:** 🛠️ feasible backlog
- **Tracking issue:** [#692](https://github.com/pedrosakuma/aws2azure/issues/692)
- **Azure equivalent:** `Azure Service Bus Topics management REST API`
- **Real-Azure verified:** ✅ 2026-07-16 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/29473539261) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/29473539261)

## Sub-features

### Idempotent topic delete over Service Bus Topics REST {#sub-feature-idempotent-topic-delete-over-service-bus-topics-rest}

- **Capability ID:** `sub-feature:sns:deletetopic:idempotent-topic-delete-over-service-bus-topics-rest`
- **Status:** ✅ implemented

Parses TopicArn, extracts the topic name, and issues DELETE https://{namespace}.servicebus.windows.net/{topic}?api-version=2021-05. The delete is preceded by a GET probe so that a missing-entity 404 short-circuits cleanly without depending on the DELETE status code (the SB emulator returns HTTP 400 with no distinguishing body for DELETE on a missing entity; real Azure returns 404 for both).

## Behaviour differences

- DeleteTopic accepts only proxy-shaped ARNs of the form arn:aws:sns:{region}:{accountId}:{topicName}. The proxy currently synthesises accountId as 000000000000, but delete only uses the topic-name suffix when translating to Azure.
- FIFO topics can be deleted by their .fifo ARN names once they have been provisioned on the Service Bus-backed subset described in CreateTopic / Publish / PublishBatch.
- Azure deletes are asynchronous underneath Service Bus. A successful DeleteTopic response means the topic was accepted for deletion, not necessarily that every broker-side artifact is already gone.

## References

- <https://docs.aws.amazon.com/sns/latest/api/API_DeleteTopic.html>
- <https://learn.microsoft.com/en-us/rest/api/servicebus/delete-topic>

