# sqs / UntagQueue {#operation-sqs-untagqueue}

[← sqs operation index](../../sqs.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:sqs:untagqueue`
- **Status:** 🟡 partial
- **Disposition:** 🛠️ feasible backlog
- **Tracking issue:** [#801](https://github.com/pedrosakuma/aws2azure/issues/801)
- **Azure equivalent:** `GET + PUT QueueDescription with aws2azure's compact metadata envelope stored in UserMetadata.`

## Sub-features

### Queue existence validation {#sub-feature-queue-existence-validation}

- **Capability ID:** `sub-feature:sqs:untagqueue:queue-existence-validation`
- **Status:** ✅ implemented

Returns NonExistentQueue if the SB queue does not exist.

### Tag removal {#sub-feature-tag-removal}

- **Capability ID:** `sub-feature:sqs:untagqueue:tag-removal`
- **Status:** ✅ implemented

Reads the stored tag map from UserMetadata, removes requested keys, and writes the updated QueueDescription.

### UserMetadata capacity guard {#sub-feature-usermetadata-capacity-guard}

- **Capability ID:** `sub-feature:sqs:untagqueue:usermetadata-capacity-guard`
- **Status:** ✅ implemented

Updated metadata envelopes are kept within Service Bus's 1024-character UserMetadata limit.

## Behaviour differences

- Tags are stored inside an aws2azure-owned compact metadata envelope, base64-encoded into Service Bus QueueDescription.UserMetadata. Removing the last tag preserves any persisted queue-default DelaySeconds / ReceiveMessageWaitTimeSeconds values and clears UserMetadata only when no defaults remain.
- If QueueDescription.UserMetadata is already non-empty and does not contain aws2azure's tag blob, UntagQueue fails with InvalidParameterValue instead of overwriting operator-owned metadata.
- UntagQueue uses the Service Bus management ETag from GET as the PUT If-Match value. A 412 Precondition Failed triggers a bounded refetch/remerge retry so concurrent queue property changes are not clobbered.
- Service Bus UserMetadata is limited to roughly 1024 characters in the legacy management schema; aws2azure rejects updates that cannot fit the serialized tag map.
- Real-Azure conformance coverage exists in Aws2Azure.IntegrationTests.Sqs.SqsRealAzureConformanceTests.Queue_metadata_and_tags_round_trip_against_real_service_bus; it was not executed in this environment.

## References

- <https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_UntagQueue.html>
- <https://learn.microsoft.com/azure/service-bus-messaging/service-bus-xml-management-api>

