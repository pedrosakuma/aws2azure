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
            var binding = document.Bindings[i];
            if (binding is null)
            {
                throw new ProxyConfigException($"bindings[{i}]: entry must not be null.");
            }
            config.Credentials.Add(TranslateBinding(binding, i));
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
        ValidateBackendFields(backend, path, BackendFields.None);
        ValidateTargetFields(
            backend.Target,
            path,
            TargetFields.Endpoint | TargetFields.AccountName);
        ValidateAuthFields(backend.Auth, path);
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
        ValidateBackendFields(backend, path, BackendFields.Queues);
        ValidateTargetFields(
            backend.Target,
            path,
            TargetFields.Namespace | TargetFields.ManagementEndpoint | TargetFields.Transport);
        ValidateAuthFields(backend.Auth, path);
        return new ServiceBusCredentials
        {
            Namespace = backend.Target.Namespace ?? string.Empty,
            ManagementEndpoint = backend.Target.ManagementEndpoint,
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
        ValidateBackendFields(backend, path, BackendFields.None);
        ValidateTargetFields(
            backend.Target,
            path,
            TargetFields.Endpoint | TargetFields.DatabaseName | TargetFields.PreferredRegions);
        ValidateAuthFields(backend.Auth, path);
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
                ValidateBackendFields(
                    backend,
                    path,
                    BackendFields.Topics | BackendFields.EventGridFallback);
                ValidateTargetFields(
                    backend.Target,
                    path,
                    TargetFields.Namespace | TargetFields.Endpoint | TargetFields.ManagementEndpoint);
                ValidateAuthFields(backend.Auth, path);
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
                if (backend.EventGridFallback is { } fallback)
                {
                    var fallbackPath = path + ".eventGridFallback";
                    RequireKind(fallback, fallbackPath, "eventGrid");
                    azure.EventGrid = TranslateEventGridBackend(fallback, fallbackPath);
                }
                return;
            default:
                throw Invalid(path, backend.Kind, "serviceBusTopics");
        }
    }

    private static EventGridCredentials TranslateEventGridBackend(AzureBackendConfig backend, string path)
    {
        RequireMode(backend.Auth, path, KeyOrAad);
        ValidateBackendFields(backend, path, BackendFields.None);
        ValidateTargetFields(
            backend.Target,
            path,
            TargetFields.Endpoint | TargetFields.Namespace | TargetFields.TopicName);
        ValidateAuthFields(backend.Auth, path);
        var grid = new EventGridCredentials
        {
            Endpoint = backend.Target.Endpoint ?? string.Empty,
            Namespace = backend.Target.Namespace,
            TopicName = backend.Target.TopicName,
        };
        ApplyKeyOrAad(backend.Auth, path, key => grid.AccessKey = key, grid);
        return grid;
    }

    private static EventHubsCredentials TranslateEventHubs(AzureBackendConfig backend, string path)
    {
        RequireKind(backend, path, "eventHubs");
        RequireMode(backend.Auth, path, SasOrAad);
        ValidateBackendFields(
            backend,
            path,
            BackendFields.Streams | BackendFields.ShardIteratorSigningKey);
        ValidateTargetFields(
            backend.Target,
            path,
            TargetFields.Namespace | TargetFields.Endpoint);
        ValidateAuthFields(backend.Auth, path);
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
        ValidateBackendFields(backend, path, BackendFields.None);
        ValidateTargetFields(backend.Target, path, TargetFields.VaultUrl);
        ValidateAuthFields(backend.Auth, path);
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

    [Flags]
    private enum BackendFields
    {
        None = 0,
        Queues = 1,
        Topics = 2,
        Streams = 4,
        ShardIteratorSigningKey = 8,
        EventGridFallback = 16,
    }

    [Flags]
    private enum TargetFields
    {
        None = 0,
        Endpoint = 1,
        AccountName = 2,
        Namespace = 4,
        DatabaseName = 8,
        ManagementEndpoint = 16,
        PreferredRegions = 32,
        Transport = 64,
        VaultUrl = 128,
        TopicName = 256,
    }

    private static void ValidateBackendFields(
        AzureBackendConfig backend,
        string path,
        BackendFields allowed)
    {
        RejectUnsupported(backend.Queues, allowed, BackendFields.Queues, path, "queues");
        RejectUnsupported(backend.Topics, allowed, BackendFields.Topics, path, "topics");
        RejectUnsupported(backend.Streams, allowed, BackendFields.Streams, path, "streams");
        RejectUnsupported(
            backend.ShardIteratorSigningKey,
            allowed,
            BackendFields.ShardIteratorSigningKey,
            path,
            "shardIteratorSigningKey");
        RejectUnsupported(
            backend.EventGridFallback,
            allowed,
            BackendFields.EventGridFallback,
            path,
            "eventGridFallback");
    }

    private static void ValidateTargetFields(
        AzureTargetConfig target,
        string path,
        TargetFields allowed)
    {
        RejectUnsupported(target.Endpoint, allowed, TargetFields.Endpoint, path + ".target", "endpoint");
        RejectUnsupported(target.AccountName, allowed, TargetFields.AccountName, path + ".target", "accountName");
        RejectUnsupported(target.Namespace, allowed, TargetFields.Namespace, path + ".target", "namespace");
        RejectUnsupported(target.DatabaseName, allowed, TargetFields.DatabaseName, path + ".target", "databaseName");
        RejectUnsupported(
            target.ManagementEndpoint,
            allowed,
            TargetFields.ManagementEndpoint,
            path + ".target",
            "managementEndpoint");
        RejectUnsupported(
            target.PreferredRegions,
            allowed,
            TargetFields.PreferredRegions,
            path + ".target",
            "preferredRegions");
        RejectUnsupported(target.Transport, allowed, TargetFields.Transport, path + ".target", "transport");
        RejectUnsupported(target.VaultUrl, allowed, TargetFields.VaultUrl, path + ".target", "vaultUrl");
        RejectUnsupported(target.TopicName, allowed, TargetFields.TopicName, path + ".target", "topicName");
    }

    private static void ValidateAuthFields(AzureAuthConfig auth, string path)
    {
        var allowed = auth.Mode switch
        {
            AzureAuthKind.SharedKey => AuthFields.Key,
            AzureAuthKind.Sas => AuthFields.Key | AuthFields.KeyName,
            AzureAuthKind.ManagedIdentity => AuthFields.ClientId,
            AzureAuthKind.ClientSecret =>
                AuthFields.TenantId | AuthFields.ClientId | AuthFields.ClientSecret,
            AzureAuthKind.WorkloadIdentity => AuthFields.None,
            AzureAuthKind.Reference => AuthFields.Identity,
            _ => AuthFields.None,
        };
        var authPath = path + ".auth";
        RejectUnsupported(auth.Key, allowed, AuthFields.Key, authPath, "key");
        RejectUnsupported(auth.KeyName, allowed, AuthFields.KeyName, authPath, "keyName");
        RejectUnsupported(auth.TenantId, allowed, AuthFields.TenantId, authPath, "tenantId");
        RejectUnsupported(auth.ClientId, allowed, AuthFields.ClientId, authPath, "clientId");
        RejectUnsupported(auth.ClientSecret, allowed, AuthFields.ClientSecret, authPath, "clientSecret");
        RejectUnsupported(auth.Identity, allowed, AuthFields.Identity, authPath, "identity");
    }

    [Flags]
    private enum AuthFields
    {
        None = 0,
        Key = 1,
        KeyName = 2,
        TenantId = 4,
        ClientId = 8,
        ClientSecret = 16,
        Identity = 32,
    }

    private static void RejectUnsupported<TValue, TFields>(
        TValue? value,
        TFields allowed,
        TFields field,
        string path,
        string property)
        where TFields : struct, Enum
    {
        if (value is not null
            && (Convert.ToUInt64(allowed) & Convert.ToUInt64(field)) == 0)
        {
            throw new ProxyConfigException(
                $"{path}.{property} is not valid for this backend shape.");
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
