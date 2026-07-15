# Production runbook

This runbook turns the repository's compatibility, deployment, and telemetry
documentation into one production procedure. Use it for each workload and each
release. The result is workload-specific: a release can be a **go** for one
application and a **no-go** for another.

The procedure does not assume that the proxy is a Kubernetes sidecar. Run it as
a sidecar, a standalone container/process, or behind a platform service, but
record which component owns TLS, secrets, probes, traffic shifting, and
rollback. Topology-dependent choices below are called out explicitly.

## Operator record

Before staging, create a durable release record with:

| Field | Required evidence |
|---|---|
| Workload and owner | Application, owning team, and incident contact |
| Proxy artifact | Immutable image digest or binary checksum and version |
| Topology | Sidecar/standalone/platform, network path, and TLS termination point |
| AWS surface | Services, operations, request sizes, concurrency, and retry settings actually used |
| Azure targets | Resource types, regions, SKUs/capacity, and identity/RBAC assignments |
| Compatibility review | Links or snapshots for every required operation and accepted design gap |
| Staging evidence | Real-Azure smoke, load, failure, credential-rotation, and rollback results |
| SLO and rollback gates | Workload-specific thresholds, observation window, and decision owner |
| Previous release | Immutable artifact and configuration to restore |

Do not use `latest` as the recorded artifact. A rollback must identify the exact
previous binary or image.

## 1. Qualify the workload

1. Find the closest pattern in
   [Workload compatibility](../site/workload-compatibility.md).
2. Inventory every AWS operation the application calls, including startup,
   health, cleanup, retry, and rarely used administrative paths. Confirm each
   operation and required sub-feature in the
   [coverage matrix](../site/coverage.md).
3. Read the linked service page and
   [design gaps](../site/design-gaps.md). Record each semantic difference and
   its application-level mitigation.
4. Review [real-Azure divergences](../site/divergences.md). An implemented
   operation without a real-Azure seal requires representative validation in
   your own staging environment.
5. Verify the application's AWS SDK retry behavior, idempotency assumptions,
   timeouts, payload sizes, concurrency, consistency requirements, ordering,
   transactions, and control-plane calls. Do not qualify only the happy path.

An available module is not a statement of full AWS parity. A required
`unsupported` or `stub` operation is a no-go. A `partial` operation is a go only
when its documented difference has been tested and explicitly accepted.

## 2. Prepare a production-safe artifact and configuration

### Artifact and modules

- Build or select an immutable Native-AOT artifact for the target architecture.
- Compile only the modules the workload needs when a smaller artifact is useful;
  see [build-time module selection](./module-selection.md).
- At startup, verify the artifact contains the expected modules:

  ```bash
  curl --fail --silent http://localhost:8080/_aws2azure/modules
  ```

- Enable only those services in configuration. A compiled module and an enabled
  service are separate requirements.

### Credentials and identity

- Use one binding per AWS identity and least-privilege Azure data-plane access.
- Prefer `workloadIdentity` or `managedIdentity` where the deployment platform
  and backend support it. Follow the RBAC table and mode-specific requirements
  in [Azure authentication](../azure-authentication.md).
- If a static Azure key or client secret is unavoidable, keep the config out of
  source control and deliver it through the platform's secret mechanism. Limit
  who can read both the secret object and proxy process memory.
- Keep the application's AWS signing secret and the proxy binding synchronized.
  The proxy validates SigV4 before selecting Azure credentials.
- Treat configuration as startup state. The proxy loads and validates it once
  when the process starts; changing a mounted file or environment variable does
  not hot-reload credentials. Plan a controlled rollout for every static
  credential change.

### Network and TLS

- Use TLS between the application and proxy whenever traffic leaves a trusted
  loopback/shared network namespace. A same-pod sidecar can use loopback HTTP;
  a standalone or cross-host deployment should terminate TLS at the proxy or a
  trusted adjacent ingress.
- Preserve the service-prefixed `Host` value (`s3.*`, `sqs.*`, and so on);
  routing depends on it. Configure S3 clients for path-style addressing.
- Restrict inbound access to intended workloads and outbound access to the
  configured Azure and Entra endpoints.
- Never set `AWS2AZURE_INSECURE_TLS=1` in production. It disables certificate
  validation for outbound Azure calls and exists only for local emulators.

