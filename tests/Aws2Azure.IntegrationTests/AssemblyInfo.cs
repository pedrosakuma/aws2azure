// Six in-process fixtures in this assembly each boot their own
// WebApplicationFactory<Program> and mutate the process-global
// AWS2AZURE_CONFIG_FILE environment variable to point at their own config file
// (S3IntegrationFixture, DynamoDbIntegrationFixture, DynamoDbSprocFixture,
// ProxyHostFixture, SnsServiceBusProxyFixture, SqsEmulatorProxyFixture).
// Program.cs reads that variable once at host build, and WebApplicationFactory
// builds the host lazily, so two fixtures booting concurrently (xUnit runs
// distinct collections in parallel) could each capture the other's config —
// surfacing as e.g. "access key 'AKIA-IT-DDB' is not configured" when the
// DynamoDB proxy boots with the S3 fixture's config. Serialize the whole
// assembly so the env-var writes never interleave (issue #251). This mirrors
// the same guard already in the Conformance assembly.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
