using System.Text.Json;
using Aws2Azure.Core.Configuration;

namespace Aws2Azure.UnitTests.Configuration;

public class ProxyConfigJsonTests
{
    [Fact]
    public void Deserializes_full_shape()
    {
        const string json = """
        {
          "services": {
            "s3":  { "enabled": true },
            "sqs": { "enabled": false }
          },
          "credentials": [
            {
              "awsAccessKeyId": "AKIA1",
              "awsSecretAccessKey": "secret1",
              "azure": {
                "blob":       { "accountName": "acc1", "accountKey": "key1" },
                "serviceBus": { "namespace": "ns1",   "sasKeyName": "RootManageSharedAccessKey", "sasKey": "sb1" },
                "cosmos":     { "endpoint":  "https://x.documents.azure.com", "primaryKey": "cosmos1" }
              }
            }
          ]
        }
        """;

        var config = JsonSerializer.Deserialize(json, ProxyConfigJsonContext.Default.ProxyConfig);

        Assert.NotNull(config);
        Assert.True(config!.Services["s3"].Enabled);
        Assert.False(config.Services["sqs"].Enabled);

        var entry = Assert.Single(config.Credentials);
        Assert.Equal("AKIA1", entry.AwsAccessKeyId);
        Assert.Equal("secret1", entry.AwsSecretAccessKey);
        Assert.Equal("acc1", entry.Azure.Blob!.AccountName);
        Assert.Equal("ns1", entry.Azure.ServiceBus!.Namespace);
        Assert.Equal("https://x.documents.azure.com", entry.Azure.Cosmos!.Endpoint);
    }

    [Fact]
    public void Property_names_are_case_insensitive()
    {
        const string json = """
        { "Credentials": [ { "AWSAccessKeyId": "AKIA", "awsSecretAccessKey": "s", "azure": {} } ] }
        """;

        var config = JsonSerializer.Deserialize(json, ProxyConfigJsonContext.Default.ProxyConfig);
        Assert.Equal("AKIA", Assert.Single(config!.Credentials).AwsAccessKeyId);
    }
}
