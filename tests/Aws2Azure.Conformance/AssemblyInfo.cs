// The conformance assembly mutates the process-global AWS2AZURE_CONFIG_FILE
// environment variable from two distinct collection/class fixtures (the Tier-1
// ConformanceProxyFixture with its dummy Blob config, and the Tier-2
// S3BackendDifferentialFixture with a live Azurite serviceEndpoint). Program.cs
// reads that variable once at host build, so two fixtures booting their own
// WebApplicationFactory<Program> concurrently could each pick up the other's
// config. Serialize the whole assembly so the env-var writes never interleave.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
