# SecretsManager isolated operation capacity (report-only)

Delivery 2 of the performance methodology, following the
[lifecycle operation diagnostics](../workloads/secretsmanager-basic-lifecycle.md).
The lifecycle's serial nine-action loop does **not** measure isolated GET
capacity. This harness measures one selected AWS operation/path at a time,
through the proxy against **real Azure Key Vault**, with concurrency **1, 2, 5,
8**. It is an exploratory client-action curve, not a CPU-saturation claim,
promotion/qualification result, or replacement for the existing 9/4.5 floors.

Implementation: `tests/Aws2Azure.PerfTests/SecretsManager/`. The closed-loop
driver is `PerfRunner`; sweep analysis reuses `PerfSweep.DetectKnee`. The
shared-warmup `RunSweepAsync` is deliberately not used: each level needs its
own fresh process, inventory, warmup, drain and verified cleanup.

## Scenario catalog

Select **exactly one** identity per invocation. All identities have explicit
zero-value baseline waivers and are registered in `KnownPerfScenariosTests`.

| `AWS2AZURE_SECRETS_CAPACITY_SCENARIO` | Measured action and state |
|---|---|
| `create-fresh` | CreateSecret, unique previously unused name for every attempt |
| `describe-worker-local` | DescribeSecret, each worker reads its own one-version secret |
| `get-current-worker-local` | GetSecretValue without VersionId, worker-local |
| `get-version-worker-local` | GetSecretValue with the seeded VersionId, worker-local |
| `get-current-shared` | GetSecretValue current, all workers read the **same** secret |
| `put-fresh-version` | PutSecretValue once on each distinct one-version secret (1 → at most 2 versions) |
| `update-fresh-version` | UpdateSecret with a new value once per distinct one-version secret (1 → at most 2 versions) |
| `list-first-page-32` | ListSecrets first page, MaxResults=16, fixed 32-secret inventory |
| `list-second-page-32` | ListSecrets second page, MaxResults=16, token obtained before warmup |
| `delete-force-fresh` | DeleteSecret with ForceDeleteWithoutRecovery, distinct one-version secret per attempt |

Read-local inventory is always eight secrets regardless of concurrency; shared
GET uses one. Explicit-version GET currently reads the only seeded version,
**not a historical version in a large version chain**. LIST preflight checks
both pages against the exact owned inventory; measurement performs one page
request, not a multi-page traversal. There are no filters or unrelated secrets.
Writing the same secret concurrently is deliberately **not** part of the
fresh-version write scenarios. Same-secret write contention and deeper version
histories require separate experiments, not relabeling these results.

Create necessarily grows the active inventory and delete shrinks it; both are
bounded one-pass experiments, not stationary infinite loops. Writes do not
accumulate unbounded version chains. No measurement action performs setup,
cleanup, polling, automatic SDK retry, or inventory refill.

## Fixed budget and phase boundaries

The initial constants in `SecretsCapacityPlan` are intentionally small and not
environment-overridable. Expanding duration, request budget, inventory or the
ladder requires a deliberate reviewed change and new resource authorization.

- One selected scenario, four sequential levels, fresh proxy process per level.
- Setup deadline: 5 minutes per level. No vault/resource provisioning.
  At most 264 setup CreateSecret calls for single-use write/delete scenarios;
  zero for create; 8 for local reads, 1 for shared GET, 32 plus two LIST
  preflight calls for LIST. Each seed has exactly one version.
- Warmup: **8 serial successful actions**, excluded from timing; errors abort.
  Single-use scenarios reserve 8 distinct warmup names, leaving 256 measurement
  names. There is no failure-swallowing time-based warmup.
- Measurement: **5 seconds of dispatch**, at most **256 attempted AWS actions**
  (including errors/throttles), concurrency 1/2/5/8. The strict reservation in
  `PerfRunner` prevents parallel overshoot. Actions have a 15-second linked
  cancellation deadline; measurement+drain has a 25-second deadline.
