namespace Aws2Azure.Core.Configuration;

/// <summary>
/// Projects the on-disk <see cref="ConfigDocument"/> onto the resolved
/// <see cref="ProxyConfig"/> model consumed by the runtime. The resolved model and
/// its per-backend credential POCOs are the stable contract shared with the
/// credential resolver and every service module; this translator is the only place
/// that understands the binding-centric JSON shape.
/// </summary>
/// <remarks>
/// Structural errors specific to the new schema (blank/unknown <c>kind</c>, a
/// <c>kind</c> that is invalid for the AWS service it sits under) are reported here
/// with a <c>bindings[i].azure.&lt;service&gt;</c> path. Semantic validation
/// (required fields, mutually exclusive auth shapes, identity-pool resolution) is
/// left to <see cref="ProxyConfigValidator"/>, which runs on the translated model.
/// </remarks>
public static class ConfigDocumentTranslator
{
    public static ProxyConfig ToProxyConfig(ConfigDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var config = new ProxyConfig
        {
            AzureIdentities = document.AzureIdentities,
        };

        ApplyServices(config, document.Services);

        for (var i = 0; i < document.Bindings.Count; i++)
        {
            config.Credentials.Add(TranslateBinding(document.Bindings[i], i));
        }

        return config;
    }

    private static void ApplyServices(ProxyConfig config, ServicesConfig services)
    {
        if (services.S3 is { } s3)
        {
            config.Services["s3"] = new ServiceToggle { Enabled = s3.Enabled };
            if (s3.PresignedTrustedSigningHosts is { } hosts)
            {
                config.S3.PresignedTrustedSigningHosts = hosts;
            }
        }

        if (services.Sqs is { } sqs)
        {
            config.Services["sqs"] = new ServiceToggle { Enabled = sqs.Enabled };
        }

        if (services.DynamoDb is { } ddb)
        {
            config.Services["dynamodb"] = new ServiceToggle { Enabled = ddb.Enabled };
            config.DynamoDb = new DynamoDbSettings
            {
                UseStoredProcedures = ddb.UseStoredProcedures,
                ConsistencyCheck = ddb.ConsistencyCheck,
                CosmosBinaryResponses = ddb.CosmosBinaryResponses,
                CosmosBinaryRequests = ddb.CosmosBinaryRequests,
                EnableGlobalSecondaryIndexQueries = ddb.EnableGlobalSecondaryIndexQueries,
                EnableLocalSecondaryIndexNumericOrdering = ddb.EnableLocalSecondaryIndexNumericOrdering,
            };
        }

        if (services.Sns is { } sns)
        {
            config.Services["sns"] = new ServiceToggle { Enabled = sns.Enabled };
            config.Sns = new SnsSettings { DefaultBackend = sns.DefaultBackend };
        }

        if (services.Kinesis is { } kinesis)
        {
            config.Services["kinesis"] = new ServiceToggle { Enabled = kinesis.Enabled };
        }

        if (services.SecretsManager is { } sm)
        {
            config.Services["secretsmanager"] = new ServiceToggle { Enabled = sm.Enabled };
        }
    }

    private static CredentialEntry TranslateBinding(BindingEntry binding, int index)
    {
        var entry = new CredentialEntry
        {
            AwsAccessKeyId = binding.Aws.AccessKeyId,
            AwsSecretAccessKey = binding.Aws.SecretAccessKey,
        };

        var azure = entry.Azure;
        var set = binding.Azure;

        if (set.S3 is { } s3)
        {
            azure.Blob = TranslateBlob(s3, Path(index, "s3"));
        }

        if (set.Sqs is { } sqs)
        {
            azure.ServiceBus = TranslateServiceBus(sqs, Path(index, "sqs"));
        }

        if (set.DynamoDb is { } ddb)
        {
            azure.Cosmos = TranslateCosmos(ddb, Path(index, "dynamodb"));
        }

        if (set.Sns is { } sns)
        {
            TranslateSns(azure, sns, Path(index, "sns"));
        }

        if (set.Kinesis is { } kinesis)
        {
            azure.EventHubs = TranslateEventHubs(kinesis, Path(index, "kinesis"));
        }

        if (set.SecretsManager is { } sm)
        {
            azure.KeyVault = TranslateKeyVault(sm, Path(index, "secretsmanager"));
        }

        return entry;
    }

