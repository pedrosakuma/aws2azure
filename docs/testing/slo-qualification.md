# Workload SLO qualification artifacts

Workload GA uses a versioned qualification artifact rather than treating every
performance result as the same kind of evidence. The contract has three
artifact kinds:

| Artifact kind | Purpose | Permitted claim |
|---|---|---|
| `emulator_regression` | Detect proxy overhead regressions against the same local emulator and host shape | Proxy regression only; never Azure capacity |
| `real_azure_workload_qualification` | Qualify a specific candidate and workload profile against production-shaped Azure resources | Workload SLO candidate or qualification |
| `ab_experiment` | Compare an opt-in implementation variant such as CosmosBinary or numeric LSI ordering | Report only; never a workload gate by itself |

Validate an artifact with:

```bash
dotnet run --project tools/Aws2Azure.GapDocs -- \
  validate-qualification qualification.yaml
```

Generate an emulator regression artifact from the committed perf contract and a
machine-readable run snapshot with:

```bash
dotnet run --project tools/Aws2Azure.GapDocs -- \
  generate-emulator-qualification \
  --reference docs/perf/baseline-reference.json \
  --latest docs/perf/baseline-latest.json \
  --output qualification.yaml \
  --run-id "$GITHUB_RUN_ID" \
  --run-url "$GITHUB_SERVER_URL/$GITHUB_REPOSITORY/actions/runs/$GITHUB_RUN_ID" \
  --git-sha "$GITHUB_SHA" \
  --artifact-digest "sha256:<published-binary-or-image>" \
  --config-digest "sha256:<resolved-non-secret-perf-config>"
```

The adapter emits absolute throughput, latency, and resource signals plus the
configured proxy-to-SDK throughput/p50/p99 ratios. Missing, stale, untracked,
zero-completion, or unevaluable measurements are retained as explicit
`findings`; they are not silently converted into passing evidence.

Generate a real-Azure workload candidate from the conformance evidence for an
explicit operation set with:

```bash
dotnet run --project tools/Aws2Azure.GapDocs -- \
  generate-real-azure-workload-qualification \
  --evidence TestResults/evidence/real-azure-evidence.json \
  --output TestResults/real-azure-workload-qualification.yaml \
  --profile-id s3-basic-object-crud \
  --profile-version 1 \
  --operation s3:CreateBucket \
  --operation s3:PutObject \
  --operation s3:GetObject \
  --git-sha "$GITHUB_SHA" \
  --artifact-digest "sha256:<published-binary-or-image>" \
  --config-digest "sha256:<resolved-non-secret-config>" \
  --region eastus2 \
  --backend-description "Blob Storage Standard_LRS"
```

This adapter classifies correctness evidence only. Passing real-Azure
conformance produces `candidate` with a blocking `load_evidence_missing`
finding; skipped or absent operation evidence produces `inconclusive`, and a
failed required conformance scenario produces `blocked`. It never emits
`qualified`: that verdict requires a later production-shaped load artifact with
reviewed thresholds, sample counts, duration, and capacity signals.

Candidate scenario IDs are stable. Matrix IDs are preserved for core scenarios,
while operational categories are normalized to the matching workload manifest
IDs when the command receives `--manifest` (`throttling`, `timeout`,
`service-unavailable`, `cancellation`, `retry-exhaustion`, `restart`,
`rollback`, and `concurrency`). A conformance scenario shared by several
operations is emitted once, never as a synthetic `conformance-###` row. This
lets later qualification evidence and GA manifests refer to the same identities
without claiming that correctness evidence is capacity evidence.

The nightly workflow generates one candidate per workload manifest and uploads
it with two immutable siblings:

- `runtime-sha256.txt`: sorted hashes of the complete proxy runtime output;
- `config-manifest.json`: the non-secret region, deployment inputs, selected
  conformance plan, and hashes of the matrix, Bicep, and workflow.

The candidate's `artifact_digest` and `config_digest` are hashes of those files.
The manifests are evidence, not substitutes for production-shaped load.

Generate a final qualification from a reviewed policy, a correctness candidate,
and repeated immutable load evidence with:

```bash
dotnet run --project tools/Aws2Azure.GapDocs -- \
  generate-real-azure-load-qualification \
  --manifest docs/workloads/s3-basic-object-crud.yaml \
  --candidate artifacts/correctness/s3-basic-object-crud.yaml \
  --policy docs/workloads/qualification/s3-basic-object-crud.yaml \
  --evidence artifacts/load/run-1/load-evidence.json \
  --evidence artifacts/load/run-2/load-evidence.json \
  --evidence artifacts/load/run-3/load-evidence.json \
  --output artifacts/qualification/s3-basic-object-crud.yaml \
  --trend-output artifacts/qualification/s3-basic-object-crud-trend.csv \
  --run-id "$GITHUB_RUN_ID" \
  --run-url "$GITHUB_SERVER_URL/$GITHUB_REPOSITORY/actions/runs/$GITHUB_RUN_ID" \
  --run-attempt "$GITHUB_RUN_ATTEMPT"
```

Every load evidence file represents one immutable GitHub Actions run/attempt
and carries the exact candidate/config digests, region/SKU description, run
window, scenario counts, and raw measured signals. The generator rejects drift
between runs, duplicate attempts, missing required scenarios, zero-completion
runs, insufficient samples/duration, excessive failures, and threshold misses.
It aggregates capacity gates conservatively (the worst run wins) rather than
averaging a bad run away. A valid policy must include a blocking real-Azure
backend-capacity throughput or latency threshold; emulator floors and A/B ratios
cannot be reused.

Minimal runner output (values are measurements, never policy thresholds):

```json
{
  "schema_version": 1,
  "profile": {
    "id": "s3-basic-object-crud",
    "version": 1,
    "services": [{ "service": "s3", "operations": ["PutObject"] }]
  },
  "candidate": {
    "git_sha": "0123456789abcdef",
    "artifact_digest": "sha256:runtime-manifest",
    "config_digest": "sha256:config-manifest"
  },
  "provenance": {
    "run_id": "123451",
    "run_url": "https://github.com/pedrosakuma/aws2azure/actions/runs/123451",
    "run_attempt": 1,
    "generated_at_utc": "2026-07-16T15:35:00Z",
    "window_start_utc": "2026-07-16T15:30:00Z",
    "window_end_utc": "2026-07-16T15:35:00Z",
    "region": "eastus2",
    "backend_description": "Blob Storage Standard_LRS"
  },
  "scenarios": [{
    "id": "representative-load",
    "service": "s3",
    "operation": "PutObject",
    "evidence_source": "real_azure",
    "completions": 1000,
    "failures": 0,
    "skipped": 0,
    "duration_seconds": 300,
    "captured_at_utc": "2026-07-16T15:35:00Z"
  }],
  "signals": [{
    "id": "representative-load-p99",
    "scenario_id": "representative-load",
    "metric": "p99_ms",
    "measured_value": 420,
    "samples": 1000,
    "captured_at_utc": "2026-07-16T15:35:00Z"
  }]
}
```

The manual
[`qualification-real-azure`](../../.github/workflows/qualification-real-azure.yml)
workflow downloads a correctness candidate and multiple
`real-azure-workload-load-<profile>` artifacts by immutable run id, emits the
qualification plus a workload-only CSV trend, and fails unless the verdict is
`qualified`. It never downloads `perf-results` (emulator regression) or
`perf-real-azure-results` (A/B experiments).

## Version 1 shape