- Dispatch stops at the time/request boundary; in-flight actions drain. A
  window that consumes the entire attempt budget is **invalid**, even if its
  final action drains past five seconds. Increase budget only deliberately;
  never replenish inventory within a window. No successful actions, any
  non-throttle error, short windows, failed cleanup, or missing configuration
  are failures, not successful skips. Partial throttling is reported; a fully
  throttled window is invalid even in resource-only mode.
- Maximum owned names: **264 at a time / 1,056 across a four-level single-use
  sweep**. Put/Update have at most two versions per name; other scenarios at
  most one. Measured+warmup actions are at most **1,056 per invocation**.
- Cleanup has its own independent 5-minute deadline per level, not the expired
  setup/measurement token. Each owned name allows one active DELETE and at most
  40 iterations of purge DELETE + active GET + deleted GET (at most 121
  control requests/name), plus two final empty-vault GETs. The deadline will
  usually bind first; these are hard *upper bounds*, not a cost forecast.
  Identity exchanges and empty-vault preflight are additional control calls.
  AWS action counts are **not** downstream Key Vault request counts: shipping
  translation, label reconciliation, retry and purge can issue multiple calls.
- Stop at the first invalid level; no subsequent level runs. This keeps a
  broken backend or resource leak from silently consuming the entire sweep.

### Delete and asynchronous backend work

Force-delete can return before the proxy's 30-second detached purge loop ends.
The harness waits **35 seconds after delete warmup** and **35 seconds after
the measured drain**, then terminates the per-level proxy before direct cleanup.
The elapsed quiet interval is recorded; it does **not** prove purge succeeded.
Cleanup independently checks active and deleted-secret endpoints, attempts
remaining purges, then requires both inventories to be empty.

Purges from **earlier measured deletes can overlap later measured deletes**.
The reported delete response throughput therefore includes that contention;
it is not “isolated TPM with zero cleanup cost,” nor a measurement of completed
purges. Quiet intervals are outside the action TPS denominator. Process
termination prevents proxy tasks from crossing levels; it cannot assert that
all invisible Azure service maintenance has stopped. Runs still require an
exclusive vault and comparable topology/cooling conditions.

## Explicit opt-in execution

**Do not execute without approval for this finite experiment and its billed
Azure resources.** This delivery has offline validation only; no real Azure
capacity result is asserted. There is no Key Vault emulator and no simulated
Key Vault throughput measurement.

Use a pre-provisioned **disposable, exclusively leased, initially empty**
public-Azure Key Vault, including an empty soft-deleted inventory, with purge
protection disabled. The workload identity must have get/list/set/delete/purge
permissions. The operator owns the vault lease, resource-group teardown and
budget. The acknowledgement below is not a distributed lock. Never share
the vault with qualification, RC observation, another capacity process or
production; empty-inventory preflight detects existing contents but cannot
prevent a competing operator from mutating it later.

Prepare the existing verified sealed candidate files with
`eng/resolve-sealed-runtime.sh` and the documented sealed-runtime process.
Configure:

- `AZURE_KEYVAULT_URL`, `AZURE_TENANT_ID`, `AZURE_CLIENT_ID`,
  `AZURE_FEDERATED_TOKEN_FILE` (existing workload-identity fixture contract);
- `AWS2AZURE_SEALED_RUNTIME_MODE=candidate`;
- `AWS2AZURE_SEALED_CANDIDATE_EXECUTABLE`,
  `AWS2AZURE_SEALED_CANDIDATE_MANIFEST`,
  `AWS2AZURE_SEALED_CANDIDATE_IDENTITY`;
- `AWS2AZURE_QUALIFICATION_SHA` (full harness/qualification checkout SHA);
  when deliberately using an approved historical binary, the existing
  `AWS2AZURE_QUALIFICATION_MODE=reaffirm` identity-verification rules apply.
  This does **not** make capacity output promotion evidence.
