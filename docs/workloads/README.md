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

`certification/authority.yaml` is the explicit, versioned evaluation contract.
Its `evaluated_as_of_utc` is an exact, deterministic UTC instant and cannot be
later than the trusted `UtcNow` used by validation. Every timestamped evidence,
approval, qualification, runtime, rollback, and revocation cutoff uses that
exact instant; generation never substitutes the wall clock into output.

At startup the authoritative generation and certification paths capture the
authority contract plus every canonical YAML input into one private immutable
byte snapshot. The normalized canonical hash is derived from those captured
bytes, and all parsing and evaluation reads the materialized snapshot rather
than returning to the working tree. The authority contract is captured and
parsed from the same snapshot but excluded from the canonical hash to avoid a
circular expected-hash dependency. A source edit after capture therefore
cannot alter the bytes being evaluated or published.

The contract separately pins
`expected_evaluator_implementation_revision`, a reproducible digest over all
GapDocs C# sources plus its project, repository build properties, and pinned SDK
definition. MSBuild computes the same normalized digest and embeds it as a
generated constant in the executing GapDocs assembly. Generation and
certification require the captured source digest, authority contract digest,
and embedded executing-assembly digest to match, so running a stale `--no-build`
binary fails closed. The generated identity source is excluded from its own
inputs to avoid a circular digest. This exclusion follows the resolved
`IntermediateOutputPath`, so custom in-project intermediate directories cannot
feed generated C# back into either the build-time or runtime source identity.
Current and stale intermediate roots are recognized only by a separate
`.workload-ga-evaluator-intermediate-root` marker with the evaluator's exact
reserved content. A source file merely named `WorkloadGaEvaluatorIdentity.g.cs`
does not classify its directory as generated or remove sibling sources from
compilation or identity hashing.

`expected_evaluator_schema_version` describes the authority evaluator format
only; it is not presented as implementation identity. Canonical or
implementation changes fail validation until their expected digests are
deliberately reviewed and updated. Paths and line endings are normalized before
hashing, so both identities are checkout-independent and reproducible.

`certify-workload` only issues authoritative output for regular, non-symlink
`.yaml` manifests under `docs/workloads`; those bytes are therefore part of the
canonical identity. `check-workload` remains available for non-authoritative
inspection of arbitrary manifests.

JSON compatibility is additive. `certify-workload --format json` remains a raw
schema-1 profile report, and `docs/site/workload-ga.json` remains an array of
schema-1 profile reports. Each report now carries optional `evaluation` and
`authority` properties; existing schema-1 consumers that ignore unknown fields
retain their original root shape and fields. The authority contract's own
`schema_version` is independent and does not change the report schema.

For current workload adoption, the generated workload certification has the
highest precedence. Profile manifests and gap docs are normative inputs,
release notes are immutable historical records of what was promoted at that
time, and guides are explanatory only. A historical GA release claim never
overrides a later `candidate`, `conditional`, or `blocked` certification
verdict. Because the certification is point-in-time, consumers must also check
`evaluated_as_of_utc`, the canonical-input revision, and the evaluator
implementation revision rather than treating any verdict as permanent.

Profile-specific adoption guidance:

- [S3 basic object CRUD](s3-basic-object-crud.md)
- [S3 metadata and compatibility controls](s3-metadata-compatibility.md)
- [SQS standard messaging](sqs-standard-messaging.md)
- [SQS dead-letter and redrive](sqs-dlq-redrive.md)
- [SQS FIFO messaging over AMQP](sqs-fifo-amqp.md)
- [Kinesis basic record ingestion](kinesis-basic-record-ingestion.md)
- [Kinesis single consumer per shard](kinesis-single-consumer-per-shard.md)
- [DynamoDB basic table and item CRUD](dynamodb-basic-crud.md)
- [DynamoDB Query, Scan, and secondary indexes](dynamodb-query-scan-indexes.md)
- [DynamoDB single-partition transactions](dynamodb-single-partition-transactions.md)
- [Secrets Manager basic lifecycle](secretsmanager-basic-lifecycle.md)
- [SNS standard publish (Service Bus Topics backend)](sns-standard-publish-service-bus.md)
- [SNS standard publish (Event Grid backend)](sns-standard-publish-event-grid.md)
- [SNS subscription management (Service Bus Topics backend)](sns-subscription-management-service-bus.md)
