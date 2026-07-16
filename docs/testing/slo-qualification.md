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
  generated_at_utc: 2026-07-16T16:00:00Z
  window_start_utc: 2026-07-16T15:50:00Z
  window_end_utc: 2026-07-16T15:59:00Z
  region: eastus2
  backend_description: Blob Storage Standard_LRS

rules:
  max_artifact_age_hours: 72
  min_samples_per_scenario: 100
  min_duration_seconds: 300
  max_failure_rate: 0.001
  zero_completions_disqualify: true
  only_skipped_real_azure_disqualifies: true

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
```

## Validation rules

- `qualified` real-Azure artifacts require immutable candidate and run
  provenance, a region/backend description, fresh measurements, minimum samples
  and duration, at least one blocking signal, and at least one real-Azure
  scenario.
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

This contract does not choose profile thresholds. Thresholds are introduced by
reviewed profile artifacts after anomalous baseline results are investigated;
they are never raised merely to make a run green.
