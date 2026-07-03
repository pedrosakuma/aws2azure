using Aws2Azure.Core.Configuration;

namespace Aws2Azure.UnitTests.Configuration;

public class ProxyConfigLoaderTests : IDisposable
{
    private readonly string _tempFile;

    public ProxyConfigLoaderTests()
    {
        _tempFile = Path.Combine(Path.GetTempPath(), $"aws2azure-test-{Guid.NewGuid():N}.json");
    }

    public void Dispose()
    {
        if (File.Exists(_tempFile))
        {
            File.Delete(_tempFile);
        }
    }

    [Fact]
    public void Loads_from_json_file()
    {
        File.WriteAllText(_tempFile, """
        {
          "services": { "s3": { "enabled": true } },
          "bindings": [ {
            "aws": { "accessKeyId": "AKIA", "secretAccessKey": "s" },
            "azure": { "s3": { "kind": "blob", "target": { "accountName": "a" }, "auth": { "mode": "sharedKey", "key": "k" } } }
          } ]
        }
        """);

        var config = ProxyConfigLoader.Load(_tempFile, envVars: new Dictionary<string, string?>());

        Assert.True(config.Services["s3"].Enabled);
        Assert.Equal("AKIA", Assert.Single(config.Credentials).AwsAccessKeyId);
    }

    [Fact]
    public void Returns_empty_config_when_file_missing_and_no_env()
    {
        var config = ProxyConfigLoader.Load(
            jsonFilePath: Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid():N}.json"),
            envVars: new Dictionary<string, string?>());

        Assert.Empty(config.Services);
        Assert.Empty(config.Credentials);
    }

    [Fact]
    public void Env_vars_override_json_scalars()
    {
        File.WriteAllText(_tempFile, """
        {
          "services": { "s3": { "enabled": false } },
          "bindings": [ {
            "aws": { "accessKeyId": "AKIA-FILE", "secretAccessKey": "secret-file" },
            "azure": { "s3": { "kind": "blob", "target": { "accountName": "acc-file" }, "auth": { "mode": "sharedKey", "key": "key-file" } } }
          } ]
        }
        """);

        var env = new Dictionary<string, string?>
        {
            ["AWS2AZURE__SERVICES__S3__ENABLED"]                       = "true",
            ["AWS2AZURE__BINDINGS__0__AWS__ACCESSKEYID"]               = "AKIA-ENV",
            ["AWS2AZURE__BINDINGS__0__AZURE__S3__TARGET__ACCOUNTNAME"] = "acc-env",
        };

        var config = ProxyConfigLoader.Load(_tempFile, env);

        Assert.True(config.Services["s3"].Enabled);
        var entry = Assert.Single(config.Credentials);
        Assert.Equal("AKIA-ENV", entry.AwsAccessKeyId);
        Assert.Equal("secret-file", entry.AwsSecretAccessKey);
        Assert.Equal("acc-env", entry.Azure.Blob!.AccountName);
        Assert.Equal("key-file", entry.Azure.Blob.AccountKey);
    }

    [Fact]
    public void Env_vars_can_introduce_new_binding()
    {
        var env = new Dictionary<string, string?>
        {
            ["AWS2AZURE__BINDINGS__0__AWS__ACCESSKEYID"]            = "AKIA-NEW",
            ["AWS2AZURE__BINDINGS__0__AWS__SECRETACCESSKEY"]        = "secret-new",
            ["AWS2AZURE__BINDINGS__0__AZURE__SQS__KIND"]            = "serviceBus",
            ["AWS2AZURE__BINDINGS__0__AZURE__SQS__TARGET__NAMESPACE"] = "ns",
            ["AWS2AZURE__BINDINGS__0__AZURE__SQS__AUTH__KEYNAME"]   = "RootManageSharedAccessKey",
            ["AWS2AZURE__BINDINGS__0__AZURE__SQS__AUTH__KEY"]       = "sb-key",
        };

        var config = ProxyConfigLoader.Load(jsonFilePath: null, env);

        var entry = Assert.Single(config.Credentials);
        Assert.Equal("AKIA-NEW", entry.AwsAccessKeyId);
        Assert.Equal("ns", entry.Azure.ServiceBus!.Namespace);
        Assert.Equal("sb-key", entry.Azure.ServiceBus.SasKey);
    }

    [Fact]
    public void Env_var_overrides_dynamodb_consistency_check()
    {
        var env = new Dictionary<string, string?>
        {
            ["AWS2AZURE__SERVICES__DYNAMODB__CONSISTENCYCHECK"] = "required",
        };

        var config = ProxyConfigLoader.Load(jsonFilePath: null, env);

        Assert.Equal(ConsistencyCheckMode.Required, config.DynamoDb.ConsistencyCheck);
    }

    [Fact]
    public void Env_vars_can_set_cosmos_preferred_regions()
    {
        var env = new Dictionary<string, string?>
        {
            ["AWS2AZURE__BINDINGS__0__AZURE__DYNAMODB__KIND"] = "cosmos",
            ["AWS2AZURE__BINDINGS__0__AZURE__DYNAMODB__TARGET__ENDPOINT"] = "https://acct.documents.azure.com/",
            ["AWS2AZURE__BINDINGS__0__AZURE__DYNAMODB__TARGET__DATABASENAME"] = "main",
            ["AWS2AZURE__BINDINGS__0__AZURE__DYNAMODB__AUTH__KEY"] = "key",
            ["AWS2AZURE__BINDINGS__0__AZURE__DYNAMODB__TARGET__PREFERREDREGIONS__0"] = "West US",
            ["AWS2AZURE__BINDINGS__0__AZURE__DYNAMODB__TARGET__PREFERREDREGIONS__1"] = "East US",
        };

        var config = ProxyConfigLoader.Load(jsonFilePath: null, env);

        var regions = Assert.Single(config.Credentials).Azure.Cosmos!.PreferredRegions;
        Assert.Equal(new[] { "West US", "East US" }, regions);
    }

    [Fact]
    public void Env_var_ignores_invalid_dynamodb_consistency_check()
    {
        var env = new Dictionary<string, string?>
        {
            ["AWS2AZURE__SERVICES__DYNAMODB__CONSISTENCYCHECK"] = "banana",
        };

        var config = ProxyConfigLoader.Load(jsonFilePath: null, env);

        Assert.Equal(ConsistencyCheckMode.Disabled, config.DynamoDb.ConsistencyCheck);
    }

    [Fact]
    public void Env_var_overrides_dynamodb_cosmos_binary_requests()
    {
        var env = new Dictionary<string, string?>
        {
            ["AWS2AZURE__SERVICES__DYNAMODB__COSMOSBINARYREQUESTS"] = "true",
        };

        var config = ProxyConfigLoader.Load(jsonFilePath: null, env);

        Assert.True(config.DynamoDb.CosmosBinaryRequests);
    }

    [Fact]
    public void Cosmos_binary_requests_defaults_off()
    {
        var config = ProxyConfigLoader.Load(jsonFilePath: null, envVars: new Dictionary<string, string?>());

        Assert.False(config.DynamoDb.CosmosBinaryRequests);
    }

    [Fact]
    public void Throws_on_malformed_json()
    {
        File.WriteAllText(_tempFile, "{ this is not valid json");

        Assert.ThrowsAny<System.Text.Json.JsonException>(
            () => ProxyConfigLoader.Load(_tempFile, envVars: new Dictionary<string, string?>()));
    }
}