- `AWS2AZURE_LOAD_GIT_SHA` identifying the harness checkout;
- leave `AWS2AZURE_RC_OBSERVATION_COHORT_ROLE` unset. Candidate mode is required;
  RC/stable cohort overrides and source-build fallback are rejected.

Then, only after explicit authorization, for example:

```bash
export AWS2AZURE_SECRETS_CAPACITY=1
export AWS2AZURE_SECRETS_CAPACITY_EXCLUSIVE_VAULT=1
export AWS2AZURE_SECRETS_CAPACITY_SCENARIO=get-current-worker-local
export AWS2AZURE_SECRETS_CAPACITY_OUTPUT="$PWD/artifacts/secrets-capacity"
export AWS2AZURE_SECRETS_CAPACITY_TOPOLOGY='record runner/proxy/backend regions, network path, CPU/memory limits and isolation'
dotnet test tests/Aws2Azure.PerfTests -c Release \
  --filter 'Category=SecretsManagerCapacity'
```

No workflow is added or modified. Default CI/emulator/perf runs skip this test
**before fixture initialization** unless the dedicated opt-in is exactly `1`.
Once opted in, missing credentials, unknown scenario, sealed-identity problems,
write failures or evidence publication failures fail the test. Other real-Azure
or perf labels do not opt into this surface. Do not add the opt-in globally to
the existing nightly emulator provisioning.

## Evidence and interpretation

The output directory contains run-UUID/scenario/concurrency-specific files,
written with create-new semantics:

- `*.ownership.json`: all potentially owned names, persisted **before any
  mutation**, including names whose create response could time out.
- `*.operation-timings.json`: PR #1021's schema, percentile histogram and
  completion-time measurement/drain assignment, with corrected isolated
  workload/schedule/warmup/retry metadata. Per-operation successes/attempts TPS,
  mean, p50/p95/p99, errors and throttles remain report-only/non-promotable.
- `*.capacity.json`: runtime/config/backend digests, harness SHA, operator
  topology, load/retry policy, budgets, counts, state shape, measured/drain wall
  time, quiet and cleanup wall time, cleanup status and validity. `controlRequests`
  counts the harness's direct Key Vault requests, **not** the proxy's internal
  traffic. It deliberately does not serialize raw exceptions or secret values.
- `*.sweep.json`: only after all four valid levels; the existing knee heuristic
  and all levels. A knee is not proof of proxy CPU saturation or sustainable
  long-run throughput, especially in five-second, single-pass experiments.

`PerfResult` summaries cover dispatch plus in-flight drain; diagnostic
`measurement` rows use completions within the requested window and `drain`
rows show later completions. Keep those denominators distinct. Cleanup never
contributes successful operations to measurement. Warmup initializes connections
but does not prove readiness/stationarity; stronger readiness and controlled
comparison methodology are the next delivery.

If cleanup fails, the test fails and retains its ownership manifest. Keep the
lease; inspect those names and both active/deleted endpoints, finish purging or
tear down the authorized disposable resource group. Do not discard failed
evidence, reuse a dirty vault or label the experiment successful. Host/process
termination cannot guarantee cleanup; the persisted manifest is the recovery
record. No cleanup code deletes unrelated inventory.

Offline verification:

```bash
dotnet test tests/Aws2Azure.PerfTests -c Release \
  --filter 'FullyQualifiedName~SecretsCapacity|FullyQualifiedName~SecretsManagerCapacity|FullyQualifiedName~KnownPerfScenariosTests|FullyQualifiedName~PerfRunnerWarmupTests'
```

The fake-client tests verify dispatch, exact attempt bounds, finite inventory,
variant identities, cleanup failure handling and policy outcomes. They measure
**neither proxy overhead nor real Key Vault capacity**. No new dependencies,
shipping-path optimizations, baseline threshold changes or qualification-floor
changes are part of this delivery.
