using Aws2Azure.Core.Configuration;

namespace Aws2Azure.UnitTests.Configuration;

public class ProxyConfigValidatorTests
{
    private static ProxyConfig ValidBase() => new()
    {
        Credentials =
        {
            new CredentialEntry
            {
                AwsAccessKeyId = "AKIA1",
                AwsSecretAccessKey = "secret1",
                Azure = new AzureCredentials
                {
                    Blob = new BlobCredentials { AccountName = "acc", AccountKey = "key" },
                },
            },
        },
    };

    [Fact]
    public void Accepts_minimal_valid_config()
    {
        var ex = Record.Exception(() => ProxyConfigValidator.Validate(ValidBase()));
        Assert.Null(ex);
    }

    [Fact]
    public void Throws_when_no_credentials_configured()
    {
        var ex = Assert.Throws<ProxyConfigException>(() => ProxyConfigValidator.Validate(new ProxyConfig()));
        Assert.Contains("credentials: at least one entry", ex.Message);
    }

    [Fact]
    public void Throws_on_duplicate_aws_access_key_id()
    {
        var config = ValidBase();
        config.Credentials.Add(new CredentialEntry
        {
            AwsAccessKeyId = "AKIA1",
            AwsSecretAccessKey = "secret2",
            Azure = new AzureCredentials
            {
                Blob = new BlobCredentials { AccountName = "acc2", AccountKey = "key2" },
            },
        });

        var ex = Assert.Throws<ProxyConfigException>(() => ProxyConfigValidator.Validate(config));
        Assert.Contains("duplicate value 'AKIA1'", ex.Message);
    }

    [Fact]
    public void Throws_on_missing_required_fields()
    {
        var config = new ProxyConfig
        {
            Credentials =
            {
                new CredentialEntry
                {
                    AwsAccessKeyId = "",
                    AwsSecretAccessKey = "",
                    Azure = new AzureCredentials
                    {
                        Blob       = new BlobCredentials(),
                        ServiceBus = new ServiceBusCredentials(),
                        Cosmos     = new CosmosCredentials(),
                    },
                },
            },
        };

        var ex = Assert.Throws<ProxyConfigException>(() => ProxyConfigValidator.Validate(config));
        Assert.Contains("awsAccessKeyId: required", ex.Message);
        Assert.Contains("awsSecretAccessKey: required", ex.Message);
        Assert.Contains("blob.accountName: required", ex.Message);
        Assert.Contains("blob.accountKey: required", ex.Message);
        Assert.Contains("serviceBus.namespace: required", ex.Message);
        Assert.Contains("serviceBus.sasKeyName: required", ex.Message);
        Assert.Contains("serviceBus.sasKey: required", ex.Message);
        Assert.Contains("cosmos.endpoint: required", ex.Message);
        Assert.Contains("cosmos.databaseName: required", ex.Message);
        Assert.Contains("either primaryKey OR (tenantId+clientId+clientSecret)", ex.Message);
    }

    [Fact]
    public void Aggregates_all_errors_into_single_exception()
    {
        var config = new ProxyConfig
        {
            Credentials =
            {
                new CredentialEntry { AwsAccessKeyId = "", AwsSecretAccessKey = "" },
            },
        };

        var ex = Assert.Throws<ProxyConfigException>(() => ProxyConfigValidator.Validate(config));
        Assert.True(ex.Message.Split("\n  - ").Length >= 3);
    }
}
