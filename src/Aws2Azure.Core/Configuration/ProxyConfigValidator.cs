using System.Text;

namespace Aws2Azure.Core.Configuration;

/// <summary>
/// Validates a <see cref="ProxyConfig"/> before the host starts. Accumulates
/// every issue and throws <see cref="ProxyConfigException"/> with all of
/// them so misconfigurations don't have to be fixed one at a time.
/// </summary>
/// <remarks>
/// Error paths speak the on-disk binding-centric vocabulary
/// (<c>bindings[i].aws.*</c>, <c>bindings[i].azure.&lt;service&gt;.target.*</c> /
/// <c>.auth.*</c>) even though validation runs against the resolved
/// <see cref="ProxyConfig"/> model, so a misconfigured field name always matches
/// what the user actually wrote. The <c>azureIdentities</c> pool is the one
/// exception: its entries keep the flat <c>authMode</c>/<c>tenantId</c>/
/// <c>clientId</c>/<c>clientSecret</c> shape (it isn't a <c>target</c>/<c>auth</c>
/// backend block), so its own validation messages use that vocabulary instead.
/// </remarks>
public static class ProxyConfigValidator
{
    public static void Validate(ProxyConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var errors = new List<string>();

        if (config.Credentials.Count == 0)
        {
            errors.Add("bindings: at least one entry is required.");
        }

        if (!Enum.IsDefined(typeof(SnsTopicBackend), config.Sns.DefaultBackend))
        {
            errors.Add($"services.sns.defaultBackend: unknown value '{(int)config.Sns.DefaultBackend}'.");
        }

        if (!Enum.IsDefined(typeof(ConsistencyCheckMode), config.DynamoDb.ConsistencyCheck))
        {
            errors.Add($"services.dynamodb.consistencyCheck: unknown value '{(int)config.DynamoDb.ConsistencyCheck}'.");
        }

        ValidatePresignedTrustedSigningHosts(config, errors);

        ResolveAzureIdentities(config, errors);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < config.Credentials.Count; i++)
        {
            var entry = config.Credentials[i];
            var prefix = $"bindings[{i}]";

            if (string.IsNullOrWhiteSpace(entry.AwsAccessKeyId))
            {
                errors.Add($"{prefix}.aws.accessKeyId: required.");
            }
            else if (!seen.Add(entry.AwsAccessKeyId))
            {
                errors.Add($"{prefix}.aws.accessKeyId: duplicate value '{entry.AwsAccessKeyId}'.");
            }

            if (string.IsNullOrWhiteSpace(entry.AwsSecretAccessKey))
            {
                errors.Add($"{prefix}.aws.secretAccessKey: required.");
            }

            ValidateAzure(entry.Azure, config.Sns, prefix + ".azure", errors);
        }

        foreach (var (name, toggle) in config.Services)
        {
            if (toggle is null)
            {
                errors.Add($"services.{name}: entry is null.");
            }
        }

