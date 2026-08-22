# sns / ListTopics {#operation-sns-listtopics}

[← sns operation index](../../sns.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:sns:listtopics`
- **Status:** 🟡 partial
- **Disposition:** 🛠️ feasible backlog
- **Tracking issue:** [#800](https://github.com/pedrosakuma/aws2azure/issues/800)
- **Azure equivalent:** `Azure Service Bus Topics management REST API`
- **Real-Azure verified:** ✅ 2026-07-16 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/29473539261) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/29473539261)

## Sub-features

### Topic enumeration over Service Bus Topics REST {#sub-feature-topic-enumeration-over-service-bus-topics-rest}

- **Capability ID:** `sub-feature:sns:listtopics:topic-enumeration-over-service-bus-topics-rest`
- **Status:** ✅ implemented

Maps ListTopics to GET https://{namespace}.servicebus.windows.net/$Resources/topics?api-version=2021-05&$skip={N}&$top=100, parses the Atom feed entry titles, and emits SNS XML members with synthetic TopicArns.

## Behaviour differences

- TopicArn values are proxy-synthesised as arn:aws:sns:{sigv4-region}:000000000000:{topicName}. The account id is a stable placeholder, not an AWS account namespace.
- NextToken is an opaque base64-encoded Service Bus skip counter, not an AWS-compatible cursor. Tokens only preserve the next $skip offset and do not encode any other AWS pagination semantics.
- Pagination is fixed to Azure's $top=100 management page size for this slice. When Azure returns exactly 100 topics the proxy emits NextToken=base64(skip+100); otherwise NextToken is omitted.
- FIFO topics are distinguished only by their .fifo names in list output. ListTopics does not surface any additional FIFO-only attributes beyond the ARN/name itself.

## References

- <https://docs.aws.amazon.com/sns/latest/api/API_ListTopics.html>
- <https://learn.microsoft.com/en-us/rest/api/servicebus/list-topics>

