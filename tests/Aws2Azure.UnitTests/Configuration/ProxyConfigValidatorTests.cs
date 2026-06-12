using Aws2Azure.Core.Configuration;

namespace Aws2Azure.UnitTests.Configuration;

[Collection("EnvironmentVariables")]
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
                        Blob = new BlobCredentials(),
                        ServiceBus = new ServiceBusCredentials(),
                        ServiceBusTopics = new ServiceBusTopicsCredentials(),
                        Cosmos = new CosmosCredentials(),
                        EventHubs = new EventHubsCredentials(),
                        EventGrid = new EventGridCredentials(),
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
        Assert.Contains("serviceBusTopics.namespace: required", ex.Message);
        Assert.Contains("serviceBusTopics: either (sasKeyName+sasKey) OR (tenantId+clientId+clientSecret)", ex.Message);
        Assert.Contains("cosmos.endpoint: required", ex.Message);
        Assert.Contains("cosmos.databaseName: required", ex.Message);
        Assert.Contains("either primaryKey OR (tenantId+clientId+clientSecret)", ex.Message);
        Assert.Contains("eventHubs.namespace: required", ex.Message);
        Assert.Contains("eventHubs: either (sasKeyName+sasKey) OR (tenantId+clientId+clientSecret)", ex.Message);
        Assert.Contains("eventGrid: either endpoint OR (namespace+topicName) is required", ex.Message);
        Assert.Contains("eventGrid: either accessKey OR (tenantId+clientId+clientSecret)", ex.Message);
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

    [Fact]
    public void Throws_when_event_grid_mixes_access_key_and_aad()
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
                        EventGrid = new EventGridCredentials
                        {
                            Endpoint = "https://example.westus2-1.eventgrid.azure.net/api/events",
                            AccessKey = "key",
                            TenantId = "tenant",
                        },
                    },
                },
            },
        };

        var ex = Assert.Throws<ProxyConfigException>(() => ProxyConfigValidator.Validate(config));
        Assert.Contains("eventGrid: AccessKey and AAD fields are mutually exclusive", ex.Message);
    }

    [Fact]
    public void Throws_when_event_grid_endpoint_is_not_https()
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
                        EventGrid = new EventGridCredentials
                        {
                            Endpoint = "http://example.westus2-1.eventgrid.azure.net/api/events",
                            AccessKey = "key",
                        },
                    },
                },
            },
        };

        var ex = Assert.Throws<ProxyConfigException>(() => ProxyConfigValidator.Validate(config));
        Assert.Contains("eventGrid.endpoint: must use https scheme.", ex.Message);
    }

    [Fact]
    public void Accepts_event_grid_with_access_key()
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
                        EventGrid = new EventGridCredentials
                        {
                            Endpoint = "https://example.westus2-1.eventgrid.azure.net/api/events",
                            AccessKey = "key",
                        },
                    },
                },
            },
        };

        var ex = Record.Exception(() => ProxyConfigValidator.Validate(config));
        Assert.Null(ex);
    }

    [Fact]
    public void Accepts_event_grid_with_namespace_and_topic_name()
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
                        EventGrid = new EventGridCredentials
                        {
                            Namespace = "eastus-1.eventgrid.azure.net",
                            TopicName = "orders",
                            AccessKey = "key",
                        },
                    },
                },
            },
        };

        var ex = Record.Exception(() => ProxyConfigValidator.Validate(config));
        Assert.Null(ex);
    }

    [Fact]
    public void Accepts_key_vault_with_aad_credentials()
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
                        KeyVault = new KeyVaultCredentials
                        {
                            VaultUrl = "https://example.vault.azure.net/",
                            TenantId = "tenant",
                            ClientId = "client",
                            ClientSecret = "secret",
                        },
                    },
                },
            },
        };

        var ex = Record.Exception(() => ProxyConfigValidator.Validate(config));
        Assert.Null(ex);
    }

    [Fact]
    public void Rejects_key_vault_when_aad_fields_are_incomplete()
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
                        KeyVault = new KeyVaultCredentials
                        {
                            VaultUrl = "https://example.vault.azure.net/",
                            TenantId = "tenant",
                            ClientId = "client",
                        },
                    },
                },
            },
        };

        var ex = Assert.Throws<ProxyConfigException>(() => ProxyConfigValidator.Validate(config));
        Assert.Contains("keyVault: tenantId, clientId, and clientSecret are required together", ex.Message);
    }

    [Fact]
    public void Throws_when_event_grid_topic_backend_has_no_destination_or_auth()
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
                        ServiceBusTopics = new ServiceBusTopicsCredentials
                        {
                            Namespace = "myns",
                            SasKeyName = "Root",
                            SasKey = "key",
                            Topics = new Dictionary<string, SnsTopicSettings>
                            {
                                ["orders"] = new()
                                {
                                    Backend = SnsTopicBackend.EventGrid,
                                },
                            },
                        },
                    },
                },
            },
        };

        var ex = Assert.Throws<ProxyConfigException>(() => ProxyConfigValidator.Validate(config));
        Assert.Contains("topics.orders: EventGrid backend requires either eventGridTopicEndpoint or azure.eventGrid endpoint/(namespace+topicName)", ex.Message);
        Assert.Contains("topics.orders: EventGrid backend requires either eventGridAccessKey or azure.eventGrid accessKey/(tenantId+clientId+clientSecret)", ex.Message);
    }

    [Fact]
    public void Throws_when_default_backend_event_grid_has_no_global_destination()
    {
        var config = new ProxyConfig
        {
            Sns = new SnsSettings { DefaultBackend = SnsTopicBackend.EventGrid },
            Credentials =
            {
                new CredentialEntry
                {
                    AwsAccessKeyId = "AKIA1",
                    AwsSecretAccessKey = "secret",
                    Azure = new AzureCredentials
                    {
                        ServiceBusTopics = new ServiceBusTopicsCredentials
                        {
                            Namespace = "myns",
                            SasKeyName = "Root",
                            SasKey = "key",
                        },
                    },
                },
            },
        };

        var ex = Assert.Throws<ProxyConfigException>(() => ProxyConfigValidator.Validate(config));
        Assert.Contains("serviceBusTopics: sns.defaultBackend=EventGrid: EventGrid backend requires either eventGridTopicEndpoint or azure.eventGrid endpoint/(namespace+topicName)", ex.Message);
    }

    [Fact]
    public void Throws_when_service_bus_topics_mixes_sas_and_aad()
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
                        ServiceBusTopics = new ServiceBusTopicsCredentials
                        {
                            Namespace = "myns",
                            SasKeyName = "Root",
                            SasKey = "key",
                            TenantId = "tenant",
                        },
                    },
                },
            },
        };

        var ex = Assert.Throws<ProxyConfigException>(() => ProxyConfigValidator.Validate(config));
        Assert.Contains("serviceBusTopics: SAS and AAD fields are mutually exclusive", ex.Message);
    }

    [Fact]
    public void Throws_when_service_bus_topics_is_missing_required_fields()
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
                        ServiceBusTopics = new ServiceBusTopicsCredentials
                        {
                            Namespace = "",
                            SasKeyName = "Root",
                        },
                    },
                },
            },
        };

        var ex = Assert.Throws<ProxyConfigException>(() => ProxyConfigValidator.Validate(config));
        Assert.Contains("serviceBusTopics.namespace: required.", ex.Message);
        Assert.Contains("serviceBusTopics: SAS auth requires both sasKeyName and sasKey.", ex.Message);
    }

    [Fact]
    public void Accepts_service_bus_topics_with_complete_sas()
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
                        ServiceBusTopics = new ServiceBusTopicsCredentials
                        {
                            Namespace = "myns",
                            SasKeyName = "Root",
                            SasKey = "key",
                            Topics = new Dictionary<string, SnsTopicSettings>
                            {
                                ["orders"] = new()
                                {
                                    Backend = SnsTopicBackend.EventGrid,
                                    EventGridTopicEndpoint = "https://override.westus2-1.eventgrid.azure.net/api/events",
                                    EventGridAccessKey = "per-topic-key",
                                },
                            },
                        },
                    },
                },
            },
        };

        var ex = Record.Exception(() => ProxyConfigValidator.Validate(config));
        Assert.Null(ex);
    }

    [Fact]
    public void Rejects_managed_identity_with_client_secret()
    {
        var config = ValidBase();
        config.Credentials[0].Azure.EventHubs = new EventHubsCredentials
        {
            Namespace = "myns",
            AuthMode = AzureAuthMode.ManagedIdentity,
            ClientSecret = "secret",
        };

        var ex = Assert.Throws<ProxyConfigException>(() => ProxyConfigValidator.Validate(config));
        Assert.Contains("eventHubs: authMode 'ManagedIdentity' must not specify clientSecret.", ex.Message);
    }

    [Fact]
    public void Accepts_per_topic_event_grid_access_key_override_when_global_event_grid_uses_managed_identity()
    {
        var config = ValidBase();
        config.Credentials[0].Azure.EventGrid = new EventGridCredentials
        {
            Endpoint = "https://global.westus2-1.eventgrid.azure.net/api/events",
            AuthMode = AzureAuthMode.ManagedIdentity,
        };
        config.Credentials[0].Azure.ServiceBusTopics = new ServiceBusTopicsCredentials
        {
            Namespace = "myns",
            SasKeyName = "Root",
            SasKey = "key",
            Topics = new Dictionary<string, SnsTopicSettings>
            {
                ["orders"] = new()
                {
                    Backend = SnsTopicBackend.EventGrid,
                    EventGridTopicEndpoint = "https://override.westus2-1.eventgrid.azure.net/api/events",
                    EventGridAccessKey = "per-topic-key",
                },
            },
        };

        var ex = Record.Exception(() => ProxyConfigValidator.Validate(config));
        Assert.Null(ex);
    }

    [Fact]
    public void Accepts_managed_identity_system_assigned_without_aad_fields()
    {
        var config = ValidBase();
        config.Credentials[0].Azure.EventHubs = new EventHubsCredentials
        {
            Namespace = "myns",
            AuthMode = AzureAuthMode.ManagedIdentity,
        };

        var ex = Record.Exception(() => ProxyConfigValidator.Validate(config));
        Assert.Null(ex);
    }

    [Fact]
    public void Accepts_workload_identity_when_environment_is_configured()
    {
        var old = ClearAndSetWorkloadIdentityEnvironment("tenant", "client", "token-file");
        try
        {
            var config = ValidBase();
            config.Credentials[0].Azure.KeyVault = new KeyVaultCredentials
            {
                VaultUrl = "https://example.vault.azure.net/",
                AuthMode = AzureAuthMode.WorkloadIdentity,
            };

            var ex = Record.Exception(() => ProxyConfigValidator.Validate(config));
            Assert.Null(ex);
        }
        finally
        {
            RestoreWorkloadIdentityEnvironment(old);
        }
    }

    [Fact]
    public void Rejects_workload_identity_with_configured_client_secret()
    {
        var old = ClearAndSetWorkloadIdentityEnvironment("tenant", "client", "token-file");
        try
        {
            var config = ValidBase();
            config.Credentials[0].Azure.KeyVault = new KeyVaultCredentials
            {
                VaultUrl = "https://example.vault.azure.net/",
                AuthMode = AzureAuthMode.WorkloadIdentity,
                ClientSecret = "secret",
            };

            var ex = Assert.Throws<ProxyConfigException>(() => ProxyConfigValidator.Validate(config));
            Assert.Contains("keyVault: authMode 'WorkloadIdentity' takes tenant/client/token from AZURE_* environment variables", ex.Message);
        }
        finally
        {
            RestoreWorkloadIdentityEnvironment(old);
        }
    }

    [Fact]
    public void Rejects_workload_identity_when_environment_is_missing()
    {
        var old = ClearAndSetWorkloadIdentityEnvironment(null, null, null);
        try
        {
            var config = ValidBase();
            config.Credentials[0].Azure.KeyVault = new KeyVaultCredentials
            {
                VaultUrl = "https://example.vault.azure.net/",
                AuthMode = AzureAuthMode.WorkloadIdentity,
            };

            var ex = Assert.Throws<ProxyConfigException>(() => ProxyConfigValidator.Validate(config));
            Assert.Contains("keyVault: authMode 'WorkloadIdentity' requires the AZURE_TENANT_ID, AZURE_CLIENT_ID and AZURE_FEDERATED_TOKEN_FILE environment variables.", ex.Message);
            Assert.Contains("AZURE_TENANT_ID", ex.Message);
            Assert.Contains("AZURE_CLIENT_ID", ex.Message);
            Assert.Contains("AZURE_FEDERATED_TOKEN_FILE", ex.Message);
        }
        finally
        {
            RestoreWorkloadIdentityEnvironment(old);
        }
    }

    [Fact]
    public void Rejects_key_shape_with_non_default_auth_mode()
    {
        var config = ValidBase();
        config.Credentials[0].Azure.EventHubs = new EventHubsCredentials
        {
            Namespace = "myns",
            SasKeyName = "Root",
            SasKey = "key",
            AuthMode = AzureAuthMode.ManagedIdentity,
        };

        var ex = Assert.Throws<ProxyConfigException>(() => ProxyConfigValidator.Validate(config));
        Assert.Contains("eventHubs: authMode 'ManagedIdentity' cannot be combined with SAS auth", ex.Message);
    }

    private static (string? Tenant, string? Client, string? TokenFile) ClearAndSetWorkloadIdentityEnvironment(
        string? tenant,
        string? client,
        string? tokenFile)
    {
        var old = (
            Environment.GetEnvironmentVariable("AZURE_TENANT_ID"),
            Environment.GetEnvironmentVariable("AZURE_CLIENT_ID"),
            Environment.GetEnvironmentVariable("AZURE_FEDERATED_TOKEN_FILE"));
        Environment.SetEnvironmentVariable("AZURE_TENANT_ID", tenant);
        Environment.SetEnvironmentVariable("AZURE_CLIENT_ID", client);
        Environment.SetEnvironmentVariable("AZURE_FEDERATED_TOKEN_FILE", tokenFile);
        return old;
    }

    private static void RestoreWorkloadIdentityEnvironment((string? Tenant, string? Client, string? TokenFile) old)
    {
        Environment.SetEnvironmentVariable("AZURE_TENANT_ID", old.Tenant);
        Environment.SetEnvironmentVariable("AZURE_CLIENT_ID", old.Client);
        Environment.SetEnvironmentVariable("AZURE_FEDERATED_TOKEN_FILE", old.TokenFile);
    }
}
