# SQS dead-letter and redrive profile

This profile covers `RedrivePolicy`, broker redrive to a target queue,
dead-letter source attribution, and stateless
`ListDeadLetterSourceQueues` pagination. It is independent of the
`sqs-standard-messaging` GA profile.

`RedrivePolicy` maps to Service Bus `ForwardDeadLetteredMessagesTo` and
`MaxDeliveryCount`. `GetQueueAttributes` returns an AWS-shaped synthetic ARN
using the proxy placeholder account. Source pagination uses a restart-safe
namespace cursor and only emits `NextToken` when another matching source exists.

On the AMQP path, forwarded messages surface `DeadLetterQueueSourceArn` and the
proxy-prefixed Service Bus dead-letter reason/description when the broker
provides them. Real-Azure qualification must exercise receive/abandon through
redrive, attribution on the target, pagination across a proxy restart, and
cleanup.

`RedriveAllowPolicy` and AWS IAM permission administration remain unsupported;
use Azure RBAC, SAS, network policy, and binding isolation.
