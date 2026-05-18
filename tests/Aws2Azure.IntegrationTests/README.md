# Aws2Azure.IntegrationTests

Integration tests for the proxy. They are isolated from the unit-test project
so they can:

- Spin up real emulator containers (Azurite via Testcontainers).
- Boot the proxy in-process via `WebApplicationFactory<Program>`.
- Drive real AWS clients (boto3) against the proxy.

## Running locally

```bash
# .NET tests (uses Testcontainers — needs Docker reachable)
dotnet test tests/Aws2Azure.IntegrationTests

# Full emulator stack for manual exploration
docker compose -f deploy/docker-compose.yml up

# boto3 smoke test (against a running proxy)
PROXY_URL=http://127.0.0.1:5099 python tests/Aws2Azure.IntegrationTests/Boto3/smoke.py
```

If Docker isn't reachable, the Azurite-backed tests are skipped automatically
via `SkippableFact`. The in-process proxy tests do not require Docker.

## Emulator caveat

Emulators (Azurite, Service Bus, Cosmos) **do not match** the production Azure
surface in every edge case. Any divergence observed by an integration test
must be recorded in the relevant `docs/gaps/<service>/<Operation>.yaml` under
`behavior_differences:` before the operation is allowed to graduate from
`status: stub` to `partial`/`implemented`.
