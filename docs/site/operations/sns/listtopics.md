# sns / ListTopics {#operation-sns-listtopics}

[← sns operation index](../../sns.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:sns:listtopics`
- **Status:** ✅ implemented
- **Azure equivalent:** `Azure Service Bus Topics management REST API`
- **Real-Azure verified:** ✅ 2026-07-16 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/29473539261) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/29473539261)

## Sub-features

### Topic enumeration over Service Bus Topics REST {#sub-feature-topic-enumeration-over-service-bus-topics-rest}

- **Capability ID:** `sub-feature:sns:listtopics:topic-enumeration-over-service-bus-topics-rest`
- **Status:** ✅ implemented

Maps ListTopics to GET https://{namespace}.servicebus.windows.net/$Resources/topics?api-version=2021-05&$skip={N}&$top=100, parses the Atom feed entry titles, and emits SNS XML members with synthetic TopicArns.

### NextToken pagination cursor {#sub-feature-nexttoken-pagination-cursor}

- **Capability ID:** `sub-feature:sns:listtopics:nexttoken-pagination-cursor`
- **Status:** ✅ implemented

NextToken is an intentionally opaque, proxy-owned continuation token (base64 of the next Service Bus $skip offset). AWS itself documents SNS NextToken as an opaque, implementation-defined string that callers must treat as a black box and simply pass back verbatim -- it is never required to be a portable or AWS-native cursor format. An opaque server-issued cursor is therefore a normal, spec-compliant pagination pattern, not a compatibility gap: any AWS SDK client calling ListTopics repeatedly with the returned NextToken until it is absent gets correct, complete enumeration. Reclassified from feasible_backlog to by_design (tracked under #800) because there is no compatibility surface to close here beyond what already works.

## Behaviour differences

- TopicArn values are proxy-synthesised as arn:aws:sns:{sigv4-region}:000000000000:{topicName}. The account id is a stable placeholder, not an AWS account namespace.
- NextToken is an opaque base64-encoded Service Bus skip counter. Clients must treat it as an opaque continuation token (as AWS's own ListTopics contract requires) rather than parsing or persisting it beyond a single paging sequence; it is not guaranteed stable across proxy versions or backend changes.
- Pagination is fixed to Azure's $top=100 management page size for this slice. When Azure returns exactly 100 topics the proxy emits NextToken=base64(skip+100); otherwise NextToken is omitted.
- FIFO topics are distinguished only by their .fifo names in list output. ListTopics does not surface any additional FIFO-only attributes beyond the ARN/name itself.

## References

- <https://docs.aws.amazon.com/sns/latest/api/API_ListTopics.html>
- <https://learn.microsoft.com/en-us/rest/api/servicebus/list-topics>

