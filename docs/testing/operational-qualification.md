# Operational workload qualification runbook

Operational qualification is separate from emulator regression and from
feature-specific A/B experiments.

## Evidence classes

| Evidence | Used for | Never claims |
|---|---|---|
| Emulator `perf.yml` qualification | Proxy-overhead regression | Azure capacity or workload GA |
| Real-Azure workload load evidence | Profile capacity/reliability under a production-shaped topology | A/B feature causality |
| `perf-real-azure.yml` | CosmosBinary/LSI A/B falsification | Workload qualification |

## Required scenario semantics

- **throttling, timeout, service-unavailable, cancellation, retry-exhaustion**:
  deterministic injection is acceptable when it proves the AWS-native envelope,
  cancellation propagation, and bounded SDK retry behavior. It is not reported
  as a naturally observed Azure incident.
- **concurrency/read-after-write/redelivery**: live Azure when required by the
  profile manifest.
- **restart**: write state through the official AWS SDK, terminate the
  out-of-process proxy, restart the same sealed runtime/config, and read or
  address the pre-existing Azure state.
- **rollback**: deploy the sealed candidate, create/read canary state, replace it
  with the previously approved sealed runtime without changing the backend, and
  verify the same state plus cleanup. A source build of "main" or a config-only
  restart is not rollback evidence.

The real-Azure conformance matrix includes deterministic retry exhaustion and
live restart checks for the initial candidate profiles. Rollback remains an
external deployment action because the repository cannot manufacture a
previously approved immutable artifact.

## Reproduce

1. Run `integration-real-azure` for the candidate SHA. Retain the
   `real-azure-conformance` artifact containing correctness candidates,
   `runtime-sha256.txt`, and `config-manifest.json`.
2. Execute the profile's production-shaped load job at least the policy's
   `min_distinct_runs`. Each run must upload exactly one
   `real-azure-workload-load-<profile>/load-evidence.json`; do not reuse a run id
   or mix candidate/config digests, regions, SKUs, emulator results, or A/B arms.
3. Complete the rollback action above and include its scenario row in every
   evidence bundle required by the reviewed policy.
4. Dispatch `qualification-real-azure` with the correctness run id, all load run
   ids, and the committed policy path.
5. Download the emitted YAML and CSV. Review blocking findings and the worst-run
   capacity values. Do not average away a breach, raise a threshold, or treat a
   rerun as resolution.
6. After review, commit the immutable YAML below `docs/workloads/evidence/` and
   reference it from the matching workload manifest. The GA evaluator verifies
   profile/operation/scenario/source coherence and freshness.

## Interpretation

`qualified` means every required scenario and blocking capacity/reliability gate
passed across the minimum distinct immutable runs. `candidate` means evidence is
missing, inconsistent, stale, insufficient, or failed; the findings state the
exact reason. External Azure throttling and network-noise signals remain visible
but cannot silently replace a failed backend-capacity gate.