    // Which auth.mode values each backend accepts. Enforced up front so a mode that
    // is meaningless for the backend (e.g. clientSecret on Blob, sharedKey on Event
    // Hubs) is rejected at startup instead of being silently coerced into key/SAS
    // auth once the resolved model drops the AzureAuthKind discriminator.
    private static readonly AzureAuthKind[] KeyOnly = { AzureAuthKind.SharedKey };
    private static readonly AzureAuthKind[] SasOnly = { AzureAuthKind.Sas };
    private static readonly AzureAuthKind[] KeyOrAad =
    {
        AzureAuthKind.SharedKey, AzureAuthKind.ManagedIdentity, AzureAuthKind.ClientSecret,
        AzureAuthKind.WorkloadIdentity, AzureAuthKind.Reference,
    };
    private static readonly AzureAuthKind[] SasOrAad =
    {
        AzureAuthKind.Sas, AzureAuthKind.ManagedIdentity, AzureAuthKind.ClientSecret,
        AzureAuthKind.WorkloadIdentity, AzureAuthKind.Reference,
    };
    private static readonly AzureAuthKind[] AadOnly =
    {
        AzureAuthKind.ManagedIdentity, AzureAuthKind.ClientSecret,
        AzureAuthKind.WorkloadIdentity, AzureAuthKind.Reference,
    };

    private static BlobCredentials TranslateBlob(AzureBackendConfig backend, string path)
    {
        RequireKind(backend, path, "blob");
        RequireMode(backend.Auth, path, KeyOnly);
        return new BlobCredentials
        {
            AccountName = backend.Target.AccountName ?? string.Empty,
            ServiceEndpoint = backend.Target.Endpoint,
            AccountKey = backend.Auth.Key ?? string.Empty,
        };
    }

    private static ServiceBusCredentials TranslateServiceBus(AzureBackendConfig backend, string path)
    {
        RequireKind(backend, path, "serviceBus");
        RequireMode(backend.Auth, path, SasOnly);
        return new ServiceBusCredentials
        {
            Namespace = backend.Target.Namespace ?? string.Empty,
            Transport = backend.Target.Transport ?? SqsTransport.Rest,
            SasKeyName = backend.Auth.KeyName ?? string.Empty,
            SasKey = backend.Auth.Key ?? string.Empty,
            Queues = backend.Queues,
        };
    }

    private static CosmosCredentials TranslateCosmos(AzureBackendConfig backend, string path)
    {
        RequireKind(backend, path, "cosmos");
        RequireMode(backend.Auth, path, KeyOrAad);
        var cosmos = new CosmosCredentials
        {
            Endpoint = backend.Target.Endpoint ?? string.Empty,
            DatabaseName = backend.Target.DatabaseName ?? string.Empty,
            PreferredRegions = backend.Target.PreferredRegions,
        };
        ApplyKeyOrAad(backend.Auth, path, key => cosmos.PrimaryKey = key, cosmos);
        return cosmos;
    }

    private static void TranslateSns(AzureCredentials azure, AzureBackendConfig backend, string path)
    {
        var kind = NormalizeKind(backend.Kind, path);
        switch (kind)
        {
            case "servicebustopics":
                RequireMode(backend.Auth, path, SasOrAad);
                var topics = new ServiceBusTopicsCredentials
                {
                    Namespace = backend.Target.Namespace ?? string.Empty,
                    Endpoint = backend.Target.Endpoint,
                    ManagementEndpoint = backend.Target.ManagementEndpoint,
                    SasKeyName = backend.Auth.KeyName ?? string.Empty,
                    SasKey = backend.Auth.Key ?? string.Empty,
                    Topics = backend.Topics,
                };
                ApplyAadOnly(backend.Auth, topics);
                azure.ServiceBusTopics = topics;
                return;
            case "eventgrid":
                RequireMode(backend.Auth, path, KeyOrAad);
                var grid = new EventGridCredentials
                {
                    Endpoint = backend.Target.Endpoint ?? string.Empty,
                    Namespace = backend.Target.Namespace,
                    TopicName = backend.Target.TopicName,
                };
                ApplyKeyOrAad(backend.Auth, path, key => grid.AccessKey = key, grid);
                azure.EventGrid = grid;
                return;
            default:
                throw Invalid(path, backend.Kind, "serviceBusTopics", "eventGrid");
        }
    }

    private static EventHubsCredentials TranslateEventHubs(AzureBackendConfig backend, string path)
    {
        RequireKind(backend, path, "eventHubs");
        RequireMode(backend.Auth, path, SasOrAad);
        var eventHubs = new EventHubsCredentials
        {
            Namespace = backend.Target.Namespace ?? string.Empty,
            Endpoint = backend.Target.Endpoint,
            SasKeyName = backend.Auth.KeyName ?? string.Empty,
            SasKey = backend.Auth.Key ?? string.Empty,
            ShardIteratorSigningKey = backend.ShardIteratorSigningKey,
            Streams = backend.Streams,
        };
        ApplyAadOnly(backend.Auth, eventHubs);
        return eventHubs;
    }