For Kubernetes mechanics, use either the
[sidecar guide](./sidecar.md) or the
[Helm chart](../../deploy/helm/aws2azure/README.md). Helm includes optional
Ingress TLS, HPA, PodDisruptionBudget, and ServiceMonitor support; none is a
requirement for other platforms.

## 3. Prove the workload in staging

Staging must use real Azure resources with the same relevant service
configuration, identity mode, network controls, and capacity model as
production. Emulator tests remain valuable regression checks but do not prove
Azure throttling, consistency, authorization, or capacity behavior.

Run all of the following:

1. Start the exact candidate artifact and production-shaped configuration.
   Confirm startup validation succeeds and no warning reports disabled outbound
   TLS verification.
2. Check the operational endpoints on a host name that does not match an AWS
   service:

   ```bash
   curl --fail --silent http://localhost:8080/health
   curl --fail --silent http://localhost:8080/ready
   curl --fail --silent http://localhost:8080/_aws2azure/modules
   curl --fail --silent http://localhost:8080/_aws2azure/metrics
   ```

   `/health` proves that the process is serving. `/ready` proves only that at
   least one compiled service is enabled and at least one credential entry was
   loaded; it does **not** call Azure or prove backend health.
3. Drive every required operation through the proxy with the same AWS SDK,
   addressing style, request shape, and retry configuration used by the
   application. Verify results in both the AWS response and Azure resource.
4. Exercise representative peak concurrency, payload distribution, long
   polling, batching, consistency, ordering, and transaction paths.
5. Inject or simulate the failures the workload must tolerate: invalid
   credentials, missing RBAC, backend throttling, timeout, backend
   unavailability, proxy restart, and application retry exhaustion.
6. Perform the credential-rotation and rollback procedures below.
7. Compare measured latency and error rate with the workload SLO. Keep Azure
   service telemetry beside proxy metrics so capacity and translation overhead
   can be distinguished.

The repository's [real-Azure test procedure](../testing/real-azure-nightly.md)
is a useful CRUD smoke reference. The
[performance harness](../perf/README.md) detects proxy regressions against local
emulators; its throughput values are not Azure capacity targets.

## 4. Go/no-go decision

### Go only when all are true

- Every required operation and sub-feature is implemented or an explicitly
  accepted partial behavior.
- Every applicable design gap has a tested workload-level mitigation.
- Operations without a repository real-Azure seal were exercised against real
  Azure in representative staging.
- The candidate meets the workload's error-rate, latency, throughput, payload,
  and concurrency gates with production-shaped Azure capacity.
- Application retries are enabled for retryable service-native errors, bounded,
  jittered, and safe for the operation's idempotency model.
- Identity, RBAC, secret delivery, network restrictions, and TLS have passed
  review. `AWS2AZURE_INSECURE_TLS` is absent.
- Health probes, proxy/platform metrics, logs, Azure telemetry, and alerts are
  visible to the on-call team.
- Canary promotion and rollback thresholds have named owners and observation
  windows.
- Static credential rotation and artifact/config rollback both succeeded in
  staging.
- The exact candidate and previous artifact/configuration are retained.

### No-go when any is true

- A required operation is unsupported/stubbed, or an unaccepted partial/design
  gap changes correctness.
- Validation relies only on an emulator for an unsealed production path.
- Representative load misses the workload SLO or exhausts Azure/proxy/platform
  capacity without a tested mitigation.
- The application depends on retry, ordering, consistency, transaction, IAM, or
  control-plane semantics that the selected Azure backend cannot reproduce.
- Credentials are over-privileged, committed to source, cannot be rotated, or
  fail on the intended identity/RBAC path.
- Remote plaintext traffic or disabled outbound certificate validation remains.
- Operators cannot observe the minimum signals below or cannot restore the
  previous release within the required recovery objective.

Record the decision, evidence, exceptions, approver, and expiration date for
every temporary waiver.

## 5. Deploy and canary

### Before changing traffic

- Capture baseline request/error rates, p95/p99 latency, Azure throttles and
  capacity, memory, CPU, restarts, and representative business outcomes.
- Verify the previous artifact and configuration can still be deployed.
- Freeze unrelated backend/config changes during the canary observation window.
- Confirm the candidate answers `/health`, `/ready`, and the expected module
  list before application traffic starts.

### Canary by topology

