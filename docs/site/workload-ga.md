# Workload GA certification

These verdicts are generated from versioned profile manifests, gap docs, real-Azure seals, and qualification artifacts.

Legend: ⛔ blocked · 🟡 conditional · 🔵 candidate · ✅ GA

| Profile | Version | Minimum proxy | Verdict | Blocking reasons |
|---|---:|---|---|---|
| DynamoDB basic table and item CRUD (`dynamodb-basic-crud`) | 1 | `0.1.0` | ✅ GA | 0 |
| Kinesis basic record ingestion (`kinesis-basic-record-ingestion`) | 1 | `0.1.0` | 🔵 candidate | 1 |
| S3 basic object CRUD (`s3-basic-object-crud`) | 1 | `0.1.0` | ✅ GA | 0 |
| Secrets Manager basic lifecycle (`secretsmanager-basic-lifecycle`) | 1 | `0.1.0` | ✅ GA | 0 |
| SNS standard publish (Event Grid backend) (`sns-standard-publish-event-grid`) | 1 | `0.1.0` | 🔵 candidate | 1 |
| SNS standard publish (Service Bus Topics backend) (`sns-standard-publish-service-bus`) | 1 | `0.1.0` | 🔵 candidate | 1 |
| SQS standard messaging (`sqs-standard-messaging`) | 1 | `0.1.0` | ✅ GA | 0 |

A profile reaches GA only when every required operation is compatible or explicitly accepted, every real-Azure seal is fresh, and a matching reviewed qualification artifact is `qualified`.
