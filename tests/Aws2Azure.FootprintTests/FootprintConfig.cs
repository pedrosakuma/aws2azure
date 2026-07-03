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
            "sns": { "enabled": true, "defaultBackend": "ServiceBusTopics" },
            "kinesis": { "enabled": true },
            "secretsmanager": { "enabled": true }
          },
          "bindings": [
            {
              "aws": {
                "accessKeyId": "AKIA-FOOTPRINT-EXAMPLE",
                "secretAccessKey": "footprint-secret-not-used"
              },
              "azure": {
                "s3": {
                  "kind": "blob",
                  "target": {
                    "accountName": "devstoreaccount1",
                    "endpoint": "http://127.0.0.1:10000/devstoreaccount1"
                  },
                  "auth": {
                    "mode": "sharedKey",
                    "key": "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw=="
                  }
                },
                "sqs": {
                  "kind": "serviceBus",
                  "target": {
                    "namespace": "http://127.0.0.1:5672/",
                    "transport": "Amqp"
                  },
                  "auth": {
                    "mode": "sas",
                    "keyName": "RootManageSharedAccessKey",
                    "key": "SAS_KEY_VALUE"
                  }
                },
                "sns": {
                  "kind": "serviceBusTopics",
                  "target": {
                    "namespace": "sbemulatorns",
                    "endpoint": "http://127.0.0.1:5672/",
                    "managementEndpoint": "http://127.0.0.1:5300/"
                  },
                  "auth": {
                    "mode": "sas",
                    "keyName": "RootManageSharedAccessKey",
                    "key": "SAS_KEY_VALUE"
                  }
                },
                "dynamodb": {
                  "kind": "cosmos",
                  "target": {
                    "endpoint": "http://127.0.0.1:8081/",
                    "databaseName": "aws2azure"
                  },
                  "auth": {
                    "mode": "sharedKey",
                    "key": "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw=="
                  }
                },
                "kinesis": {
                  "kind": "eventHubs",
                  "target": {
                    "namespace": "footprint-eh",
                    "endpoint": "https://footprint-eh.servicebus.windows.net/"
                  },
                  "auth": {
                    "mode": "sas",
                    "keyName": "RootManageSharedAccessKey",
                    "key": "SAS_KEY_VALUE"
                  }
                },
                "secretsmanager": {
                  "kind": "keyVault",
                  "target": {
                    "vaultUrl": "https://footprint.vault.azure.net/"
                  },
                  "auth": {
                    "mode": "clientSecret",
                    "tenantId": "00000000-0000-0000-0000-000000000000",
                    "clientId": "00000000-0000-0000-0000-000000000000",
                    "clientSecret": "footprint-secret-not-used"
                  }
                }
              }
            }
          ]
        }
        """;
}