- **Sidecar:** canary whole application instances/pods with the candidate
  sidecar. Do not mix two sidecar versions inside one application instance.
  Compare candidate instances with unchanged instances handling equivalent
  workload.
- **Standalone proxy:** split a small, identifiable set of clients or traffic to
  candidate instances. Preserve the service-prefixed `Host` header through the
  load balancer/ingress.
- **Single-instance platform:** use a parallel deployment slot or a controlled
  maintenance window. If neither permits isolation, the change is not a canary;
  use stricter pre-deploy evidence and a shorter rollback trigger.

Start with a low-risk cohort, hold for the recorded observation window, and
promote in steps. At each step compare:

- workload success and native AWS error codes;
- proxy request/error rate and p95/p99 duration by service and operation;
- backend duration and Azure-native latency/throttle/capacity signals;
- memory, CPU, active requests, restarts, and OOM/eviction events;
- ordering, duplication, consistency, and business invariants relevant to the
  workload.

Stop promotion on any rollback trigger. Do not average the candidate and stable
cohorts together when deciding.

## 6. Observe and alert

### Health and platform signals

| Signal | Use |
|---|---|
| `/health` | Liveness only; restart a stuck/non-serving process |
| `/ready` | Startup/readiness gate for loaded services and credentials, not Azure reachability |
| Process/container restarts and exit logs | Configuration failure, crash, OOM, or platform eviction |
| CPU and working set vs. requests/limits | Proxy/platform saturation and memory headroom |
| Azure service metrics and activity/resource logs | Backend throttling, quota, availability, authorization, and capacity |

### Proxy metrics

Scrape `/_aws2azure/metrics` directly or through any compatible collector. The
endpoint uses Prometheus text format, but Prometheus is optional.

| Metric | Important labels | Operator use |
|---|---|---|
| `aws2azure_requests_total` | `service`, `operation`, `status` (`2xx`...`5xx`) | Traffic, availability, and status-class rate |
| `aws2azure_errors_total` | `service`, `operation`, `status_code` | Error rate and exact HTTP status |
| `aws2azure_request_duration_seconds` | `service`, `operation`, `status` | End-to-end proxy p95/p99 |
| `aws2azure_module_duration_seconds` | `service`, `operation` | Handler wall-clock time after SigV4 |
| `aws2azure_backend_duration_seconds` | `service`, `operation` | Accumulated Azure-call time |
| `aws2azure_request_size_bytes` / `aws2azure_response_size_bytes` | `service`, `operation` | Payload shifts and memory-risk context |
| `aws2azure_active_requests` | none | In-flight saturation/backlog clue |
| `aws2azure_process_working_set_bytes` | none | Resident-memory headroom |
| `aws2azure_dotnet_gc_heap_size_bytes` | none | Managed-heap trend |
| `aws2azure_dotnet_gc_allocated_bytes_total` | none | Allocation rate |
| `aws2azure_dotnet_gc_gen2_collections_total` | none | Full-GC rate |

Backend duration is a sum of Azure calls and can exceed module wall time when
calls run in parallel. It is not a direct "proxy overhead" value. Diagnose
latency by comparing:

1. end-to-end request duration;
2. module wall-clock duration;
3. accumulated backend duration;
4. Azure-native server latency, throttling, and capacity;
5. an equivalent direct-Azure baseline when one exists.

A rise in both proxy backend duration and Azure latency points to Azure or the
network. Stable backend duration with rising end-to-end/module duration points
toward proxy, client upload/download, CPU, memory, or concurrency pressure.
Confirm with a controlled comparison; do not subtract parallel accumulated
backend time from wall time.

### Minimum alerts

Choose thresholds from the workload SLO and baseline rather than copying a
universal number:

- readiness unavailable or repeated process restarts;
- 5xx/503 rate and workload failure rate above the rollback gate;
- 429 rate above the workload's throttle budget;
- p95/p99 duration above the SLO by service/operation;
- active requests rising with falling throughput;
- working set nearing the platform limit, OOMKill, or sustained CPU throttling;
- Azure quota/capacity saturation, authorization failures, or regional
  availability events;
- absence of expected traffic/metrics, which can indicate routing or scraping
  failure rather than health.

Collect stdout/stderr and platform logs at `Information` or the deployment's
approved level. Alert on warnings/errors and retain startup output. Do not log
secret values or complete signed requests.

