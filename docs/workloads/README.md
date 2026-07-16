# Workload GA profiles

Each YAML file is a versioned support contract evaluated by:

```bash
dotnet run --project tools/Aws2Azure.GapDocs -- \
  certify-workload docs/workloads/s3-basic-object-crud.yaml --format json
```

The manifest declares the exact required operations, contextual workload
requirements, explicitly accepted partial operations and design gaps, maximum
real-Azure seal age, minimum proxy version, and required operational scenarios.
`evidence.qualification_artifact` remains empty until a reviewed,
production-shaped real-Azure artifact is committed under
`docs/workloads/evidence/` and referenced with its repository-relative path.

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
