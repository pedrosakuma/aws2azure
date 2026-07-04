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
        Assert.Contains("bindings: at least one entry", ex.Message);
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
        Assert.Contains("bindings[0].aws.accessKeyId: required", ex.Message);
        Assert.Contains("bindings[0].aws.secretAccessKey: required", ex.Message);
        Assert.Contains("bindings[0].azure.s3.target.accountName: required", ex.Message);
        Assert.Contains("bindings[0].azure.s3.auth.key: required", ex.Message);
        Assert.Contains("bindings[0].azure.sqs.target.namespace: required", ex.Message);
        Assert.Contains("bindings[0].azure.sqs.auth.keyName: required", ex.Message);
        Assert.Contains("bindings[0].azure.sqs.auth.key: required", ex.Message);
        Assert.Contains("bindings[0].azure.sns.target.namespace: required", ex.Message);
        Assert.Contains("bindings[0].azure.sns: either (auth.keyName+auth.key) OR (auth.tenantId+auth.clientId+auth.clientSecret)", ex.Message);
        Assert.Contains("bindings[0].azure.dynamodb.target.endpoint: required", ex.Message);
        Assert.Contains("bindings[0].azure.dynamodb.target.databaseName: required", ex.Message);
        Assert.Contains("either auth.key OR (auth.tenantId+auth.clientId+auth.clientSecret)", ex.Message);
        Assert.Contains("bindings[0].azure.kinesis.target.namespace: required", ex.Message);
        Assert.Contains("bindings[0].azure.kinesis: either (auth.keyName+auth.key) OR (auth.tenantId+auth.clientId+auth.clientSecret)", ex.Message);
        Assert.Contains("bindings[0].azure.sns: either target.endpoint OR (target.namespace+target.topicName) is required", ex.Message);
        Assert.Contains("bindings[0].azure.sns: either auth.key OR (auth.tenantId+auth.clientId+auth.clientSecret)", ex.Message);
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
        Assert.Contains("azure.kinesis: auth.keyName/auth.key and AAD fields are mutually exclusive", ex.Message);
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
        Assert.Contains("AAD requires auth.tenantId, auth.clientId, and auth.clientSecret together", ex.Message);
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
        Assert.Contains("azure.sns: auth.key and AAD fields are mutually exclusive", ex.Message);
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
        Assert.Contains("azure.sns.target.endpoint: must use https scheme.", ex.Message);
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
        Assert.Contains("azure.secretsmanager: auth.tenantId, auth.clientId, and auth.clientSecret are required together for Key Vault AAD auth.", ex.Message);
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
        Assert.Contains("azure.sns.topics.orders: EventGrid backend requires either eventGridTopicEndpoint or this binding's azure.sns to use kind=eventGrid with target.endpoint/(target.namespace+target.topicName)", ex.Message);
        Assert.Contains("azure.sns.topics.orders: EventGrid backend requires either eventGridAccessKey or this binding's azure.sns to use kind=eventGrid with auth.key/(auth.tenantId+auth.clientId+auth.clientSecret)", ex.Message);
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
        Assert.Contains("azure.sns: services.sns.defaultBackend=EventGrid: EventGrid backend requires either eventGridTopicEndpoint or this binding's azure.sns to use kind=eventGrid with target.endpoint/(target.namespace+target.topicName)", ex.Message);
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
        Assert.Contains("azure.sns: auth.keyName/auth.key and AAD fields are mutually exclusive", ex.Message);
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
        Assert.Contains("azure.sns.target.namespace: required.", ex.Message);
        Assert.Contains("azure.sns: SAS auth requires both auth.keyName and auth.key.", ex.Message);
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
        Assert.Contains("azure.kinesis: auth.mode 'ManagedIdentity' must not specify auth.clientSecret.", ex.Message);
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
    public void Resolves_named_managed_identity_reference_into_block()
    {
        var config = ValidBase();
        config.AzureIdentities = new Dictionary<string, AzureIdentity>
        {
            ["prod-mi"] = new() { AuthMode = AzureAuthMode.ManagedIdentity, ClientId = "user-mi-client" },
        };
        config.Credentials[0].Azure.Cosmos = new CosmosCredentials
        {
            Endpoint = "https://acct.documents.azure.com",
            DatabaseName = "orders",
            Identity = "prod-mi",
        };

        var ex = Record.Exception(() => ProxyConfigValidator.Validate(config));

        Assert.Null(ex);
        var cosmos = config.Credentials[0].Azure.Cosmos!;
        Assert.Equal(AzureAuthMode.ManagedIdentity, cosmos.AuthMode);
        Assert.Equal("user-mi-client", cosmos.ClientId);
    }

    [Fact]
    public void Resolves_one_named_identity_across_multiple_backends_and_access_keys()
    {
        var config = ValidBase();
        config.AzureIdentities = new Dictionary<string, AzureIdentity>
        {
            ["prod-mi"] = new() { AuthMode = AzureAuthMode.ManagedIdentity, ClientId = "shared-mi" },
        };
        config.Credentials[0].Azure.Cosmos = new CosmosCredentials
        {
            Endpoint = "https://acct.documents.azure.com",
            DatabaseName = "orders",
            Identity = "prod-mi",
        };
        config.Credentials[0].Azure.KeyVault = new KeyVaultCredentials
        {
            VaultUrl = "https://example.vault.azure.net/",
            Identity = "prod-mi",
        };
        config.Credentials.Add(new CredentialEntry
        {
            AwsAccessKeyId = "AKIA2",
            AwsSecretAccessKey = "secret2",
            Azure = new AzureCredentials
            {
                EventHubs = new EventHubsCredentials { Namespace = "myns", Identity = "prod-mi" },
            },
        });

        var ex = Record.Exception(() => ProxyConfigValidator.Validate(config));

        Assert.Null(ex);
        Assert.Equal(AzureAuthMode.ManagedIdentity, config.Credentials[0].Azure.Cosmos!.AuthMode);
        Assert.Equal(AzureAuthMode.ManagedIdentity, config.Credentials[0].Azure.KeyVault!.AuthMode);
        Assert.Equal("shared-mi", config.Credentials[1].Azure.EventHubs!.ClientId);
    }

    [Fact]
    public void Validate_is_idempotent_for_resolved_identity_reference()
    {
        var config = ValidBase();
        config.AzureIdentities = new Dictionary<string, AzureIdentity>
        {
            ["prod-mi"] = new() { AuthMode = AzureAuthMode.ManagedIdentity, ClientId = "user-mi-client" },
        };
        config.Credentials[0].Azure.Cosmos = new CosmosCredentials
        {
            Endpoint = "https://acct.documents.azure.com",
            DatabaseName = "orders",
            Identity = "prod-mi",
        };

        ProxyConfigValidator.Validate(config);
        var ex = Record.Exception(() => ProxyConfigValidator.Validate(config));

        Assert.Null(ex);
        Assert.Null(config.Credentials[0].Azure.Cosmos!.Identity);
        Assert.Equal(AzureAuthMode.ManagedIdentity, config.Credentials[0].Azure.Cosmos!.AuthMode);
    }

    [Fact]
    public void Rejects_dangling_identity_reference()
    {
        var config = ValidBase();
        config.Credentials[0].Azure.KeyVault = new KeyVaultCredentials
        {
            VaultUrl = "https://example.vault.azure.net/",
            Identity = "missing",
        };

        var ex = Assert.Throws<ProxyConfigException>(() => ProxyConfigValidator.Validate(config));
        Assert.Contains("azure.secretsmanager.auth: identity reference 'missing' was not found in azureIdentities.", ex.Message);
    }

    [Fact]
    public void Rejects_identity_reference_combined_with_inline_aad_fields()
    {
        var config = ValidBase();
        config.AzureIdentities = new Dictionary<string, AzureIdentity>
        {
            ["prod-mi"] = new() { AuthMode = AzureAuthMode.ManagedIdentity },
        };
        config.Credentials[0].Azure.KeyVault = new KeyVaultCredentials
        {
            VaultUrl = "https://example.vault.azure.net/",
            Identity = "prod-mi",
            ClientSecret = "inline-secret",
        };

        var ex = Assert.Throws<ProxyConfigException>(() => ProxyConfigValidator.Validate(config));
        Assert.Contains("azure.secretsmanager.auth: identity reference 'prod-mi' cannot be combined with inline auth.mode/auth.tenantId/auth.clientId/auth.clientSecret fields.", ex.Message);
    }

    [Fact]
    public void Rejects_malformed_named_identity()
    {
        var config = ValidBase();
        config.AzureIdentities = new Dictionary<string, AzureIdentity>
        {
            ["bad-mi"] = new() { AuthMode = AzureAuthMode.ManagedIdentity, ClientSecret = "secret" },
        };
        config.Credentials[0].Azure.KeyVault = new KeyVaultCredentials
        {
            VaultUrl = "https://example.vault.azure.net/",
            Identity = "bad-mi",
        };

        var ex = Assert.Throws<ProxyConfigException>(() => ProxyConfigValidator.Validate(config));
        Assert.Contains("azureIdentities.bad-mi: authMode 'ManagedIdentity' must not specify clientSecret.", ex.Message);
    }

    [Fact]
    public void Accepts_unreferenced_workload_identity_named_entry_without_environment()
    {
        var old = ClearAndSetWorkloadIdentityEnvironment(null, null, null);
        try
        {
            var config = ValidBase();
            config.AzureIdentities = new Dictionary<string, AzureIdentity>
            {
                ["wi"] = new() { AuthMode = AzureAuthMode.WorkloadIdentity },
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
            Assert.Contains("azure.secretsmanager: auth.mode 'WorkloadIdentity' takes tenant/client/token from AZURE_* environment variables", ex.Message);
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
            Assert.Contains("azure.secretsmanager: auth.mode 'WorkloadIdentity' requires the AZURE_TENANT_ID, AZURE_CLIENT_ID and AZURE_FEDERATED_TOKEN_FILE environment variables.", ex.Message);
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
        Assert.Contains("azure.kinesis: auth.mode 'ManagedIdentity' cannot be combined with auth.keyName/auth.key auth", ex.Message);
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

    [Fact]
    public void Accepts_valid_presigned_trusted_signing_hosts()
    {
        var config = ValidBase();
        config.S3.PresignedTrustedSigningHosts.Add("s3.amazonaws.com");
        config.S3.PresignedTrustedSigningHosts.Add("s3.us-east-1.amazonaws.com");

        var ex = Record.Exception(() => ProxyConfigValidator.Validate(config));
        Assert.Null(ex);
    }

    [Theory]
    [InlineData("https://s3.amazonaws.com", "must be a bare host without a scheme")]
    [InlineData("s3.amazonaws.com/bucket", "must be a bare host without a path")]
    [InlineData("s3 .amazonaws.com", "must not contain whitespace")]
    [InlineData("S3.Amazonaws.com", "must be lowercase")]
    [InlineData("", "must not be empty")]
    public void Rejects_malformed_presigned_trusted_signing_host(string host, string expectedMessage)
    {
        var config = ValidBase();
        config.S3.PresignedTrustedSigningHosts.Add(host);

        var ex = Assert.Throws<ProxyConfigException>(() => ProxyConfigValidator.Validate(config));
        Assert.Contains(expectedMessage, ex.Message);
    }
}
