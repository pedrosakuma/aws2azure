# sqs / GetQueueUrl {#operation-sqs-getqueueurl}

[← sqs operation index](../../sqs.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:sqs:getqueueurl`
- **Status:** ✅ implemented
- **Azure equivalent:** `GET https://{namespace}.servicebus.windows.net/{queue}?api-version=2021-05 (existence probe)`
- **Real-Azure verified:** ✅ 2026-07-20 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/29769257977) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/29769257977)

## Sub-features

### QueueOwnerAWSAccountId {#sub-feature-queueownerawsaccountid}

- **Capability ID:** `sub-feature:sqs:getqueueurl:queueownerawsaccountid`
- **Status:** ⛔ unsupported
- **Disposition:** 🔵 by design

**Gap.** aws2azure does not model AWS accounts; a placeholder 12-zero account id is always returned in the URL path. If a caller supplies a different QueueOwnerAWSAccountId, it is ignored.

## Behaviour differences

- Returned QueueUrl is '{request-scheme}://{request-host}/000000000000/{queue}' so the AWS SDK keeps routing back to the same proxy endpoint the caller reached.
- Existence check uses Service Bus GET; an unknown queue returns AWS.SimpleQueueService.NonExistentQueue.
- Validated against real Azure Service Bus through both the standard message lifecycle and queue discovery after proxy restart.

## References

- <https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_GetQueueUrl.html>

