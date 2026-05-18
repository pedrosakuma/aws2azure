# aws2azure

> A transparent HTTP proxy that accepts requests in the **AWS wire protocol** (SigV4 + service-specific payloads) and translates them into equivalent calls against **Azure REST APIs**.

The goal is to enable applications using the official AWS SDK in **any language** to point `endpoint_url` at this proxy and operate against Azure without code changes — reducing the cost and risk of migrating workloads from AWS to Azure.

## Status

🚧 **Early planning / pre-alpha.** No working code yet.

## Scope

- **Direction:** AWS → Azure only (no reverse, no other clouds).
- **Runtime:** .NET (Native AOT).
- **Process model:** single binary multiplexing services by Host header / path.
- **Azure integration:** direct REST calls (no Azure SDK dependency).
- **First service:** S3 → Azure Blob Storage.

## Planned Services

| AWS Service | Azure Target | Status |
|---|---|---|
| S3 | Blob Storage | Planned (Phase 1) |
| SQS | Service Bus | Planned (Phase 2) |
| DynamoDB | Cosmos DB (NoSQL API) | Planned (Phase 3) |
| Kinesis | Event Hubs | Planned (Phase 4) |
| SNS | Event Grid / Service Bus Topics | Planned (Phase 5) |

## Non-Goals

- Not a 100% feature-compatible reimplementation. Gaps will be **documented exhaustively** per operation/sub-feature.
- Not a control-plane / IaC tool (use Terraform/Crossplane for that).
- Not a fork or repackaging of existing projects (s3proxy, MinIO, etc.) — built from scratch.



## Project Tracking

The canonical, durable source of truth for scope, phases, and status is the pinned **[Project Roadmap](https://github.com/pedrosakuma/aws2azure/issues/16)** issue. All work is tracked via GitHub issues and milestones — start there.

## License

MIT — see [LICENSE](./LICENSE).
