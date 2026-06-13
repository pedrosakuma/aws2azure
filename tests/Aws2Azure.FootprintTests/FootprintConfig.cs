namespace Aws2Azure.FootprintTests;

/// <summary>
/// Renders a self-contained proxy config that enables every service module
/// with placeholder-but-valid Azure credential blocks. The proxy never dials
/// any backend at idle (modules connect lazily on the first request), so these
/// values only have to satisfy <c>ProxyConfigValidator</c> — they let the host
/// boot and serve <c>/_aws2azure/health</c> with all modules registered, which
/// is exactly the steady-state footprint a sidecar pays before traffic.
/// </summary>
internal static class FootprintConfig
{
    /// <summary>
    /// The service ids enabled by <see cref="AllModulesJson"/>, lower-cased to
    /// match each module's <c>ServiceName</c>.
    /// </summary>
    public static readonly IReadOnlyList<string> AllServices = new[]
    {
        "s3", "sqs", "dynamodb", "sns", "kinesis", "secretsmanager",
    };

    public const string AllModulesJson = """
        {
          "services": {
            "s3": { "enabled": true },
            "sqs": { "enabled": true },
            "dynamodb": { "enabled": true },
            "sns": { "enabled": true },
            "kinesis": { "enabled": true },
            "secretsmanager": { "enabled": true }
          },
          "sns": { "defaultBackend": "ServiceBusTopics" },
          "credentials": [
            {
              "awsAccessKeyId": "AKIA-FOOTPRINT-EXAMPLE",
              "awsSecretAccessKey": "footprint-secret-not-used",
              "azure": {
                "blob": {
                  "accountName": "devstoreaccount1",
                  "accountKey": "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==",
                  "serviceEndpoint": "http://127.0.0.1:10000/devstoreaccount1"
                },
                "serviceBus": {
                  "namespace": "http://127.0.0.1:5672/",
                  "sasKeyName": "RootManageSharedAccessKey",
                  "sasKey": "SAS_KEY_VALUE",
                  "transport": "Amqp"
                },
                "serviceBusTopics": {
                  "namespace": "sbemulatorns",
                  "endpoint": "http://127.0.0.1:5672/",
                  "managementEndpoint": "http://127.0.0.1:5300/",
                  "sasKeyName": "RootManageSharedAccessKey",
                  "sasKey": "SAS_KEY_VALUE"
                },
                "cosmos": {
                  "endpoint": "http://127.0.0.1:8081/",
                  "primaryKey": "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==",
                  "databaseName": "aws2azure"
                },
                "eventHubs": {
                  "namespace": "footprint-eh",
                  "endpoint": "https://footprint-eh.servicebus.windows.net/",
                  "sasKeyName": "RootManageSharedAccessKey",
                  "sasKey": "SAS_KEY_VALUE"
                },
                "keyVault": {
                  "vaultUrl": "https://footprint.vault.azure.net/",
                  "tenantId": "00000000-0000-0000-0000-000000000000",
                  "clientId": "00000000-0000-0000-0000-000000000000",
                  "clientSecret": "footprint-secret-not-used"
                }
              }
            }
          ]
        }
        """;
}
