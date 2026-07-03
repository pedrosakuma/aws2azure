using System.Text.Json;
using Aws2Azure.Core.Configuration;

namespace Aws2Azure.UnitTests.Configuration;

/// <summary>
/// Deserialization + translation tests for the binding-centric on-disk schema
/// (<see cref="ConfigDocument"/>) into the resolved <see cref="ProxyConfig"/> model.
/// </summary>
public class ProxyConfigJsonTests
{
    private static ProxyConfig Translate(string json)
    {
        var document = JsonSerializer.Deserialize(json, ConfigDocumentJsonContext.Default.ConfigDocument);
        Assert.NotNull(document);
        return ConfigDocumentTranslator.ToProxyConfig(document!);
    }

    [Fact]
    public void Deserializes_full_shape()
    {
        const string json = """
        {
          "services": {
            "s3":  { "enabled": true },
            "sqs": { "enabled": false },
            "sns": { "enabled": true, "defaultBackend": "EventGrid" }
          },
          "bindings": [
            {
              "aws": { "accessKeyId": "AKIA1", "secretAccessKey": "secret1" },
              "azure": {
                "s3":       { "kind": "blob",       "target": { "accountName": "acc1" }, "auth": { "mode": "sharedKey", "key": "key1" } },
                "sqs":      { "kind": "serviceBus", "target": { "namespace": "ns1" },    "auth": { "mode": "sas", "keyName": "RootManageSharedAccessKey", "key": "sb1" } },
                "dynamodb": { "kind": "cosmos",     "target": { "endpoint": "https://x.documents.azure.com", "databaseName": "db1", "preferredRegions": [ "West US", "East US" ] }, "auth": { "mode": "sharedKey", "key": "cosmos1" } }
              }
            }
          ]
        }
        """;

        var config = Translate(json);

        Assert.True(config.Services["s3"].Enabled);
        Assert.False(config.Services["sqs"].Enabled);
        Assert.Equal(SnsTopicBackend.EventGrid, config.Sns.DefaultBackend);

        var entry = Assert.Single(config.Credentials);
        Assert.Equal("AKIA1", entry.AwsAccessKeyId);
        Assert.Equal("secret1", entry.AwsSecretAccessKey);
        Assert.Equal("acc1", entry.Azure.Blob!.AccountName);
        Assert.Equal("key1", entry.Azure.Blob.AccountKey);
        Assert.Equal("ns1", entry.Azure.ServiceBus!.Namespace);
        Assert.Equal("sb1", entry.Azure.ServiceBus.SasKey);
        Assert.Equal("https://x.documents.azure.com", entry.Azure.Cosmos!.Endpoint);
        Assert.Equal("db1", entry.Azure.Cosmos.DatabaseName);
        Assert.Equal("cosmos1", entry.Azure.Cosmos.PrimaryKey);
        Assert.Equal(new[] { "West US", "East US" }, entry.Azure.Cosmos.PreferredRegions);
    }

    [Fact]
    public void Property_names_are_case_insensitive()
    {
        const string json = """
        { "Bindings": [ { "AWS": { "AccessKeyId": "AKIA", "secretAccessKey": "s" }, "azure": {} } ] }
        """;

        var config = Translate(json);
        Assert.Equal("AKIA", Assert.Single(config.Credentials).AwsAccessKeyId);
    }

    [Fact]
    public void Deserializes_auth_mode_with_camel_case_enum_value()
    {
        const string json = """
        {
          "bindings": [
            {
              "aws": { "accessKeyId": "AKIA", "secretAccessKey": "secret" },
              "azure": {
                "kinesis": {
                  "kind": "eventHubs",
                  "target": { "namespace": "myns" },
                  "auth": { "mode": "managedIdentity" }
                }
              }
            }
          ]
        }
        """;

        var config = Translate(json);

        Assert.Equal(AzureAuthMode.ManagedIdentity, Assert.Single(config.Credentials).Azure.EventHubs!.AuthMode);
    }

    [Fact]
    public void Deserializes_azure_identities_pool_and_identity_reference()
    {
        const string json = """
        {
          "azureIdentities": {
            "prod-mi": { "authMode": "managedIdentity", "clientId": "user-mi-client" }
          },
          "bindings": [
            {
              "aws": { "accessKeyId": "AKIA", "secretAccessKey": "secret" },
              "azure": {
                "dynamodb": {
                  "kind": "cosmos",
                  "target": { "endpoint": "https://acct.documents.azure.com", "databaseName": "orders" },
                  "auth": { "mode": "reference", "identity": "prod-mi" }
                }
              }
            }
          ]
        }
        """;

        var config = Translate(json);

        var identity = Assert.Contains("prod-mi", config.AzureIdentities!);
        Assert.Equal(AzureAuthMode.ManagedIdentity, identity.AuthMode);
        Assert.Equal("user-mi-client", identity.ClientId);
        Assert.Equal("prod-mi", Assert.Single(config.Credentials).Azure.Cosmos!.Identity);
    }

    [Theory]
    // Blob is shared-key only.
    [InlineData("s3", "blob", "clientSecret")]
    [InlineData("s3", "blob", "sas")]
    // Service Bus queues are SAS only.
    [InlineData("sqs", "serviceBus", "sharedKey")]
    [InlineData("sqs", "serviceBus", "managedIdentity")]
    // Cosmos is shared-key or AAD, never SAS.
    [InlineData("dynamodb", "cosmos", "sas")]
    // Event Hubs is SAS or AAD, never shared-key.
    [InlineData("kinesis", "eventHubs", "sharedKey")]
    // Key Vault is AAD only.
    [InlineData("secretsmanager", "keyVault", "sharedKey")]
    [InlineData("secretsmanager", "keyVault", "sas")]
    public void Rejects_auth_mode_not_valid_for_backend(string service, string kind, string mode)
    {
        var json = $$"""
        {
          "bindings": [
            {
              "aws": { "accessKeyId": "AKIA", "secretAccessKey": "secret" },
              "azure": {
                "{{service}}": { "kind": "{{kind}}", "target": {}, "auth": { "mode": "{{mode}}" } }
              }
            }
          ]
        }
        """;

        var ex = Assert.Throws<ProxyConfigException>(() => Translate(json));
        Assert.Contains($"bindings[0].azure.{service}.auth.mode", ex.Message);
    }
}
