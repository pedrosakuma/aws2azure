# Workload GA certification

These verdicts are generated from versioned profile manifests, gap docs, real-Azure seals, and qualification artifacts.

> **Current adoption authority (as of `2026-08-27T00:19:31Z`):** This generated certification has the highest precedence for current workload adoption. Release notes are immutable historical records and cannot override a current `candidate`, `conditional`, or `blocked` verdict.
>
> Source repository: `pedrosakuma/aws2azure`; canonical inputs: `normalized_yaml_sha256:706eeffc6e40075389dc1f652c3ed9eaf04292e4816c8a61b9050858f1214ad1`; evaluator schema: `3`; evaluator implementation: `gapdocs_evaluator_implementation_sha256:5fa11997be871f8692e4ad3caf182dbd725f7f7426a860be7fa2c0836439b34e`; contract: `docs/workloads/certification/authority.yaml`.

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
| DynamoDB basic table and item CRUD (`dynamodb-basic-crud`) | 1 | `0.1.0` | ✅ GA | 0 |
| DynamoDB Query, Scan, and secondary indexes (`dynamodb-query-scan-indexes`) | 1 | `0.1.0` | 🟡 conditional | 1 |
| DynamoDB single-table single-partition transactions (`dynamodb-single-partition-transactions`) | 1 | `0.1.0` | 🔵 candidate | 16 |
| Kinesis basic record ingestion (`kinesis-basic-record-ingestion`) | 1 | `0.1.0` | 🔵 candidate | 1 |
| Kinesis single consumer per shard (`kinesis-single-consumer-per-shard`) | 1 | `0.1.0` | 🔵 candidate | 1 |
| S3 basic object CRUD (`s3-basic-object-crud`) | 1 | `0.1.0` | ✅ GA | 0 |
| Secrets Manager basic lifecycle (`secretsmanager-basic-lifecycle`) | 1 | `0.1.0` | ✅ GA | 0 |
| SNS standard publish (Event Grid backend) (`sns-standard-publish-event-grid`) | 1 | `0.1.0` | 🔵 candidate | 1 |
| SNS standard publish (Service Bus Topics backend) (`sns-standard-publish-service-bus`) | 1 | `0.1.0` | 🔵 candidate | 1 |
| SNS subscription management (Service Bus Topics backend) (`sns-subscription-management-service-bus`) | 1 | `0.1.0` | 🔵 candidate | 1 |
| SQS dead-letter and redrive (`sqs-dlq-redrive`) | 1 | `0.1.0` | ⛔ blocked | 7 |
| SQS FIFO messaging over AMQP (`sqs-fifo-amqp`) | 1 | `0.1.0` | ⛔ blocked | 2 |
| SQS standard messaging (`sqs-standard-messaging`) | 1 | `0.1.0` | ✅ GA | 0 |

A profile reaches GA only when every required operation is compatible or explicitly accepted, every real-Azure seal is fresh, and a matching reviewed qualification artifact is `qualified`.
