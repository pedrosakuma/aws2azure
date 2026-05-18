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
          "credentials": [ { "awsAccessKeyId": "AKIA", "awsSecretAccessKey": "s",
                             "azure": { "blob": { "accountName": "a", "accountKey": "k" } } } ]
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
          "credentials": [ { "awsAccessKeyId": "AKIA-FILE", "awsSecretAccessKey": "secret-file",
                             "azure": { "blob": { "accountName": "acc-file", "accountKey": "key-file" } } } ]
        }
        """);

        var env = new Dictionary<string, string?>
        {
            ["AWS2AZURE__SERVICES__S3__ENABLED"]                    = "true",
            ["AWS2AZURE__CREDENTIALS__0__AWSACCESSKEYID"]           = "AKIA-ENV",
            ["AWS2AZURE__CREDENTIALS__0__AZURE__BLOB__ACCOUNTNAME"] = "acc-env",
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
    public void Env_vars_can_introduce_new_credential_entry()
    {
        var env = new Dictionary<string, string?>
        {
            ["AWS2AZURE__CREDENTIALS__0__AWSACCESSKEYID"]                = "AKIA-NEW",
            ["AWS2AZURE__CREDENTIALS__0__AWSSECRETACCESSKEY"]            = "secret-new",
            ["AWS2AZURE__CREDENTIALS__0__AZURE__SERVICEBUS__NAMESPACE"]  = "ns",
            ["AWS2AZURE__CREDENTIALS__0__AZURE__SERVICEBUS__SASKEYNAME"] = "RootManageSharedAccessKey",
            ["AWS2AZURE__CREDENTIALS__0__AZURE__SERVICEBUS__SASKEY"]     = "sb-key",
        };

        var config = ProxyConfigLoader.Load(jsonFilePath: null, env);

        var entry = Assert.Single(config.Credentials);
        Assert.Equal("AKIA-NEW", entry.AwsAccessKeyId);
        Assert.Equal("ns", entry.Azure.ServiceBus!.Namespace);
        Assert.Equal("sb-key", entry.Azure.ServiceBus.SasKey);
    }

    [Fact]
    public void Throws_on_malformed_json()
    {
        File.WriteAllText(_tempFile, "{ this is not valid json");

        Assert.ThrowsAny<System.Text.Json.JsonException>(
            () => ProxyConfigLoader.Load(_tempFile, envVars: new Dictionary<string, string?>()));
    }
}
