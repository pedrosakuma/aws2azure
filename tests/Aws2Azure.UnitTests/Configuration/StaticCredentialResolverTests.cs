using Aws2Azure.Core.Configuration;

namespace Aws2Azure.UnitTests.Configuration;

public class StaticCredentialResolverTests
{
    private static ProxyConfig SampleConfig() => new()
    {
        Credentials =
        {
            new CredentialEntry
            {
                AwsAccessKeyId = "AKIA1",
                AwsSecretAccessKey = "secret1",
                Azure = new AzureCredentials
                {
                    Blob       = new BlobCredentials { AccountName = "acc1", AccountKey = "key1" },
                    ServiceBus = new ServiceBusCredentials { Namespace = "ns1", SasKeyName = "n", SasKey = "k" },
                },
            },
            new CredentialEntry
            {
                AwsAccessKeyId = "AKIA2",
                AwsSecretAccessKey = "secret2",
                Azure = new AzureCredentials
                {
                    Cosmos = new CosmosCredentials { Endpoint = "https://x", PrimaryKey = "pk", DatabaseName = "main" },
                },
            },
        },
    };

    [Fact]
    public void TryGetAwsSecret_returns_matching_secret()
    {
        var resolver = new StaticCredentialResolver(SampleConfig());

        Assert.True(resolver.TryGetAwsSecret("AKIA1", out var s1));
        Assert.Equal("secret1", s1);

        Assert.True(resolver.TryGetAwsSecret("AKIA2", out var s2));
        Assert.Equal("secret2", s2);
    }

    [Fact]
    public void TryGetAwsSecret_returns_false_for_unknown_key()
    {
        var resolver = new StaticCredentialResolver(SampleConfig());

        Assert.False(resolver.TryGetAwsSecret("AKIAUNKNOWN", out var secret));
        Assert.Equal(string.Empty, secret);
    }

    [Fact]
    public void Aws_access_key_lookup_is_case_sensitive()
    {
        var resolver = new StaticCredentialResolver(SampleConfig());
        Assert.False(resolver.TryGetAwsSecret("akia1", out _));
    }

    [Fact]
    public void GetAzureCredentialsFor_returns_typed_credentials()
    {
        var resolver = new StaticCredentialResolver(SampleConfig());

        var blob = Assert.IsType<BlobCredentials>(resolver.GetAzureCredentialsFor("AKIA1", AzureService.Blob));
        Assert.Equal("acc1", blob.AccountName);

        var sb = Assert.IsType<ServiceBusCredentials>(resolver.GetAzureCredentialsFor("AKIA1", AzureService.ServiceBus));
        Assert.Equal("ns1", sb.Namespace);

        var cosmos = Assert.IsType<CosmosCredentials>(resolver.GetAzureCredentialsFor("AKIA2", AzureService.Cosmos));
        Assert.Equal("https://x", cosmos.Endpoint);
    }

    [Fact]
    public void GetAzureCredentialsFor_returns_null_when_service_not_configured()
    {
        var resolver = new StaticCredentialResolver(SampleConfig());
        Assert.Null(resolver.GetAzureCredentialsFor("AKIA1", AzureService.Cosmos));
        Assert.Null(resolver.GetAzureCredentialsFor("AKIA2", AzureService.Blob));
    }

    [Fact]
    public void GetAzureCredentialsFor_returns_null_for_unknown_access_key()
    {
        var resolver = new StaticCredentialResolver(SampleConfig());
        Assert.Null(resolver.GetAzureCredentialsFor("AKIAUNKNOWN", AzureService.Blob));
    }
}