## 7. Diagnose failures

Always capture the service, operation, HTTP status, native AWS error
code/message, time window, candidate/stable cohort, and related proxy/Azure
metrics. Error bodies remain service-native (XML for S3, JSON/query shapes for
other services), so the same HTTP status can mean different things.

Read the error field that matches the caller's wire protocol before using the
HTTP status as a diagnosis:

| Service | Native error shape | Canonical operation detail |
|---|---|---|
| S3 | XML `Error/Code`, `Message`, and `RequestId` | [S3 operations](../site/s3.md) |
| SQS | Query XML `Error/Type`, `Code`, and `Message`, or AWS JSON `__type` and `message` | [SQS operations](../site/sqs.md) |
| SNS | Query XML `Error/Type`, `Code`, `Message`, and `RequestId` | [SNS operations](../site/sns.md) |
| DynamoDB | AWS JSON `__type` and `message` | [DynamoDB operations](../site/dynamodb.md) |
| Kinesis | AWS JSON 1.1 `__type` and `message` | [Kinesis operations](../site/kinesis.md) |
| Secrets Manager | AWS JSON 1.1 `__type` and `message` | [Secrets Manager operations](../site/secretsmanager.md) |

| Status/symptom | Likely class | Checks | Action |
|---|---|---|---|
| `400` | Invalid AWS request, unsupported parameter/semantic condition, malformed routing payload, or mapped Azure validation error | Read the native AWS error code; compare the operation/sub-feature with its generated service page; reproduce with the same SDK request | Correct the request/configuration or stop if the required semantic is a documented gap |
| `403` | SigV4/access-key mismatch, Azure credential failure, missing RBAC, expired static secret, or network policy | Confirm the binding for the caller access key, clock/signing settings, identity mode, Entra token prerequisites, Azure role scope, and Azure authorization logs | Restore the binding/RBAC/secret; use the rotation procedure rather than weakening authorization |
| `408` or client timeout | Azure timeout, network path, exhausted internal timeout, long poll, or client deadline shorter than the operation | Compare client deadline, proxy duration/backend duration, Azure latency, DNS/TLS/connectivity, request size, and operation semantics | Remove the bottleneck or resize capacity; adjust the application deadline only with SLO and retry-budget review |
| `429` | Azure throttling/backpressure mapped to a service-native retryable error | Check exact operation, Azure throttle/quota metrics, request burst, partition/hot-key pattern, and SDK retry mode | Reduce concurrency, spread hot partitions, raise/provision Azure capacity, and use bounded jittered SDK backoff |
| `500` | Unmapped proxy failure, serialization/handler fault, or service-specific internal error | Inspect proxy logs/restarts and response body; compare candidate vs. stable cohort; reproduce with the same request | Roll back on a candidate regression; otherwise isolate the operation and preserve evidence for a defect |
| `503` | Azure/server/transport failure after retries, token-source transient failure, or an open per-endpoint circuit breaker returning a synthetic service-native transient error | Check Azure availability, network/TLS/DNS, identity endpoint, backend duration, Azure 5xx, and whether failures persist through the breaker's cool-down | Stop load amplification, keep client retries bounded, restore Azure/network/identity, and roll back if candidate-specific |

The shared Azure REST client defaults to a 100-second request timeout and up to
three attempts for replayable requests. It retries transport failures, internal
timeouts, 408, and 5xx with exponential jitter; 503 can honor `Retry-After`.
Streaming/non-replayable uploads may opt out of retries. A 429 is passed through
without an internal retry so the AWS SDK owns throttle backoff.

The per-endpoint circuit breaker opens after five consecutive logical failures
(after retry exhaustion), stays open for 30 seconds, then admits one probe.
While open it returns a synthetic 503 through the module's normal AWS error
mapping. It intentionally does not trip on 401/403, other ordinary 4xx, or 429.
There is no public breaker-state endpoint, so diagnose it by timeline and
backend evidence; do not restart-loop healthy instances merely to reset it.

## 8. Incident procedures

### Throttling

1. Stop rollout promotion and identify the throttled service/operation/partition.
2. Confirm 429/native throttle errors in proxy metrics and Azure telemetry.
3. Reduce producer concurrency or traffic for the affected cohort. Preserve
   bounded, jittered AWS SDK retries; avoid adding an unbounded retry layer.
