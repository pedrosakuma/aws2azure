# Workload GA profiles

Each YAML file is a versioned support contract evaluated by:

```bash
dotnet run --project tools/Aws2Azure.GapDocs -- \
  certify-workload docs/workloads/s3-basic-object-crud.yaml --format json
```

The manifest declares the exact required operations, contextual workload
requirements, explicitly accepted partial operations and design gaps, maximum
real-Azure seal age, minimum proxy version, and required operational scenarios.
`evidence.required_real_azure_scenarios` identifies the subset (representative
load, restart, rollback, and profile-specific live behavior) that cannot be
satisfied by deterministic injection. Other required reliability scenarios may
use deterministic evidence, but never emulator evidence.
`evidence.qualification_artifact` remains empty until a reviewed,
production-shaped real-Azure artifact is committed under
`docs/workloads/evidence/` and referenced with its repository-relative path.

## Backend-specific evidence (`required_sub_feature_seals`)

Some operations accept more than one Azure backend behind the same AWS
operation name — for example SNS `Publish`/`PublishBatch` can route to Service
Bus Topics or Event Grid depending on configuration (issue #630). A single
`verified_real_azure` seal on the operation itself cannot prove that a
*specific* claimed backend was exercised: refreshing it by testing one backend
would otherwise silently keep every other profile claiming that operation
looking fresh. When a profile claims one specific backend, list the exact
documented sub-feature (from the operation's gap-doc `sub_features`) whose own
`verified_real_azure` seal must independently be fresh:

```yaml
required_sub_feature_seals:
  - operation: sns:Publish
    sub_feature: Event Grid publish path
  - operation: sns:PublishBatch
    sub_feature: Event Grid batch publish path
```

Each entry's `operation` must already be in the manifest's `operations` list,
and its `sub_feature` must match a `sub_features[].name` documented under that
operation. A missing or stale sub-feature seal yields the same `conditional`
verdict as a missing or stale operation-level seal, but attributes the finding
to the specific backend (`sns:Publish#Event Grid publish path`), not the
operation as a whole — see
[SNS standard publish (Service Bus Topics backend)](sns-standard-publish-service-bus.yaml)
and
[SNS standard publish (Event Grid backend)](sns-standard-publish-event-grid.yaml)
for two profiles that share every operation and requirement but require
different, independently-sealed sub-features.


## Approved-runtime ledger

`approved-runtimes/<profile-id>.yaml` records the exact sealed runtime status
for each profile. Approval is profile-owned even when multiple profiles share
the same producer artifact. Records are strict schema-versioned documents:
unknown fields, profile-version drift, malformed or inconsistent producer
identity, expired ephemeral artifacts, and invalid status/evidence combinations
fail gap-doc validation.

The first sealed runtime has a bootstrap paradox: it has no earlier approved
runtime to roll back to, so it can never be a qualified or approved candidate.
It is mechanically `promotion_eligible: false`, but policy may mark it
`rollback_baseline_eligible: true`. A later candidate with a distinct complete
runtime digest may deploy, prove rollback to that bootstrap, and include the
qualification evidence linking both digests. Only that later candidate can
become the first `approved` runtime for the profile. A revoked runtime is
eligible for neither promotion nor rollback.

The consumer resolves the prior from this committed profile record on every
load run. It verifies the exact GitHub run/attempt and artifact API identity,
upload digest, safe archive extraction, sealed manifest, executable and manifest
attestations, and ledger fields before launch. S3 and Secrets Manager may point
to the same bootstrap bytes, but their eligibility remains independently
profile-owned.

Verdicts are mechanical:

| Verdict | Meaning |
|---|---|
| `blocked` | A required operation is unsupported/stubbed, or a partial operation/design gap was not explicitly accepted. |
| `conditional` | Compatibility is accepted, but at least one required real-Azure seal is missing or stale. |
| `candidate` | Compatibility and seals pass, but matching operational qualification is absent, invalid, or not yet `qualified`. |
| `ga` | Compatibility, seal freshness, operation coverage, required scenarios, and a matching `qualified` artifact all pass. |

The default gap-doc generation writes the same aggregate verdicts to
`docs/site/workload-ga.md` and `docs/site/workload-ga.json`; CI evaluates every
manifest independently and rejects stale generated output.

Profile-specific adoption guidance:

- [SQS standard messaging](sqs-standard-messaging.md)
- [SNS standard publish (Service Bus Topics backend)](sns-standard-publish-service-bus.md)
- [SNS standard publish (Event Grid backend)](sns-standard-publish-event-grid.md)