        if (errors.Count > 0)
        {
            var sb = new StringBuilder();
            sb.Append("Configuration is invalid:");
            foreach (var error in errors)
            {
                sb.Append("\n  - ").Append(error);
            }
            throw new ProxyConfigException(sb.ToString());
        }
    }

    private static void ValidateAzure(AzureCredentials azure, SnsSettings snsSettings, string prefix, List<string> errors)
    {
        if (azure.Blob is { } blob)
        {
            var s3Prefix = prefix + ".s3";
            if (string.IsNullOrWhiteSpace(blob.AccountName))
            {
                errors.Add($"{s3Prefix}.target.accountName: required.");
            }
            if (string.IsNullOrWhiteSpace(blob.AccountKey))
            {
                errors.Add($"{s3Prefix}.auth.key: required.");
            }
            if (!string.IsNullOrWhiteSpace(blob.ServiceEndpoint))
            {
                ValidateAbsoluteUri(blob.ServiceEndpoint, $"{s3Prefix}.target.endpoint", errors, Uri.UriSchemeHttp, Uri.UriSchemeHttps);
            }
        }

        if (azure.ServiceBus is { } sb)
        {
            var sqsPrefix = prefix + ".sqs";
            if (string.IsNullOrWhiteSpace(sb.Namespace))
            {
                errors.Add($"{sqsPrefix}.target.namespace: required.");
            }
            if (string.IsNullOrWhiteSpace(sb.SasKeyName))
            {
                errors.Add($"{sqsPrefix}.auth.keyName: required.");
            }
            if (string.IsNullOrWhiteSpace(sb.SasKey))
            {
                errors.Add($"{sqsPrefix}.auth.key: required.");
            }
            if (!Enum.IsDefined(typeof(SqsTransport), sb.Transport))
            {
                errors.Add($"{sqsPrefix}.target.transport: unknown value '{(int)sb.Transport}'.");
            }
            if (sb.Queues is { } queues)
            {
                foreach (var (name, settings) in queues)
                {
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        errors.Add($"{sqsPrefix}.queues: queue name must be non-empty.");
                        continue;
                    }
                    if (settings is null)
                    {
                        errors.Add($"{sqsPrefix}.queues.{name}: entry is null.");
                        continue;
                    }
                    if (settings.Transport is { } t && !Enum.IsDefined(typeof(SqsTransport), t))
                    {
                        errors.Add($"{sqsPrefix}.queues.{name}.transport: unknown value '{(int)t}'.");
                    }
                }
            }
        }

        if (azure.ServiceBusTopics is { } serviceBusTopics)
        {
            ValidateServiceBusTopics(serviceBusTopics, azure.EventGrid, snsSettings, prefix + ".sns", errors);
        }

        if (azure.Cosmos is { } cosmos)
        {
            var dynamoDbPrefix = prefix + ".dynamodb";
            if (string.IsNullOrWhiteSpace(cosmos.Endpoint))
            {
                errors.Add($"{dynamoDbPrefix}.target.endpoint: required.");
            }
            if (string.IsNullOrWhiteSpace(cosmos.DatabaseName))
            {
                errors.Add($"{dynamoDbPrefix}.target.databaseName: required.");
            }
            if (cosmos.PreferredRegions is { } preferredRegions)
            {
                for (var r = 0; r < preferredRegions.Count; r++)
                {
                    if (string.IsNullOrWhiteSpace(preferredRegions[r]))
                    {
                        errors.Add($"{dynamoDbPrefix}.target.preferredRegions[{r}]: region name must be non-empty.");
                    }
                }
            }

            ValidateDualAuth(
                dynamoDbPrefix,
                errors,
                sasLabel: "auth.key",
                authShapeLabel: "auth.key",
                hasSasPart1: !string.IsNullOrWhiteSpace(cosmos.PrimaryKey),
                hasSasPart2: !string.IsNullOrWhiteSpace(cosmos.PrimaryKey),
                sasRequirementMessage: "either auth.key OR (auth.tenantId+auth.clientId+auth.clientSecret) is required.",
                sasPairRequirementMessage: null,
                hasTenant: !string.IsNullOrWhiteSpace(cosmos.TenantId),
                hasClientId: !string.IsNullOrWhiteSpace(cosmos.ClientId),
                hasClientSecret: !string.IsNullOrWhiteSpace(cosmos.ClientSecret),
                mode: cosmos.AuthMode);
        }

        if (azure.EventHubs is { } eh)
        {
            var kinesisPrefix = prefix + ".kinesis";
            if (string.IsNullOrWhiteSpace(eh.Namespace))
            {
                errors.Add($"{kinesisPrefix}.target.namespace: required.");
            }

            if (!string.IsNullOrEmpty(eh.Endpoint))
            {
                ValidateAbsoluteUri(eh.Endpoint, $"{kinesisPrefix}.target.endpoint", errors,
                    Uri.UriSchemeHttp, Uri.UriSchemeHttps, "amqp", "amqps");
            }

            ValidateDualAuth(
                kinesisPrefix,
                errors,
                sasLabel: "auth.keyName/auth.key",
                authShapeLabel: "auth.keyName/auth.key",
                hasSasPart1: !string.IsNullOrWhiteSpace(eh.SasKeyName),
                hasSasPart2: !string.IsNullOrWhiteSpace(eh.SasKey),
                sasRequirementMessage: "either (auth.keyName+auth.key) OR (auth.tenantId+auth.clientId+auth.clientSecret) is required.",
                sasPairRequirementMessage: "SAS auth requires both auth.keyName and auth.key.",
                hasTenant: !string.IsNullOrWhiteSpace(eh.TenantId),
                hasClientId: !string.IsNullOrWhiteSpace(eh.ClientId),
                hasClientSecret: !string.IsNullOrWhiteSpace(eh.ClientSecret),
                mode: eh.AuthMode);

            if (eh.Streams is { } streams)
            {
                foreach (var (name, settings) in streams)
                {
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        errors.Add($"{kinesisPrefix}.streams: stream name must be non-empty.");
                        continue;
                    }
                    if (settings is null)
                    {
                        errors.Add($"{kinesisPrefix}.streams.{name}: entry is null.");
                        continue;
                    }

                    if (settings.PartitionCount is <= 0)
                    {
                        errors.Add($"{kinesisPrefix}.streams.{name}.partitionCount: must be greater than zero when set.");
                    }
                }
            }
        }

        if (azure.EventGrid is { } eventGrid)
        {
            ValidateEventGrid(eventGrid, prefix + ".sns", errors);
        }

        if (azure.KeyVault is { } keyVault)
        {
            var keyVaultPrefix = prefix + ".secretsmanager";
            if (string.IsNullOrWhiteSpace(keyVault.VaultUrl))
            {
                errors.Add($"{keyVaultPrefix}.target.vaultUrl: required.");
            }
            else
            {
                ValidateAbsoluteUri(keyVault.VaultUrl, $"{keyVaultPrefix}.target.vaultUrl", errors, Uri.UriSchemeHttps);
            }

            var hasTenant = !string.IsNullOrWhiteSpace(keyVault.TenantId);
            var hasClientId = !string.IsNullOrWhiteSpace(keyVault.ClientId);
            var hasClientSecret = !string.IsNullOrWhiteSpace(keyVault.ClientSecret);
            ValidateAadShape(
                keyVaultPrefix,
                keyVault.AuthMode,
                hasTenant,
                hasClientId,
                hasClientSecret,
                errors,
                clientSecretRequirementMessage: "auth.tenantId, auth.clientId, and auth.clientSecret are required together for Key Vault AAD auth.");
        }
    }

    private static void ValidateServiceBusTopics(
        ServiceBusTopicsCredentials credentials,
        EventGridCredentials? eventGrid,
        SnsSettings snsSettings,
        string prefix,
        List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(credentials.Namespace))
        {
            errors.Add($"{prefix}.target.namespace: required.");
        }

        if (!string.IsNullOrWhiteSpace(credentials.Endpoint))
        {
            ValidateAbsoluteUri(credentials.Endpoint, $"{prefix}.target.endpoint", errors,
                Uri.UriSchemeHttp, Uri.UriSchemeHttps, "amqp", "amqps");
        }

        if (!string.IsNullOrWhiteSpace(credentials.ManagementEndpoint))
        {
            ValidateAbsoluteUri(credentials.ManagementEndpoint, $"{prefix}.target.managementEndpoint", errors,
                Uri.UriSchemeHttp, Uri.UriSchemeHttps);
        }

        ValidateDualAuth(
            prefix,
            errors,
            sasLabel: "auth.keyName/auth.key",
            authShapeLabel: "auth.keyName/auth.key",
            hasSasPart1: !string.IsNullOrWhiteSpace(credentials.SasKeyName),
            hasSasPart2: !string.IsNullOrWhiteSpace(credentials.SasKey),
            sasRequirementMessage: "either (auth.keyName+auth.key) OR (auth.tenantId+auth.clientId+auth.clientSecret) is required.",
            sasPairRequirementMessage: "SAS auth requires both auth.keyName and auth.key.",
            hasTenant: !string.IsNullOrWhiteSpace(credentials.TenantId),
            hasClientId: !string.IsNullOrWhiteSpace(credentials.ClientId),
            hasClientSecret: !string.IsNullOrWhiteSpace(credentials.ClientSecret),
            mode: credentials.AuthMode);

        if (snsSettings.DefaultBackend == SnsTopicBackend.EventGrid)
        {
            ValidateSnsEventGridRoute(prefix + ": services.sns.defaultBackend=EventGrid", eventGrid, endpointOverride: null, accessKeyOverride: null, errors);
        }

        if (credentials.Topics is { } topics)
        {
            foreach (var (name, settings) in topics)
            {
                if (string.IsNullOrWhiteSpace(name))
                {
                    errors.Add($"{prefix}.topics: topic name must be non-empty.");
                    continue;
                }
                if (settings is null)
                {
                    errors.Add($"{prefix}.topics.{name}: entry is null.");
                    continue;
                }
                if (!Enum.IsDefined(typeof(SnsTopicBackend), settings.Backend))
                {
                    errors.Add($"{prefix}.topics.{name}.backend: unknown value '{(int)settings.Backend}'.");
                }
                if (settings.ServiceBusTopicName is not null && string.IsNullOrWhiteSpace(settings.ServiceBusTopicName))
                {
                    errors.Add($"{prefix}.topics.{name}.serviceBusTopicName: must be non-empty when set.");
                }
                if (!string.IsNullOrWhiteSpace(settings.EventGridTopicEndpoint))
                {
                    ValidateAbsoluteUri(settings.EventGridTopicEndpoint, $"{prefix}.topics.{name}.eventGridTopicEndpoint", errors, Uri.UriSchemeHttps);
                }
                if (settings.EventGridAccessKey is not null && string.IsNullOrWhiteSpace(settings.EventGridAccessKey))
                {
                    errors.Add($"{prefix}.topics.{name}.eventGridAccessKey: must be non-empty when set.");
                }
                if (settings.Backend == SnsTopicBackend.EventGrid)
                {
                    ValidateSnsEventGridRoute($"{prefix}.topics.{name}", eventGrid, settings.EventGridTopicEndpoint, settings.EventGridAccessKey, errors);
                }
            }
        }
    }

    private static void ValidateSnsEventGridRoute(
        string prefix,
        EventGridCredentials? credentials,
        string? endpointOverride,
        string? accessKeyOverride,
        List<string> errors)
    {
        var hasEndpoint = !string.IsNullOrWhiteSpace(endpointOverride)
            || HasEventGridEndpoint(credentials);
        if (!hasEndpoint)
        {
            errors.Add($"{prefix}: EventGrid backend requires either eventGridTopicEndpoint or this binding's azure.sns to use kind=eventGrid with target.endpoint/(target.namespace+target.topicName) set.");
        }

        var hasOverrideKey = !string.IsNullOrWhiteSpace(accessKeyOverride);
        var hasAccessKey = hasOverrideKey
            || !string.IsNullOrWhiteSpace(credentials?.AccessKey);
        var mode = credentials?.AuthMode ?? AzureAuthMode.ClientSecret;
        var hasTenant = !string.IsNullOrWhiteSpace(credentials?.TenantId);
        var hasClientId = !string.IsNullOrWhiteSpace(credentials?.ClientId);
        var hasClientSecret = !string.IsNullOrWhiteSpace(credentials?.ClientSecret);
        var hasCompleteAad = mode switch
        {
            AzureAuthMode.ClientSecret => hasTenant && hasClientId && hasClientSecret,
            AzureAuthMode.ManagedIdentity => !hasTenant && !hasClientSecret,
            AzureAuthMode.WorkloadIdentity => !hasTenant && !hasClientId && !hasClientSecret && HasWorkloadIdentityEnvironment(),
            _ => false,
        };
        if (!hasAccessKey && !hasCompleteAad)
        {
            errors.Add($"{prefix}: EventGrid backend requires either eventGridAccessKey or this binding's azure.sns to use kind=eventGrid with auth.key/(auth.tenantId+auth.clientId+auth.clientSecret) set.");
        }

        // A per-topic eventGridAccessKey override is pure key-auth at runtime
        // (SnsTopicRouting prefers it and EventGridPublisher short-circuits before AAD),
        // so it is independent of the global azure.sns authMode/AAD shape.
        if (hasOverrideKey)
        {
            return;
        }
        if (!hasAccessKey)
        {
            ValidateAadShape(prefix, mode, hasTenant, hasClientId, hasClientSecret, errors);
        }
        else
        {
            ValidateKeyShape(prefix, errors, "auth.key", mode, hasTenant || hasClientId || hasClientSecret, "auth.key");
        }
    }

    private static void ValidateEventGrid(EventGridCredentials credentials, string prefix, List<string> errors)
    {
        if (!HasEventGridEndpoint(credentials))
        {
            errors.Add($"{prefix}: either target.endpoint OR (target.namespace+target.topicName) is required.");
        }
        else if (!string.IsNullOrWhiteSpace(credentials.Endpoint))
        {
            ValidateAbsoluteUri(credentials.Endpoint, $"{prefix}.target.endpoint", errors, Uri.UriSchemeHttps);
        }

        if (credentials.Namespace is not null && string.IsNullOrWhiteSpace(credentials.Namespace))
        {
            errors.Add($"{prefix}.target.namespace: must be non-empty when set.");
        }
        if (credentials.TopicName is not null && string.IsNullOrWhiteSpace(credentials.TopicName))
        {
            errors.Add($"{prefix}.target.topicName: must be non-empty when set.");
        }

        ValidateDualAuth(
            prefix,
            errors,
            sasLabel: "auth.key",
            authShapeLabel: "auth.key",
            hasSasPart1: !string.IsNullOrWhiteSpace(credentials.AccessKey),
            hasSasPart2: !string.IsNullOrWhiteSpace(credentials.AccessKey),
            sasRequirementMessage: "either auth.key OR (auth.tenantId+auth.clientId+auth.clientSecret) is required.",
            sasPairRequirementMessage: null,
            hasTenant: !string.IsNullOrWhiteSpace(credentials.TenantId),
            hasClientId: !string.IsNullOrWhiteSpace(credentials.ClientId),
            hasClientSecret: !string.IsNullOrWhiteSpace(credentials.ClientSecret),
            mode: credentials.AuthMode);
    }

    private static bool HasEventGridEndpoint(EventGridCredentials? credentials)
        => credentials is not null
            && (!string.IsNullOrWhiteSpace(credentials.Endpoint)
                || (!string.IsNullOrWhiteSpace(credentials.Namespace)
                    && !string.IsNullOrWhiteSpace(credentials.TopicName)));

    private static void ValidateDualAuth(
        string prefix,
        List<string> errors,
        string sasLabel,
        string authShapeLabel,
        bool hasSasPart1,
        bool hasSasPart2,
        string sasRequirementMessage,
        string? sasPairRequirementMessage,
        bool hasTenant,
        bool hasClientId,
        bool hasClientSecret,
        AzureAuthMode mode)
    {
        var hasAnySas = hasSasPart1 || hasSasPart2;
        var hasCompleteSas = hasSasPart1 && hasSasPart2;
        var hasAnyAad = hasTenant || hasClientId || hasClientSecret;
        var hasCompleteAad = hasTenant && hasClientId && hasClientSecret;

        if (hasCompleteSas)
        {
            ValidateKeyShape(prefix, errors, authShapeLabel, mode, hasAnyAad, sasLabel);
            return;
        }

        if (mode != AzureAuthMode.ClientSecret)
        {
            if (hasAnySas && sasPairRequirementMessage is not null)
            {
                errors.Add($"{prefix}: {sasPairRequirementMessage}");
            }

            ValidateAadShape(prefix, mode, hasTenant, hasClientId, hasClientSecret, errors);
            if (hasAnySas && hasAnyAad)
            {
                errors.Add($"{prefix}: {sasLabel} and AAD fields are mutually exclusive — supply one shape.");
            }
            return;
        }

        if (!hasCompleteAad)
        {
            if (hasAnySas && !hasCompleteSas && sasPairRequirementMessage is not null)
            {
                errors.Add($"{prefix}: {sasPairRequirementMessage}");
            }
            else if (hasAnyAad && !hasCompleteAad)
            {
                errors.Add($"{prefix}: AAD requires auth.tenantId, auth.clientId, and auth.clientSecret together.");
            }
            else
            {
                errors.Add($"{prefix}: {sasRequirementMessage}");
            }
        }

        if (hasAnySas && hasAnyAad)
        {
            errors.Add($"{prefix}: {sasLabel} and AAD fields are mutually exclusive — supply one shape.");
        }
    }

    private static void ValidateKeyShape(
        string prefix,
        List<string> errors,
        string authShapeLabel,
        AzureAuthMode mode,
        bool hasAnyAad,
        string mutualExclusionLabel)
    {
        if (!Enum.IsDefined(typeof(AzureAuthMode), mode))
        {
            errors.Add($"{prefix}.auth.mode: unknown value '{(int)mode}'.");
            return;
        }

        if (mode != AzureAuthMode.ClientSecret)
        {
            errors.Add($"{prefix}: auth.mode '{mode}' cannot be combined with {authShapeLabel} auth — managed/workload identity replaces the secret, not the key.");
        }

        if (hasAnyAad)
        {
            errors.Add($"{prefix}: {mutualExclusionLabel} and AAD fields are mutually exclusive — supply one shape.");
        }
    }

    private static void ValidateAadShape(
        string prefix,
        AzureAuthMode mode,
        bool hasTenant,
        bool hasClientId,
        bool hasClientSecret,
        List<string> errors,
        string? clientSecretRequirementMessage = null,
        bool checkWorkloadEnvironment = true,
        string modeField = "auth.mode",
        string tenantField = "auth.tenantId",
        string clientIdField = "auth.clientId",
        string clientSecretField = "auth.clientSecret")
    {
        if (!Enum.IsDefined(typeof(AzureAuthMode), mode))
        {
            errors.Add($"{prefix}.{modeField}: unknown value '{(int)mode}'.");
            return;
        }

        switch (mode)
        {
            case AzureAuthMode.ClientSecret:
                if (!hasTenant || !hasClientId || !hasClientSecret)
                {
                    errors.Add($"{prefix}: {clientSecretRequirementMessage ?? $"AAD requires {tenantField}, {clientIdField}, and {clientSecretField} together."}");
                }
                break;
            case AzureAuthMode.ManagedIdentity:
                if (hasClientSecret)
                {
                    errors.Add($"{prefix}: {modeField} 'ManagedIdentity' must not specify {clientSecretField}.");
                }
                if (hasTenant)
                {
                    errors.Add($"{prefix}: {modeField} 'ManagedIdentity' must not specify {tenantField}.");
                }
                break;
            case AzureAuthMode.WorkloadIdentity:
                if (hasTenant || hasClientId || hasClientSecret)
                {
                    errors.Add($"{prefix}: {modeField} 'WorkloadIdentity' takes tenant/client/token from AZURE_* environment variables; do not set {tenantField}/{clientIdField}/{clientSecretField}.");
                }
                if (checkWorkloadEnvironment)
                {
                    ValidateWorkloadIdentityEnvironment(prefix, modeField, errors);
                }
                break;
        }
    }

    /// <summary>
    /// Resolves every AAD-capable backend block's optional <c>identity</c>
    /// reference against the top-level <c>azureIdentities</c> pool, mutating the
    /// block's effective AAD fields in place so all downstream validation and
    /// token dispatch read the resolved shape. Runs once at startup. Dangling
    /// references, ambiguous inline+reference combinations, and malformed named
    /// identities all fail loud.
    /// </summary>
    private static void ValidatePresignedTrustedSigningHosts(ProxyConfig config, List<string> errors)
    {
        var hosts = config.S3.PresignedTrustedSigningHosts;
        for (var i = 0; i < hosts.Count; i++)
        {
            var prefix = $"services.s3.presignedTrustedSigningHosts[{i}]";
            var host = hosts[i];
            if (string.IsNullOrWhiteSpace(host))
            {
                errors.Add($"{prefix}: must not be empty.");
                continue;
            }

            if (host.Contains("://", StringComparison.Ordinal))
            {
                errors.Add($"{prefix}: must be a bare host without a scheme (got '{host}').");
            }

            if (host.Contains('/', StringComparison.Ordinal))
            {
                errors.Add($"{prefix}: must be a bare host without a path (got '{host}').");
            }

            if (host.AsSpan().IndexOfAny(' ', '\t') >= 0)
            {
                errors.Add($"{prefix}: must not contain whitespace (got '{host}').");
            }

            if (!string.Equals(host, host.ToLowerInvariant(), StringComparison.Ordinal))
            {
                errors.Add($"{prefix}: must be lowercase (SigV4 host is case-normalized; got '{host}').");
            }
        }
    }

    private static void ResolveAzureIdentities(ProxyConfig config, List<string> errors)
    {
        var identities = config.AzureIdentities;
        if (identities is not null)
        {
            foreach (var (name, identity) in identities)
            {
                if (identity is null)
                {
                    errors.Add($"azureIdentities.{name}: entry is null.");
                    continue;
                }

                // The named identity's own shape is validated without the
                // WorkloadIdentity environment check: an unreferenced WI identity
                // must not fail startup on a host that simply isn't using it. The
                // env check runs at the consuming block once the reference is applied.
                // The pool entry itself keeps the flat authMode/tenantId/clientId/
                // clientSecret shape (it isn't a target/auth backend block), so its
                // messages use that vocabulary rather than auth.*.
                ValidateAadShape(
                    $"azureIdentities.{name}",
                    identity.AuthMode,
                    !string.IsNullOrWhiteSpace(identity.TenantId),
                    !string.IsNullOrWhiteSpace(identity.ClientId),
                    !string.IsNullOrWhiteSpace(identity.ClientSecret),
                    errors,
                    checkWorkloadEnvironment: false,
                    modeField: "authMode",
                    tenantField: "tenantId",
                    clientIdField: "clientId",
                    clientSecretField: "clientSecret");
            }
        }

        for (var i = 0; i < config.Credentials.Count; i++)
        {
            var azure = config.Credentials[i].Azure;
            var prefix = $"bindings[{i}].azure";
            ResolveBlockIdentity(identities, azure.Cosmos, prefix + ".dynamodb", errors);
            ResolveBlockIdentity(identities, azure.EventHubs, prefix + ".kinesis", errors);
            ResolveBlockIdentity(identities, azure.ServiceBusTopics, prefix + ".sns", errors);
            ResolveBlockIdentity(identities, azure.EventGrid, prefix + ".sns", errors);
            ResolveBlockIdentity(identities, azure.KeyVault, prefix + ".secretsmanager", errors);
        }
    }

    private static void ResolveBlockIdentity(
        Dictionary<string, AzureIdentity>? identities,
        IAadAuthCredentials? block,
        string prefix,
        List<string> errors)
    {
        if (block is null || string.IsNullOrWhiteSpace(block.Identity))
        {
            return;
        }

        var name = block.Identity;

        // A named reference replaces the inline AAD shape wholesale; allowing both
        // is ambiguous, so fail loud rather than silently letting one win.
        if (block.AuthMode != AzureAuthMode.ClientSecret
            || !string.IsNullOrWhiteSpace(block.TenantId)
            || !string.IsNullOrWhiteSpace(block.ClientId)
            || !string.IsNullOrWhiteSpace(block.ClientSecret))
        {
            errors.Add($"{prefix}.auth: identity reference '{name}' cannot be combined with inline auth.mode/auth.tenantId/auth.clientId/auth.clientSecret fields.");
            return;
        }

        if (identities is null || !identities.TryGetValue(name, out var identity) || identity is null)
        {
            errors.Add($"{prefix}.auth: identity reference '{name}' was not found in azureIdentities.");
            return;
        }

        block.AuthMode = identity.AuthMode;
        block.TenantId = identity.TenantId;
        block.ClientId = identity.ClientId;
        block.ClientSecret = identity.ClientSecret;

        // Consume the reference so resolution is idempotent: a second Validate()
        // on the same config must not see Identity set alongside the now-applied
        // non-default AuthMode and misfire the inline-ambiguity check.
        block.Identity = null;
    }

    private static bool HasWorkloadIdentityEnvironment()
        => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AZURE_TENANT_ID"))
            && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AZURE_CLIENT_ID"))
            && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AZURE_FEDERATED_TOKEN_FILE"));

    private static void ValidateWorkloadIdentityEnvironment(string prefix, string modeField, List<string> errors)
    {
        var missing = new List<string>(3);
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AZURE_TENANT_ID"))) missing.Add("AZURE_TENANT_ID");
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AZURE_CLIENT_ID"))) missing.Add("AZURE_CLIENT_ID");
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AZURE_FEDERATED_TOKEN_FILE"))) missing.Add("AZURE_FEDERATED_TOKEN_FILE");
        if (missing.Count > 0)
        {
            errors.Add($"{prefix}: {modeField} 'WorkloadIdentity' requires the AZURE_TENANT_ID, AZURE_CLIENT_ID and AZURE_FEDERATED_TOKEN_FILE environment variables. Missing: {string.Join(", ", missing)}.");
        }
    }

    private static void ValidateAbsoluteUri(string value, string field, List<string> errors, params string[] allowedSchemes)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            errors.Add($"{field}: must be an absolute URI when set.");
            return;
        }

        foreach (var scheme in allowedSchemes)
        {
            if (uri.Scheme.Equals(scheme, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        if (allowedSchemes.Length == 1)
        {
            errors.Add($"{field}: must use {allowedSchemes[0]} scheme.");
        }
        else if (allowedSchemes.Length == 2)
        {
            errors.Add($"{field}: must use {allowedSchemes[0]} or {allowedSchemes[1]} scheme.");
        }
        else
        {
            errors.Add($"{field}: must use {string.Join(", ", allowedSchemes[..^1])} or {allowedSchemes[^1]} scheme.");
        }
    }
}