4. Remove hot-key/partition pressure or increase the appropriate Azure
   throughput/SKU/quota.
5. Hold until throttle rate and latency remain below the recorded promotion
   gates, then resume gradually.

### Repeated 503 / open circuit

1. Stop promotion and prevent retry storms.
2. Check Azure status/capacity, DNS, TLS, egress policy, identity token endpoint,
   and backend 5xx.
3. Allow at least the 30-second open interval for a half-open probe after the
   underlying dependency recovers.
4. If stable instances recover but the candidate does not, roll back. If both
   fail, treat it as a dependency incident and shed/defer workload according to
   the application's recovery design.

### Timeouts

1. Compare client deadline, request/module/backend duration, and Azure latency.
2. Check payload size, concurrency, connection saturation, CPU throttling, long
   polling, and Azure capacity.
3. Reduce concurrency or payload pressure and restore network/backend health.
4. Change timeouts only after verifying idempotency, total retry budget, and the
   workload SLO. A longer timeout can hide saturation and increase in-flight
   memory.

### Static credential rotation

Configuration is loaded only at process startup.

1. Create/activate the replacement credential and grant least-privilege access
   without revoking the current credential.
2. Build the replacement config outside source control.
3. Deploy it to a canary and restart/roll out proxy instances so they load it.
4. Run signed AWS operations through every affected binding and confirm Azure
   authorization.
5. Promote the config to all instances.
6. Revoke the old Azure credential only after all old instances have drained.

For an AWS access-key/secret change, coordinate the application and proxy
binding so at least one matching pair is active throughout the rollout; use
separate bindings during overlap when the application can switch credentials
in stages. For Workload Identity, the projected token file is re-read during
token refresh, but identity/RBAC or service-account changes still require a
canary and platform-appropriate rollout.

### Azure unavailability

1. Stop promotion and determine whether the scope is one resource, region,
   service, identity plane, or network path.
2. Keep application retries bounded and shed/defer non-critical work. Do not
   create a retry storm behind the circuit breaker.
3. Follow the workload's Azure failover plan. The proxy does not infer regions
   or invent a cross-region topology; alternate endpoints/resources must be
   explicitly configured and previously tested.
4. After recovery/failover, run representative read/write/settlement operations
   and verify consistency, ordering, duplication, and business invariants before
   restoring full traffic.

## 9. Rollback

Rollback when a recorded trigger fires, including SLO breach, elevated
candidate-only 5xx/429/timeouts, correctness divergence, unsafe credential/TLS
state, crash/OOM loop, or failed post-deploy smoke.

1. Stop promotion and preserve candidate logs, metrics, native error bodies,
   artifact/config identity, and Azure telemetry.
2. Route new traffic to the exact previous artifact and configuration.
   - Sidecar: roll back whole application instances/pods.
   - Helm: use the recorded release revision with `helm rollback`.
   - Kubernetes manifests: restore the previous immutable image/config revision
     through the deployment controller.
   - Binary/process: restore the retained checksum-verified artifact and config,
     then restart under the normal supervisor.
3. Do not revoke credentials still required by draining previous instances.
4. Wait for in-flight requests/messages to settle according to the workload's
   idempotency, visibility, lock, and transaction semantics.
5. Verify `/health`, `/ready`, module inventory, representative AWS operations,
   workload invariants, error rate, latency, and Azure health.
6. Confirm metrics return to the pre-deploy baseline for the recorded recovery
   window before closing the rollback.
7. Record the incident and block re-promotion until the failed gate has new
   evidence.

Rollback restores proxy code/configuration; it does not automatically undo data
writes, Azure resource changes, queued messages, or external credential
revocation. Use workload-specific compensation where required.

## 10. Post-deploy closeout

- Confirm the full promoted cohort remains within SLO and capacity gates for the
  agreed observation window.
- Record the final artifact/configuration, dashboards, alerts, test evidence,
  accepted gaps, and rollback revision.
- Remove old credentials only after drain and validation.
- Update the relevant gap YAML when real Azure exposes a new divergence. Add a
  real-Azure seal only when the operation was actually exercised, following the
  [nightly real-Azure guide](../testing/real-azure-nightly.md#divergence-report--real-azure-seal-theme-c-467).
- Re-run this decision whenever operations, payloads, concurrency, topology,
  identity mode, Azure SKU/region, or proxy version changes materially.
