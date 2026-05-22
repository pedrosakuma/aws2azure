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
                        EventHubs  = new EventHubsCredentials(),
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
        Assert.Contains("eventHubs.namespace: required", ex.Message);
        Assert.Contains("eventHubs: either (sasKeyName+sasKey) OR (tenantId+clientId+clientSecret)", ex.Message);
    }

    [Fact]
    public void Throws_when_event_hubs_mixes_sas_and_aad()
    {
        var config = new ProxyConfig
        {
            Credentials =
            {
                new CredentialEntry
                {
                    AwsAccessKeyId = "AKIA1",
                    AwsSecretAccessKey = "secret",
                    Azure = new AzureCredentials
                    {
                        EventHubs = new EventHubsCredentials
                        {
                            Namespace = "myns",
                            SasKeyName = "Root",
                            SasKey = "ZGVhZGJlZWY=",
                            TenantId = "tenant",
                        },
                    },
                },
            },
        };

        var ex = Assert.Throws<ProxyConfigException>(() => ProxyConfigValidator.Validate(config));
        Assert.Contains("eventHubs: SAS and AAD fields are mutually exclusive", ex.Message);
    }

    [Fact]
    public void Accepts_event_hubs_with_complete_sas()
    {
        var config = new ProxyConfig
        {
            Credentials =
            {
                new CredentialEntry
                {
                    AwsAccessKeyId = "AKIA1",
                    AwsSecretAccessKey = "secret",
                    Azure = new AzureCredentials
                    {
                        EventHubs = new EventHubsCredentials
                        {
                            Namespace = "myns",
                            SasKeyName = "Root",
                            SasKey = "ZGVhZGJlZWY=",
                        },
                    },
                },
            },
        };

        var ex = Record.Exception(() => ProxyConfigValidator.Validate(config));
        Assert.Null(ex);
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

    [Fact]
    public void Throws_on_partial_aad_fields()
    {
        var config = new ProxyConfig
        {
            Credentials =
            {
                new CredentialEntry
                {
                    AwsAccessKeyId = "AKIA1",
                    AwsSecretAccessKey = "secret",
                    Azure = new AzureCredentials
                    {
                        Cosmos = new CosmosCredentials
                        {
                            Endpoint = "https://x.documents.azure.com/",
                            DatabaseName = "main",
                            TenantId = "tenant",
                            ClientId = "client",
                            // ClientSecret missing → partial AAD shape.
                        },
                    },
                },
            },
        };

        var ex = Assert.Throws<ProxyConfigException>(() => ProxyConfigValidator.Validate(config));
        Assert.Contains("AAD requires tenantId, clientId, and clientSecret together", ex.Message);
    }

    [Fact]
    public void Throws_when_primary_key_mixed_with_any_aad_field()
    {
        var config = new ProxyConfig
        {
            Credentials =
            {
                new CredentialEntry
                {
                    AwsAccessKeyId = "AKIA1",
                    AwsSecretAccessKey = "secret",
                    Azure = new AzureCredentials
                    {
                        Cosmos = new CosmosCredentials
                        {
                            Endpoint = "https://x.documents.azure.com/",
                            DatabaseName = "main",
                            PrimaryKey = "MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=",
                            TenantId = "tenant-only",
                        },
                    },
                },
            },
        };

        var ex = Assert.Throws<ProxyConfigException>(() => ProxyConfigValidator.Validate(config));
        Assert.Contains("mutually exclusive", ex.Message);
    }
}
