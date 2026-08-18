# S3 conformance goldens

Canonical golden responses for the S3 error matrix live here. The legacy
LocalStack capture path remains `<case>.golden`; additional provenances use
`<case>.<source>.golden` (for example `<case>.aws.golden`) so multiple
references for the same case can coexist without clobbering each other. Replay
always loads the most-authoritative committed golden for a case by provenance:
real AWS (`# source: aws`) > LocalStack (`# source: localstack`) > proxy-self
(`# source: proxy-self`).

These files are **captured from a real AWS implementation** (Tier 2: LocalStack;
later real AWS) — never hand-authored — and are stamped with their provenance
(`# source:`) in the file header. Emulator-derived goldens are a *necessary, not
sufficient* signal (see the emulator caveat in the repo conventions).

Until the Tier-2 capture job lands these files, the Tier-1 replay test still runs
and enforces the AWS **contract** (HTTP status + error `Code` + XML envelope) on
every PR; the golden faithfulness diff activates per-case as goldens appear.

Regenerate with record mode against the Tier-2 LocalStack fixture:

```bash
AWS2AZURE_CONFORMANCE_TIER2=1 AWS2AZURE_CONFORMANCE_RECORD=1 \
  dotnet test tests/Aws2Azure.Conformance --filter Conformance
```

The Tier-3 real-AWS capture job writes authoritative S3 goldens
through the same `GoldenStore.Save(...)` API, using
`GoldenProvenance.SourceRealAws` and a note explaining the file is the
authoritative oracle.
