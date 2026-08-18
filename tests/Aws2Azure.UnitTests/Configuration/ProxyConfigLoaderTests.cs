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
    public void Resolves_bundled_and_release_archive_config_paths()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"aws2azure-config-path-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var releaseExample = Path.Combine(directory, "config.example.json");
            File.WriteAllText(releaseExample, "{}");
            Assert.Equal(
                releaseExample,
                ProxyConfigLoader.ResolveConfigFilePath(directory, configuredPath: null));

            var bundled = Path.Combine(directory, "config.json");
            File.WriteAllText(bundled, "{}");
            Assert.Equal(
                bundled,
                ProxyConfigLoader.ResolveConfigFilePath(directory, configuredPath: null));

            var explicitPath = Path.Combine(directory, "operator.json");
            Assert.Equal(
                explicitPath,
                ProxyConfigLoader.ResolveConfigFilePath(directory, explicitPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
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
            ["AWS2AZURE__BINDINGS__0__AZURE__SQS__AUTH__MODE"]      = "sas",
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

    public static TheoryData<string, string> UnsupportedAuthModes => new()
    {
        { "S3", "sas" },
        { "S3", "managedIdentity" },
        { "S3", "clientSecret" },
        { "S3", "workloadIdentity" },
        { "S3", "reference" },
        { "SQS", "sharedKey" },
        { "SQS", "managedIdentity" },
        { "SQS", "clientSecret" },
        { "SQS", "workloadIdentity" },
        { "SQS", "reference" },
        { "DYNAMODB", "sas" },
        { "SNS", "sharedKey" },
        { "KINESIS", "sharedKey" },
        { "SECRETSMANAGER", "sharedKey" },
        { "SECRETSMANAGER", "sas" },
    };

    public static TheoryData<string, string> SupportedAuthModes => new()
    {
        { "S3", "sharedKey" },
        { "SQS", "sas" },
        { "DYNAMODB", "sharedKey" },
        { "DYNAMODB", "managedIdentity" },
        { "DYNAMODB", "clientSecret" },
        { "DYNAMODB", "workloadIdentity" },
        { "DYNAMODB", "reference" },
        { "SNS", "sas" },
        { "SNS", "managedIdentity" },
        { "SNS", "clientSecret" },
        { "SNS", "workloadIdentity" },
        { "SNS", "reference" },
        { "KINESIS", "sas" },
        { "KINESIS", "managedIdentity" },
        { "KINESIS", "clientSecret" },
        { "KINESIS", "workloadIdentity" },
        { "KINESIS", "reference" },
        { "SECRETSMANAGER", "managedIdentity" },
        { "SECRETSMANAGER", "clientSecret" },
        { "SECRETSMANAGER", "workloadIdentity" },
        { "SECRETSMANAGER", "reference" },
    };

    [Theory]
    [MemberData(nameof(UnsupportedAuthModes))]
    public void Unsupported_service_auth_mode_override_does_not_mutate_document(
        string service,
        string mode)
    {
        var env = new Dictionary<string, string?>
        {
            [$"AWS2AZURE__BINDINGS__0__AZURE__{service}__AUTH__MODE"] = mode,
        };

        var config = ProxyConfigLoader.Load(jsonFilePath: null, env);

        Assert.Empty(config.Credentials);
    }

    [Theory]
    [MemberData(nameof(SupportedAuthModes))]
    public void Supported_service_auth_mode_override_is_applied(string service, string mode)
    {
        File.WriteAllText(
            _tempFile,
            """{"azureIdentities":{"identity":{"authMode":"managedIdentity"}},"bindings":[]}""");
        var env = ValidBackendEnvironment(service, mode);

        var config = ProxyConfigLoader.Load(_tempFile, env);

        Assert.Single(config.Credentials);
    }

    [Theory]
    [InlineData("AWS2AZURE__BINDINGS__-1__AWS__ACCESSKEYID", "AKIA")]
    [InlineData("AWS2AZURE__BINDINGS__100000000__AWS__ACCESSKEYID", "AKIA")]
    [InlineData("AWS2AZURE__BINDINGS__0__AZURE__S3__UNKNOWN", "value")]
    [InlineData("AWS2AZURE__BINDINGS__0__AZURE__S3__TARGET__NAMESPACE", "not-blob")]
    [InlineData("AWS2AZURE__BINDINGS__0__AZURE__S3__AUTH__CLIENTSECRET", "not-shared-key")]
    [InlineData("AWS2AZURE__BINDINGS__0__AZURE__SNS__TARGET__TOPICNAME", "obsolete-primary-event-grid")]
    [InlineData("AWS2AZURE__BINDINGS__0__AZURE__S3__KIND", " blob ")]
    [InlineData("AWS2AZURE__BINDINGS__0__AZURE__S3__KIND", "1")]
    [InlineData("AWS2AZURE__BINDINGS__0__AZURE__S3__KIND", "unknown")]
    [InlineData("AWS2AZURE__BINDINGS__0__AZURE__UNKNOWN__KIND", "blob")]
    [InlineData("AWS2AZURE__BINDINGS__0__AZURE__DYNAMODB__TARGET__PREFERREDREGIONS__-1", "West US")]
    [InlineData("AWS2AZURE__BINDINGS__0__AZURE__DYNAMODB__TARGET__PREFERREDREGIONS__100000000", "West US")]
    [InlineData("AWS2AZURE__BINDINGS__0__AZURE__SQS__QUEUES__orders__UNKNOWN", "Rest")]
    public void Malformed_or_unknown_binding_override_does_not_mutate_document(
        string key,
        string value)
    {
        var env = new Dictionary<string, string?> { [key] = value };

        var config = ProxyConfigLoader.Load(jsonFilePath: null, env);

        Assert.Empty(config.Credentials);
    }

    [Theory]
    [InlineData("AWS2AZURE__SERVICES__S3__UNKNOWN", "true")]
    [InlineData("AWS2AZURE__SERVICES__S3__ENABLED__EXTRA", "true")]
    [InlineData("AWS2AZURE__SERVICES__S3__ENABLED", "not-a-boolean")]
    [InlineData("AWS2AZURE__SERVICES__DYNAMODB__USESTOREDPROCEDURES", "1")]
    [InlineData("AWS2AZURE__SERVICES__DYNAMODB__USESTOREDPROCEDURES", " Preferred ")]
    public void Malformed_or_unknown_service_override_does_not_mutate_document(
        string key,
        string value)
    {
        var env = new Dictionary<string, string?> { [key] = value };

        var config = ProxyConfigLoader.Load(jsonFilePath: null, env);

        Assert.Empty(config.Services);
    }

    [Fact]
    public void Override_targeting_null_binding_reports_configuration_error()
    {
        File.WriteAllText(_tempFile, """{ "bindings": [ null ] }""");
        var env = new Dictionary<string, string?>
        {
            ["AWS2AZURE__BINDINGS__0__AWS__ACCESSKEYID"] = "AKIA",
        };

        var exception = Assert.Throws<ProxyConfigException>(
            () => ProxyConfigLoader.Load(_tempFile, env));

        Assert.Contains("bindings[0]", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Reports_malformed_json_as_controlled_configuration_error()
    {
        File.WriteAllText(_tempFile, "{ this is not valid json");

        var exception = Assert.Throws<ProxyConfigException>(
            () => ProxyConfigLoader.Load(_tempFile, envVars: new Dictionary<string, string?>()));

        Assert.Contains(_tempFile, exception.Message, StringComparison.Ordinal);
        Assert.Contains("contains invalid JSON", exception.Message, StringComparison.Ordinal);
        Assert.IsType<System.Text.Json.JsonException>(exception.InnerException);
    }

    private static Dictionary<string, string?> ValidBackendEnvironment(string service, string mode)
    {
        var prefix = $"AWS2AZURE__BINDINGS__0__AZURE__{service}";
        var env = new Dictionary<string, string?>
        {
            ["AWS2AZURE__BINDINGS__0__AWS__ACCESSKEYID"] = "AKIA",
            ["AWS2AZURE__BINDINGS__0__AWS__SECRETACCESSKEY"] = "secret",
            [$"{prefix}__KIND"] = service switch
            {
                "S3" => "blob",
                "SQS" => "serviceBus",
                "DYNAMODB" => "cosmos",
                "SNS" => "serviceBusTopics",
                "KINESIS" => "eventHubs",
                "SECRETSMANAGER" => "keyVault",
                _ => throw new ArgumentOutOfRangeException(nameof(service)),
            },
            [$"{prefix}__AUTH__MODE"] = mode,
        };

        if (service == "S3")
        {
            env[$"{prefix}__TARGET__ACCOUNTNAME"] = "account";
        }
        else if (service == "SECRETSMANAGER")
        {
            env[$"{prefix}__TARGET__VAULTURL"] = "https://vault.vault.azure.net/";
        }
        else if (service == "DYNAMODB")
        {
            env[$"{prefix}__TARGET__ENDPOINT"] = "https://account.documents.azure.com:443/";
            env[$"{prefix}__TARGET__DATABASENAME"] = "database";
        }
        else
        {
            env[$"{prefix}__TARGET__NAMESPACE"] = "namespace";
        }

        switch (mode)
        {
            case "sharedKey":
                env[$"{prefix}__AUTH__KEY"] = "key";
                break;
            case "sas":
                env[$"{prefix}__AUTH__KEYNAME"] = "RootManageSharedAccessKey";
                env[$"{prefix}__AUTH__KEY"] = "key";
                break;
            case "clientSecret":
                env[$"{prefix}__AUTH__TENANTID"] = "tenant";
                env[$"{prefix}__AUTH__CLIENTID"] = "client";
                env[$"{prefix}__AUTH__CLIENTSECRET"] = "secret";
                break;
            case "reference":
                env[$"{prefix}__AUTH__IDENTITY"] = "identity";
                break;
        }

        return env;
    }
}