    private static KeyVaultCredentials TranslateKeyVault(AzureBackendConfig backend, string path)
    {
        RequireKind(backend, path, "keyVault");
        RequireMode(backend.Auth, path, AadOnly);
        var keyVault = new KeyVaultCredentials
        {
            VaultUrl = backend.Target.VaultUrl ?? string.Empty,
        };
        ApplyAadOnly(backend.Auth, keyVault);
        return keyVault;
    }

    /// <summary>
    /// Applies an auth block to a backend that supports both a shared key and AAD.
    /// A <see cref="AzureAuthKind.SharedKey"/> mode routes the key through
    /// <paramref name="setKey"/>; AAD/reference modes populate the AAD shape.
    /// </summary>
    private static void ApplyKeyOrAad(AzureAuthConfig auth, string path, Action<string> setKey, IAadAuthCredentials aad)
    {
        if (auth.Mode == AzureAuthKind.SharedKey)
        {
            setKey(auth.Key ?? string.Empty);
            return;
        }
        ApplyAadOnly(auth, aad);
    }

    /// <summary>
    /// Applies an AAD or SAS-adjacent auth block. SAS modes are handled by the
    /// caller (via <c>KeyName</c>/<c>Key</c>); here only the AAD shape and identity
    /// reference are mapped onto <paramref name="aad"/>.
    /// </summary>
    private static void ApplyAadOnly(AzureAuthConfig auth, IAadAuthCredentials aad)
    {
        switch (auth.Mode)
        {
            case AzureAuthKind.Reference:
                aad.Identity = auth.Identity;
                return;
            case AzureAuthKind.ManagedIdentity:
                aad.AuthMode = AzureAuthMode.ManagedIdentity;
                aad.ClientId = auth.ClientId;
                aad.TenantId = auth.TenantId;
                return;
            case AzureAuthKind.WorkloadIdentity:
                aad.AuthMode = AzureAuthMode.WorkloadIdentity;
                aad.ClientId = auth.ClientId;
                aad.TenantId = auth.TenantId;
                return;
            case AzureAuthKind.ClientSecret:
                aad.AuthMode = AzureAuthMode.ClientSecret;
                aad.TenantId = auth.TenantId;
                aad.ClientId = auth.ClientId;
                aad.ClientSecret = auth.ClientSecret;
                return;
            default:
                // SharedKey / Sas leave the AAD shape untouched; the concrete key or
                // SAS fields were mapped by the caller.
                return;
        }
    }

    private static void RequireKind(AzureBackendConfig backend, string path, string expected)
    {
        if (!NormalizeKind(backend.Kind, path).Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            throw Invalid(path, backend.Kind, expected);
        }
    }

    private static void RequireMode(AzureAuthConfig auth, string path, AzureAuthKind[] allowed)
    {
        if (Array.IndexOf(allowed, auth.Mode) >= 0)
        {
            return;
        }

        var expected = new string[allowed.Length];
        for (var i = 0; i < allowed.Length; i++)
        {
            expected[i] = ModeName(allowed[i]);
        }

        throw new ProxyConfigException(
            $"{path}.auth.mode '{ModeName(auth.Mode)}' is not valid for this backend; expected {string.Join(" or ", expected)}.");
    }

    /// <summary>camelCase JSON spelling of an <see cref="AzureAuthKind"/>, for error messages.</summary>
    private static string ModeName(AzureAuthKind mode) => mode switch
    {
        AzureAuthKind.SharedKey => "sharedKey",
        AzureAuthKind.Sas => "sas",
        AzureAuthKind.ManagedIdentity => "managedIdentity",
        AzureAuthKind.ClientSecret => "clientSecret",
        AzureAuthKind.WorkloadIdentity => "workloadIdentity",
        AzureAuthKind.Reference => "reference",
        _ => mode.ToString(),
    };

    private static string NormalizeKind(string kind, string path)
    {
        if (string.IsNullOrWhiteSpace(kind))
        {
            throw new ProxyConfigException($"{path}.kind is required.");
        }
        return kind.Trim().ToLowerInvariant();
    }

    private static ProxyConfigException Invalid(string path, string actual, params string[] expected)
        => new($"{path}.kind '{actual}' is not valid here; expected {string.Join(" or ", expected)}.");

    private static string Path(int index, string service) => $"bindings[{index}].azure.{service}";
}
