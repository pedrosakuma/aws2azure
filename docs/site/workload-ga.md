# Workload GA certification

These verdicts are generated from versioned profile manifests, gap docs, real-Azure seals, and qualification artifacts.

> **Current adoption authority (as of `2026-08-27T00:19:31Z`):** This generated certification has the highest precedence for current workload adoption. Release notes are immutable historical records and cannot override a current `candidate`, `conditional`, or `blocked` verdict.
>
> Source repository: `pedrosakuma/aws2azure`; canonical inputs: `normalized_yaml_sha256:d4e6f8313fe8cbfbf890f35975cf509801ad2a1961207796eaa8bc7444610367`; evaluator schema: `3`; evaluator implementation: `gapdocs_evaluator_implementation_sha256:25d90ef3e4d97c520472a8e0f006e016486fe8f5840fe8263b55133a906ce1b1`; contract: `docs/workloads/certification/authority.yaml`.

## Authority precedence

| Rank | Source | Role |
|---:|---|---|
| 1 | Live workload certification | Authoritative current adoption verdict |
| 2 | Workload profile manifests | Normative certification input |
| 3 | Gap docs | Normative capability input |
| 4 | Release notes | Immutable historical record |
| 5 | Explanatory guides | Non-authoritative explanation |

Legend: ⛔ blocked · 🟡 conditional · 🔵 candidate · ✅ GA

| Profile | Version | Minimum proxy | Verdict | Freshness | Blocking reasons |
|---|---:|---|---|---|---|
| DynamoDB basic table and item CRUD (`dynamodb-basic-crud`) | 1 | `0.1.0` | ✅ GA | expires in 70h (2026-08-29T22:52:46Z) | 0 |
| DynamoDB Query, Scan, and secondary indexes (`dynamodb-query-scan-indexes`) | 1 | `0.1.0` | 🟡 conditional | n/a | 1 |
| DynamoDB single-table single-partition transactions (`dynamodb-single-partition-transactions`) | 1 | `0.1.0` | 🔵 candidate | expired 666h ago (2026-07-30T05:23:29Z) | 16 |
| Kinesis basic record ingestion (`kinesis-basic-record-ingestion`) | 1 | `0.1.0` | 🔵 candidate | n/a | 1 |
| Kinesis single consumer per shard (`kinesis-single-consumer-per-shard`) | 1 | `0.1.0` | 🔵 candidate | n/a | 1 |
| S3 basic object CRUD (`s3-basic-object-crud`) | 1 | `0.1.0` | ✅ GA | expires in 61h (2026-08-29T13:40:16Z) | 0 |
| Secrets Manager basic lifecycle (`secretsmanager-basic-lifecycle`) | 1 | `0.1.0` | ✅ GA | expires in 18h (2026-08-27T18:42:13Z) | 0 |
| SNS standard publish (Event Grid backend) (`sns-standard-publish-event-grid`) | 1 | `0.1.0` | 🔵 candidate | n/a | 1 |
| SNS standard publish (Service Bus Topics backend) (`sns-standard-publish-service-bus`) | 1 | `0.1.0` | 🔵 candidate | n/a | 1 |
| SNS subscription management (Service Bus Topics backend) (`sns-subscription-management-service-bus`) | 1 | `0.1.0` | 🔵 candidate | n/a | 1 |
| SQS dead-letter and redrive (`sqs-dlq-redrive`) | 1 | `0.1.0` | ⛔ blocked | n/a | 7 |
| SQS FIFO messaging over AMQP (`sqs-fifo-amqp`) | 1 | `0.1.0` | ⛔ blocked | n/a | 2 |
| SQS standard messaging (`sqs-standard-messaging`) | 1 | `0.1.0` | ✅ GA | expires in 67h (2026-08-29T19:39:23Z) | 0 |

A profile reaches GA only when every required operation is compatible or explicitly accepted, every real-Azure seal is fresh, and a matching reviewed qualification artifact is `qualified`.
