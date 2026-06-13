# aws2azure

> A transparent HTTP proxy that accepts requests in the **AWS wire protocol**
> (SigV4 + service-specific payloads) and translates them into equivalent calls
> against **Azure REST APIs**.

Point any application that uses the official AWS SDK — in **any language** — at
this proxy by setting its `endpoint_url`, and it runs against Azure with no code
changes. The goal is to reduce the cost and risk of migrating workloads from AWS
to Azure.

## Quickstart

```bash
git clone https://github.com/pedrosakuma/aws2azure.git
cd aws2azure
docker compose up --build

# In another terminal — S3 against the bundled Azurite emulator:
export AWS_ACCESS_KEY_ID=AKIADEVEXAMPLE
export AWS_SECRET_ACCESS_KEY=dev-secret-key-change-me
export AWS_DEFAULT_REGION=us-east-1

aws --endpoint-url http://s3.localhost:8080 s3 mb s3://demo
aws --endpoint-url http://s3.localhost:8080 s3 cp ./README.md s3://demo/readme
aws --endpoint-url http://s3.localhost:8080 s3 ls s3://demo/
```

See the **[Getting Started guide](./docs/getting-started.md)** for configuration,
enabling the other services, running from source, and pointing at real Azure. On
Azure compute you can authenticate without a stored secret via Managed Identity
or Workload Identity — see
[Azure authentication](./docs/azure-authentication.md).

## Scope

- **Direction:** AWS → Azure only (no reverse, no other clouds).
- **Runtime:** .NET 10, Native AOT — a single self-contained binary.
- **Process model:** one binary multiplexing services by Host header / path.
- **Azure integration:** direct REST calls (no Azure SDK dependency).
- **AWS integration:** wire protocol only (no AWS SDK dependency).

## Services

| AWS Service | Azure Target | Status |
|---|---|---|
| S3 | Blob Storage | Implemented |
| SQS | Service Bus | Implemented |
| DynamoDB | Cosmos DB (NoSQL API) | Implemented |
| Kinesis | Event Hubs | Implemented |
| SNS | Service Bus Topics / Event Grid | Implemented |

Coverage is **not** 100% of each AWS service. Every operation and sub-feature is
documented exhaustively under [`docs/gaps/`](./docs/gaps/), with the rendered
coverage matrix published from [`docs/site/`](./docs/site/). Gaps are documented,
never hidden.

## Configuration

The proxy reads a JSON config file pointed to by `AWS2AZURE_CONFIG_FILE`. Each
AWS access key your clients sign with maps to a set of Azure credentials; the
proxy validates the incoming SigV4 signature, then calls Azure with the mapped
credentials. See [`docker/config.json`](./docker/config.json) for an example and
the [Getting Started guide](./docs/getting-started.md#configuration) for the full
schema.

## Operational endpoints

- `GET /health` — liveness probe.
- `GET /ready` — readiness probe.
- `GET /_aws2azure/metrics` — Prometheus metrics.

## Sidecar footprint

aws2azure is meant to run as a **sidecar**, so its resource cost is a
first-class concern. On a `linux-x64` runner the all-modules Native-AOT build
measures roughly:

| AOT binary | Idle RSS | Cold start |
|------------|----------|------------|
| ~22 MB     | ~34 MB   | ~70 ms     |

These are **measured and regression-gated** in CI — see
[`docs/perf/README.md` (footprint budget)](./docs/perf/README.md#footprint-budget-271)
for the harness, the live numbers, and the per-metric budget. Footprint numbers
are runner-bound; binary size is deterministic while cold start scales with
runner core count.

For an even leaner sidecar you can compile in only the service modules a
workload needs (e.g. `-p:Modules=s3`), trimming ~20–27% off the binary and idle
RSS — see
[**build-time module selection**](./docs/deployment/module-selection.md).

## Non-Goals

- Not a 100% feature-compatible reimplementation. Gaps are **documented
  exhaustively** per operation/sub-feature.
- Not a control-plane / IaC tool (use Terraform/Crossplane for that).
- Not a fork or repackaging of existing projects (s3proxy, MinIO, etc.) — built
  from scratch.
- No reverse direction (Azure → AWS) and no other clouds.

## Project Tracking

The canonical source of truth for scope, phases, and status is the pinned
**[Project Roadmap](https://github.com/pedrosakuma/aws2azure/issues/16)** issue.
All work is tracked via GitHub issues and milestones.

## License

MIT — see [LICENSE](./LICENSE).
