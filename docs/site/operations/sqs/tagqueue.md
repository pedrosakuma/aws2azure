# sqs / TagQueue {#operation-sqs-tagqueue}

[← sqs operation index](../../sqs.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:sqs:tagqueue`
- **Status:** 🟡 partial
- **Disposition:** 🛠️ feasible backlog
- **Tracking issue:** [#801](https://github.com/pedrosakuma/aws2azure/issues/801)
- **Azure equivalent:** `GET + PUT QueueDescription with aws2azure's compact metadata envelope stored in UserMetadata.`

## Sub-features

### Queue existence validation {#sub-feature-queue-existence-validation}

- **Capability ID:** `sub-feature:sqs:tagqueue:queue-existence-validation`
- **Status:** ✅ implemented

Returns NonExistentQueue if the SB queue does not exist.

### Tag persistence {#sub-feature-tag-persistence}

- **Capability ID:** `sub-feature:sqs:tagqueue:tag-persistence`
- **Status:** ✅ implemented

Merges requested SQS tags into the existing tag map and persists them in QueueDescription.UserMetadata.

### SQS tag limits {#sub-feature-sqs-tag-limits}

- **Capability ID:** `sub-feature:sqs:tagqueue:sqs-tag-limits`
- **Status:** ✅ implemented

Enforces at most 50 tags, key length 1..128, and value length 0..256 before writing.

### UserMetadata capacity guard {#sub-feature-usermetadata-capacity-guard}

- **Capability ID:** `sub-feature:sqs:tagqueue:usermetadata-capacity-guard`
- **Status:** ✅ implemented

Requests whose compact base64 metadata envelope would exceed Service Bus's 1024-character UserMetadata limit fail with InvalidParameterValue.

## Behaviour differences

- Tags are stored inside an aws2azure-owned compact metadata envelope, base64-encoded into Service Bus QueueDescription.UserMetadata. The same envelope also carries any persisted queue-default DelaySeconds / ReceiveMessageWaitTimeSeconds values.
- If QueueDescription.UserMetadata is already non-empty and does not contain aws2azure's tag blob, TagQueue fails with InvalidParameterValue instead of overwriting operator-owned metadata.
- TagQueue uses the Service Bus management ETag from GET as the PUT If-Match value. A 412 Precondition Failed triggers a bounded refetch/remerge retry so concurrent queue property changes are not clobbered.
- Service Bus UserMetadata is limited to roughly 1024 characters in the legacy management schema, so valid SQS tag sets near the 50-tag / 128-key / 256-value maximum may be rejected when the serialized blob does not fit.
- Real-Azure conformance coverage exists in Aws2Azure.IntegrationTests.Sqs.SqsRealAzureConformanceTests.Queue_metadata_and_tags_round_trip_against_real_service_bus; it was not executed in this environment.

## References

- <https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_TagQueue.html>
- <https://learn.microsoft.com/azure/service-bus-messaging/service-bus-xml-management-api>

