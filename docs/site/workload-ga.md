# Workload GA certification

These verdicts are generated from versioned profile manifests, gap docs, real-Azure seals, and qualification artifacts.

> **Current adoption authority (as of `2026-08-18T17:30:00Z`):** This generated certification has the highest precedence for current workload adoption. Release notes are immutable historical records and cannot override a current `candidate`, `conditional`, or `blocked` verdict.
>
> Source repository: `pedrosakuma/aws2azure`; canonical inputs: `normalized_yaml_sha256:2d602f09d84aa6398788941ac5c8bf6aa981d1daced16d8ba4330fca6032979d`; evaluator schema: `3`; evaluator implementation: `gapdocs_evaluator_implementation_sha256:3ae9e89a800d2fb86e4a2f01d4bc4f20964f27eb8efa400d738b885cb8b99488`; contract: `docs/workloads/certification/authority.yaml`.

## Authority precedence

| Rank | Source | Role |
|---:|---|---|
| 1 | Live workload certification | Authoritative current adoption verdict |
| 2 | Workload profile manifests | Normative certification input |
| 3 | Gap docs | Normative capability input |
| 4 | Release notes | Immutable historical record |
| 5 | Explanatory guides | Non-authoritative explanation |

Legend: ⛔ blocked · 🟡 conditional · 🔵 candidate · ✅ GA

| Profile | Version | Minimum proxy | Verdict | Blocking reasons |
|---|---:|---|---|---|
| DynamoDB basic table and item CRUD (`dynamodb-basic-crud`) | 1 | `0.1.0` | 🟡 conditional | 2 |
| DynamoDB Query, Scan, and secondary indexes (`dynamodb-query-scan-indexes`) | 1 | `0.1.0` | 🟡 conditional | 1 |
| DynamoDB single-table single-partition transactions (`dynamodb-single-partition-transactions`) | 1 | `0.1.0` | 🔵 candidate | 16 |
| Kinesis basic record ingestion (`kinesis-basic-record-ingestion`) | 1 | `0.1.0` | 🔵 candidate | 1 |
| Kinesis single consumer per shard (`kinesis-single-consumer-per-shard`) | 1 | `0.1.0` | 🔵 candidate | 1 |
| S3 basic object CRUD (`s3-basic-object-crud`) | 1 | `0.1.0` | 🔵 candidate | 9 |
| Secrets Manager basic lifecycle (`secretsmanager-basic-lifecycle`) | 1 | `0.1.0` | 🔵 candidate | 10 |
| SNS standard publish (Event Grid backend) (`sns-standard-publish-event-grid`) | 1 | `0.1.0` | 🔵 candidate | 1 |
| SNS standard publish (Service Bus Topics backend) (`sns-standard-publish-service-bus`) | 1 | `0.1.0` | 🔵 candidate | 1 |
| SNS subscription management (Service Bus Topics backend) (`sns-subscription-management-service-bus`) | 1 | `0.1.0` | 🔵 candidate | 1 |
| SQS dead-letter and redrive (`sqs-dlq-redrive`) | 1 | `0.1.0` | ⛔ blocked | 8 |
| SQS FIFO messaging over AMQP (`sqs-fifo-amqp`) | 1 | `0.1.0` | ⛔ blocked | 2 |
| SQS standard messaging (`sqs-standard-messaging`) | 1 | `0.1.0` | 🔵 candidate | 11 |

A profile reaches GA only when every required operation is compatible or explicitly accepted, every real-Azure seal is fresh, and a matching reviewed qualification artifact is `qualified`.
