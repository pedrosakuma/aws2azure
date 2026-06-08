# S3 conformance goldens

Canonical golden responses for the S3 error matrix live here as `<case>.golden`
files. They are **captured from a real AWS implementation** (Tier 2: LocalStack;
later real AWS) — never hand-authored — and are stamped with their provenance
(`# source:`) in the file header. Emulator-derived goldens are a *necessary, not
sufficient* signal (see the emulator caveat in the repo conventions).

Until the Tier-2 capture job lands these files, the Tier-1 replay test still runs
and enforces the AWS **contract** (HTTP status + error `Code` + XML envelope) on
every PR; the golden faithfulness diff activates per-case as goldens appear.

Regenerate with record mode against the Tier-2 LocalStack fixture:

```bash
AWS2AZURE_CONFORMANCE_RECORD=1 dotnet test tests/Aws2Azure.IntegrationTests --filter Conformance
```