```yaml
schema_version: 1
artifact_kind: real_azure_workload_qualification
verdict: qualified

profile:
  id: s3-basic-object-crud
  version: 1
  services:
    - service: s3
      operations: [CreateBucket, PutObject, GetObject, DeleteObject, DeleteBucket]

candidate:
  git_sha: 0123456789abcdef
  artifact_digest: sha256:binary-or-image
  config_digest: sha256:resolved-non-secret-config

provenance:
  run_id: "123456"
  run_url: https://github.com/pedrosakuma/aws2azure/actions/runs/123456
  run_attempt: 1
  generated_at_utc: 2026-07-16T16:00:00Z
  window_start_utc: 2026-07-16T15:40:00Z
  window_end_utc: 2026-07-16T15:59:00Z
  region: eastus2
  backend_description: Blob Storage Standard_LRS
  correctness_run:
    run_id: "123450"
    run_url: https://github.com/pedrosakuma/aws2azure/actions/runs/123450
    run_attempt: 1
    window_start_utc: 2026-07-16T15:35:00Z
    window_end_utc: 2026-07-16T15:39:00Z
    git_sha: 0123456789abcdef
    artifact_digest: sha256:binary-or-image
    config_digest: sha256:resolved-non-secret-config
  source_runs:
    - run_id: "123451"
      run_url: https://github.com/pedrosakuma/aws2azure/actions/runs/123451
      run_attempt: 1
      window_start_utc: 2026-07-16T15:40:00Z
      window_end_utc: 2026-07-16T15:45:00Z
      git_sha: 0123456789abcdef
      artifact_digest: sha256:binary-or-image
      config_digest: sha256:resolved-non-secret-config
    - run_id: "123452"
      run_url: https://github.com/pedrosakuma/aws2azure/actions/runs/123452
      run_attempt: 1
      window_start_utc: 2026-07-16T15:47:00Z
      window_end_utc: 2026-07-16T15:52:00Z
      git_sha: 0123456789abcdef
      artifact_digest: sha256:binary-or-image
      config_digest: sha256:resolved-non-secret-config
    - run_id: "123453"
      run_url: https://github.com/pedrosakuma/aws2azure/actions/runs/123453
      run_attempt: 1
      window_start_utc: 2026-07-16T15:54:00Z
      window_end_utc: 2026-07-16T15:59:00Z
      git_sha: 0123456789abcdef
      artifact_digest: sha256:binary-or-image
      config_digest: sha256:resolved-non-secret-config

rules:
  max_artifact_age_hours: 72
  min_samples_per_scenario: 100
  min_duration_seconds: 300
  max_failure_rate: 0.001
  zero_completions_disqualify: true
  only_skipped_real_azure_disqualifies: true
  min_distinct_runs: 3

signals:
  - id: latency-p99
    scenario_id: put-object
    source: backend_capacity
    disposition: blocking
    metric: p99_ms
    max_value: 1000
    measured_value: 420
    samples: 1000
    captured_at_utc: 2026-07-16T15:59:00Z
  - id: network-p95
    scenario_id: put-object
    source: network_noise
    disposition: report_only
    metric: p95_ms
    measured_value: 35
    samples: 1000
    captured_at_utc: 2026-07-16T15:59:00Z

scenarios:
  - id: put-object
    service: s3
    operation: PutObject
    evidence_source: real_azure
    completions: 1000
    failures: 0
    skipped: 0
    duration_seconds: 300
    captured_at_utc: 2026-07-16T15:59:00Z

findings:
  - code: untracked_scenario
    disposition: blocking
    message: "Latest results contain a scenario absent from baseline-reference.json"
```

## Validation rules

- `qualified` real-Azure artifacts require immutable candidate and run
  provenance, every repeated source run/attempt, a region/backend description,
  fresh measurements, minimum samples and duration, a blocking backend-capacity
  throughput or latency signal, and at least one real-Azure scenario.
- Zero completions, only skipped live evidence, stale measurements, insufficient
  samples/duration, or a failure rate above the published maximum prevent a
  `qualified` verdict.
- `emulator_regression` signals may only use `proxy_overhead`. The committed
  emulator baseline remains a regression ruler and cannot establish Azure
  throughput or latency capacity.
- `ab_experiment` signals are always `report_only` and cannot declare thresholds.
- Signal disposition is explicit: `blocking`, `advisory`, or `report_only`.
  Signal source is also explicit: `proxy_overhead`, `backend_capacity`, or
  `network_noise`.
- Findings preserve adapter-level reasons that cannot be represented as a
  numeric signal. A blocking finding prevents a passing or `qualified` verdict;
  each adapter maps it to the artifact-specific non-success state (`candidate`,
  `inconclusive`, `blocked`, or `failed`).
- A non-qualified real-Azure candidate may contain correctness scenarios and
  blocking findings without numeric signals. `qualified` still requires
  blocking real-Azure capacity signals and all workload gates.

This contract does not choose profile thresholds. Thresholds are introduced by
reviewed profile artifacts after anomalous baseline results are investigated;
they are never raised merely to make a run green.
