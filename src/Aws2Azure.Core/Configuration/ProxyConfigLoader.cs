using System.Globalization;
using System.Text.Json;

namespace Aws2Azure.Core.Configuration;

/// <summary>
/// Loads the binding-centric <see cref="ConfigDocument"/> from a JSON file with
/// <c>AWS2AZURE__*</c> environment-variable overrides, then translates it to the
/// resolved <see cref="ProxyConfig"/> consumed by the runtime. Reflection-free: the
/// JSON is parsed via the <see cref="ConfigDocumentJsonContext"/> source generator
/// and env vars are applied as a typed overlay before translation.
/// </summary>
/// <remarks>
/// Env-var convention (ASP.NET Core's <c>__</c> separator), mirroring the JSON shape:
/// <list type="bullet">
/// <item><c>AWS2AZURE__SERVICES__S3__ENABLED=true</c></item>
/// <item><c>AWS2AZURE__SERVICES__DYNAMODB__CONSISTENCYCHECK=required</c></item>
/// <item><c>AWS2AZURE__SERVICES__SNS__DEFAULTBACKEND=eventGrid</c></item>
/// <item><c>AWS2AZURE__BINDINGS__0__AWS__ACCESSKEYID=AKIA...</c></item>
/// <item><c>AWS2AZURE__BINDINGS__0__AWS__SECRETACCESSKEY=...</c></item>
/// <item><c>AWS2AZURE__BINDINGS__0__AZURE__S3__KIND=blob</c></item>
/// <item><c>AWS2AZURE__BINDINGS__0__AZURE__S3__TARGET__ACCOUNTNAME=...</c></item>
/// <item><c>AWS2AZURE__BINDINGS__0__AZURE__S3__AUTH__KEY=...</c></item>
/// </list>
/// </remarks>
public static class ProxyConfigLoader
{
    public const string EnvPrefix = "AWS2AZURE__";
    private const int MaxOverrideIndex = 1023;

    public static string? ResolveConfigFilePath(
        string baseDirectory,
        string? configuredPath)
    {
        if (!string.IsNullOrEmpty(configuredPath))
        {
            return configuredPath;
        }

        var bundledPath = Path.Combine(baseDirectory, "config.json");
        if (File.Exists(bundledPath))
        {
            return bundledPath;
        }

        var releaseExamplePath = Path.Combine(baseDirectory, "config.example.json");
        return File.Exists(releaseExamplePath) ? releaseExamplePath : null;
    }

    public static ProxyConfig Load(
        string? jsonFilePath,
        IReadOnlyDictionary<string, string?>? envVars = null)
    {
        ConfigDocument document;

        if (!string.IsNullOrEmpty(jsonFilePath) && File.Exists(jsonFilePath))
        {
            try
            {
                using var stream = File.OpenRead(jsonFilePath);
                document = JsonSerializer.Deserialize(stream, ConfigDocumentJsonContext.Default.ConfigDocument)
                    ?? new ConfigDocument();
            }
            catch (JsonException exception)
            {
                var path = string.IsNullOrEmpty(exception.Path) ? "$" : exception.Path;
                throw new ProxyConfigException(
                    $"Configuration file '{jsonFilePath}' contains invalid JSON at '{path}'.",
                    exception);
            }
        }
        else
        {
            document = new ConfigDocument();
        }

        var source = envVars ?? CaptureEnvironment();
        ApplyEnvOverrides(document, source);

        return ConfigDocumentTranslator.ToProxyConfig(document);
    }

    private static Dictionary<string, string?> CaptureEnvironment()
    {
        var dict = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            var key = entry.Key?.ToString();
            if (key is null || !key.StartsWith(EnvPrefix, StringComparison.Ordinal))
            {
                continue;
            }
            dict[key] = entry.Value?.ToString();
        }
        return dict;
    }

    private static void ApplyEnvOverrides(ConfigDocument document, IReadOnlyDictionary<string, string?> envVars)
    {
        var backendKindOverrides = CaptureBackendKindOverrides(envVars);
        foreach (var (rawKey, value) in envVars)
        {
            if (!rawKey.StartsWith(EnvPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var path = rawKey.Substring(EnvPrefix.Length).Split("__", StringSplitOptions.None);
            if (path.Length == 0)
            {
                continue;
            }

            ApplyOverride(document, path, value, backendKindOverrides);
        }
    }

    private static Dictionary<string, string> CaptureBackendKindOverrides(
        IReadOnlyDictionary<string, string?> envVars)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (rawKey, value) in envVars)
        {
            if (!rawKey.StartsWith(EnvPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var path = rawKey.Substring(EnvPrefix.Length).Split("__", StringSplitOptions.None);
            if (path.Length != 5
                || !path[0].Equals("BINDINGS", StringComparison.OrdinalIgnoreCase)
                || !int.TryParse(
                    path[1],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var index)
                || index > MaxOverrideIndex
                || !path[2].Equals("AZURE", StringComparison.OrdinalIgnoreCase)
                || !path[4].Equals("KIND", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var service = path[3].ToUpperInvariant();
            if (IsSupportedKind(service, value))
            {
                result[BackendKey(index, service)] = value!;
            }
        }

        return result;
    }

    private static void ApplyOverride(
        ConfigDocument document,
        string[] path,
        string? value,
        IReadOnlyDictionary<string, string> backendKindOverrides)
    {
        switch (path[0].ToUpperInvariant())
        {
            case "SERVICES":
                if (CanApplyServiceOverride(path, value))
                {
                    ApplyServiceOverride(document.Services, path, value);
                }
                return;
            case "BINDINGS":
                if (CanApplyBindingOverride(document, path, value, backendKindOverrides))
                {
                    ApplyBindingOverride(document, path, value);
                }
                return;
        }
    }

    private static bool CanApplyServiceOverride(string[] path, string? value)
    {
        if (path.Length != 3)
        {
            return false;
        }

        var service = path[1].ToUpperInvariant();
        var field = path[2].ToUpperInvariant();
        if (field == "ENABLED")
        {
            return service is "S3" or "SQS" or "KINESIS" or "SECRETSMANAGER" or "SNS" or "DYNAMODB"
                && bool.TryParse(value, out _);
        }

        return service switch
        {
            "SNS" when field == "DEFAULTBACKEND" =>
                TryParseEnum<SnsTopicBackend>(value, out _),
            "DYNAMODB" when field == "USESTOREDPROCEDURES" =>
                TryParseEnum<StoredProcedureMode>(value, out _),
            "DYNAMODB" when field == "CONSISTENCYCHECK" =>
                TryParseEnum<ConsistencyCheckMode>(value, out _),
            "DYNAMODB" when field is "COSMOSBINARYRESPONSES"
                or "COSMOSBINARYREQUESTS"
                or "ENABLEGLOBALSECONDARYINDEXQUERIES"
                or "ENABLELOCALSECONDARYINDEXNUMERICORDERING" =>
                bool.TryParse(value, out _),
            _ => false,
        };
    }

    private static bool CanApplyBindingOverride(
        ConfigDocument document,
        string[] path,
        string? value,
        IReadOnlyDictionary<string, string> backendKindOverrides)
    {
        if (path.Length < 4
            || !int.TryParse(
                path[1],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var index)
            || index > MaxOverrideIndex)
        {
            return false;
        }

        var section = path[2].ToUpperInvariant();
        if (section == "AWS")
        {
            return path.Length == 4
                && path[3].ToUpperInvariant() is "ACCESSKEYID" or "SECRETACCESSKEY";
        }

        if (section != "AZURE" || path.Length < 5)
        {
            return false;
        }

        var service = path[3].ToUpperInvariant();
        if (service is not ("S3" or "SQS" or "DYNAMODB" or "SNS" or "KINESIS" or "SECRETSMANAGER"))
        {
            return false;
        }

        var backendKind = GetEffectiveBackendKind(
            document,
            index,
            service,
            backendKindOverrides);
        var group = path[4].ToUpperInvariant();
        if (group == "KIND")
        {
            return path.Length == 5 && IsSupportedKind(service, value);
        }
        if (group == "SHARDITERATORSIGNINGKEY")
        {
            return service == "KINESIS" && path.Length == 5;
        }
        if (group == "AUTH")
        {
            if (path.Length != 6)
            {
                return false;
            }

            var field = path[5].ToUpperInvariant();
            return IsSupportedAuthOverride(service, backendKind, field, value);
        }
        if (group == "TARGET")
        {
            if (path.Length == 6)
            {
                var field = path[5].ToUpperInvariant();
                return IsSupportedTargetOverride(service, backendKind, field, value);
            }
            return service == "DYNAMODB"
                && path.Length == 7
                && path[5].Equals("PREFERREDREGIONS", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(
                    path[6],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var regionIndex)
                && regionIndex <= MaxOverrideIndex;
        }
        if (group == "QUEUES")
        {
            return service == "SQS"
                && path.Length >= 7
                && path[^1].Equals("TRANSPORT", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(string.Join("__", path, 5, path.Length - 6))
                && TryParseEnum<SqsTransport>(value, out _);
        }
        return false;
    }

    private static bool IsSupportedKind(string service, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        value = value.Trim();
        return service switch
        {
            "S3" => value.Equals("blob", StringComparison.OrdinalIgnoreCase),
            "SQS" => value.Equals("serviceBus", StringComparison.OrdinalIgnoreCase),
            "DYNAMODB" => value.Equals("cosmos", StringComparison.OrdinalIgnoreCase),
            "SNS" => value.Equals("serviceBusTopics", StringComparison.OrdinalIgnoreCase)
                || value.Equals("eventGrid", StringComparison.OrdinalIgnoreCase),
            "KINESIS" => value.Equals("eventHubs", StringComparison.OrdinalIgnoreCase),
            "SECRETSMANAGER" => value.Equals("keyVault", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    private static bool IsSupportedAuthOverride(
        string service,
        string? backendKind,
        string field,
        string? value)
    {
        if (field == "MODE")
        {
            return TryParseEnum<AzureAuthKind>(value, out var mode)
                && IsSupportedAuthMode(service, backendKind, mode);
        }

        return service switch
        {
            "S3" => field == "KEY",
            "SQS" => field is "KEY" or "KEYNAME",
            "DYNAMODB" => field is "KEY" or "TENANTID" or "CLIENTID" or "CLIENTSECRET" or "IDENTITY",
            "SNS" when IsStandaloneEventGrid(backendKind) =>
                field is "KEY" or "TENANTID" or "CLIENTID" or "CLIENTSECRET" or "IDENTITY",
            "SNS" =>
                field is "KEY" or "KEYNAME" or "TENANTID" or "CLIENTID" or "CLIENTSECRET" or "IDENTITY",
            "KINESIS" => field is "KEY" or "KEYNAME" or "TENANTID" or "CLIENTID" or "CLIENTSECRET" or "IDENTITY",
            "SECRETSMANAGER" => field is "TENANTID" or "CLIENTID" or "CLIENTSECRET" or "IDENTITY",
            _ => false,
        };
    }

    private static bool IsSupportedAuthMode(
        string service,
        string? backendKind,
        AzureAuthKind mode)
        => service switch
        {
            "S3" => mode == AzureAuthKind.SharedKey,
            "SQS" => mode == AzureAuthKind.Sas,
            "DYNAMODB" => mode is not AzureAuthKind.Sas,
            "SNS" when IsStandaloneEventGrid(backendKind) => mode is not AzureAuthKind.Sas,
            "SNS" or "KINESIS" => mode is not AzureAuthKind.SharedKey,
            "SECRETSMANAGER" => mode is not (AzureAuthKind.SharedKey or AzureAuthKind.Sas),
            _ => false,
        };

    private static bool IsSupportedTargetOverride(
        string service,
        string? backendKind,
        string field,
        string? value)
        => service switch
        {
            "S3" => field is "ACCOUNTNAME" or "ENDPOINT",
            "SQS" => field is "NAMESPACE" or "MANAGEMENTENDPOINT"
                || field == "TRANSPORT" && TryParseEnum<SqsTransport>(value, out _),
            "DYNAMODB" => field is "ENDPOINT" or "DATABASENAME",
            "SNS" when IsStandaloneEventGrid(backendKind) =>
                field is "ENDPOINT" or "NAMESPACE" or "TOPICNAME",
            "SNS" => field is "ENDPOINT" or "NAMESPACE" or "MANAGEMENTENDPOINT",
            "KINESIS" => field is "NAMESPACE" or "ENDPOINT",
            "SECRETSMANAGER" => field == "VAULTURL",
            _ => false,
        };

    private static string? GetEffectiveBackendKind(
        ConfigDocument document,
        int index,
        string service,
        IReadOnlyDictionary<string, string> backendKindOverrides)
    {
        if (backendKindOverrides.TryGetValue(BackendKey(index, service), out var overrideKind))
        {
            return overrideKind;
        }

        if (index >= document.Bindings.Count || document.Bindings[index] is not { } binding)
        {
            return null;
        }

        return service switch
        {
            "S3" => binding.Azure.S3?.Kind,
            "SQS" => binding.Azure.Sqs?.Kind,
            "DYNAMODB" => binding.Azure.DynamoDb?.Kind,
            "SNS" => binding.Azure.Sns?.Kind,
            "KINESIS" => binding.Azure.Kinesis?.Kind,
            "SECRETSMANAGER" => binding.Azure.SecretsManager?.Kind,
            _ => null,
        };
    }

    private static bool IsStandaloneEventGrid(string? backendKind)
        => backendKind?.Trim().Equals("eventGrid", StringComparison.OrdinalIgnoreCase) == true;

    private static string BackendKey(int index, string service)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"{index}:{service}");

    private static void ApplyServiceOverride(ServicesConfig services, string[] path, string? value)
    {
        if (path.Length < 3)
        {
            return;
        }

        var field = path[2].ToUpperInvariant();
        switch (path[1].ToUpperInvariant())
        {
            case "S3":
                services.S3 ??= new S3ServiceConfig();
                if (field == "ENABLED" && bool.TryParse(value, out var s3Enabled)) services.S3.Enabled = s3Enabled;
                return;
            case "SQS":
                services.Sqs ??= new ServiceToggleConfig();
                if (field == "ENABLED" && bool.TryParse(value, out var sqsEnabled)) services.Sqs.Enabled = sqsEnabled;
                return;
            case "KINESIS":
                services.Kinesis ??= new ServiceToggleConfig();
                if (field == "ENABLED" && bool.TryParse(value, out var kEnabled)) services.Kinesis.Enabled = kEnabled;
                return;
            case "SECRETSMANAGER":
                services.SecretsManager ??= new ServiceToggleConfig();
                if (field == "ENABLED" && bool.TryParse(value, out var smEnabled)) services.SecretsManager.Enabled = smEnabled;
                return;
            case "SNS":
                services.Sns ??= new SnsServiceConfig();
                if (field == "ENABLED" && bool.TryParse(value, out var snsEnabled)) services.Sns.Enabled = snsEnabled;
                else if (field == "DEFAULTBACKEND" && TryParseEnum<SnsTopicBackend>(value, out var backend)) services.Sns.DefaultBackend = backend;
                return;
            case "DYNAMODB":
                services.DynamoDb ??= new DynamoDbServiceConfig();
                ApplyDynamoDbServiceField(services.DynamoDb, field, value);
                return;
        }
    }

    private static void ApplyDynamoDbServiceField(DynamoDbServiceConfig ddb, string field, string? value)
    {
        switch (field)
        {
            case "ENABLED":
                if (bool.TryParse(value, out var enabled)) ddb.Enabled = enabled;
                return;
            case "USESTOREDPROCEDURES":
                if (TryParseEnum<StoredProcedureMode>(value, out var sproc)) ddb.UseStoredProcedures = sproc;
                return;
            case "CONSISTENCYCHECK":
                if (TryParseEnum<ConsistencyCheckMode>(value, out var cc)) ddb.ConsistencyCheck = cc;
                return;
            case "COSMOSBINARYRESPONSES":
                if (bool.TryParse(value, out var cbResp)) ddb.CosmosBinaryResponses = cbResp;
                return;
            case "COSMOSBINARYREQUESTS":
                if (bool.TryParse(value, out var cbReq)) ddb.CosmosBinaryRequests = cbReq;
                return;
            case "ENABLEGLOBALSECONDARYINDEXQUERIES":
                if (bool.TryParse(value, out var gsi)) ddb.EnableGlobalSecondaryIndexQueries = gsi;
                return;
            case "ENABLELOCALSECONDARYINDEXNUMERICORDERING":
                if (bool.TryParse(value, out var lsi)) ddb.EnableLocalSecondaryIndexNumericOrdering = lsi;
                return;
        }
    }

    private static void ApplyBindingOverride(ConfigDocument document, string[] path, string? value)
    {
        // path = [BINDINGS, index, ...]
        if (path.Length < 4 || !int.TryParse(path[1], out var index) || index < 0)
        {
            return;
        }

        while (document.Bindings.Count <= index)
        {
            document.Bindings.Add(new BindingEntry());
        }

        var binding = document.Bindings[index];
        if (binding is null)
        {
            throw new ProxyConfigException(
                $"bindings[{index}]: cannot apply an environment override to a null entry.");
        }
        var section = path[2].ToUpperInvariant();

        if (section == "AWS" && path.Length == 4)
        {
            switch (path[3].ToUpperInvariant())
            {
                case "ACCESSKEYID": binding.Aws.AccessKeyId = value ?? string.Empty; return;
                case "SECRETACCESSKEY": binding.Aws.SecretAccessKey = value ?? string.Empty; return;
            }
            return;
        }

        if (section == "AZURE" && path.Length >= 5)
        {
            var backend = GetOrCreateBackend(binding.Azure, path[3].ToUpperInvariant());
            if (backend is not null)
            {
                ApplyBackendOverride(backend, path, value);
            }
        }
    }

    private static AzureBackendConfig? GetOrCreateBackend(AzureBindingSet set, string service)
    {
        switch (service)
        {
            case "S3": return set.S3 ??= new AzureBackendConfig();
            case "SQS": return set.Sqs ??= new AzureBackendConfig();
            case "DYNAMODB": return set.DynamoDb ??= new AzureBackendConfig();
            case "SNS": return set.Sns ??= new AzureBackendConfig();
            case "KINESIS": return set.Kinesis ??= new AzureBackendConfig();
            case "SECRETSMANAGER": return set.SecretsManager ??= new AzureBackendConfig();
            default: return null;
        }
    }

    private static void ApplyBackendOverride(AzureBackendConfig backend, string[] path, string? value)
    {
        // path = [BINDINGS, i, AZURE, <service>, <group>, ...]
        var group = path[4].ToUpperInvariant();

        if (group == "KIND" && path.Length == 5)
        {
            backend.Kind = value ?? string.Empty;
            return;
        }

        if (group == "SHARDITERATORSIGNINGKEY" && path.Length == 5)
        {
            backend.ShardIteratorSigningKey = value;
            return;
        }

        if (group == "TARGET" && path.Length >= 6)
        {
            ApplyTargetOverride(backend.Target, path, value);
            return;
        }

        if (group == "AUTH" && path.Length == 6)
        {
            ApplyAuthOverride(backend.Auth, path[5].ToUpperInvariant(), value);
            return;
        }

        if (group == "QUEUES" && path.Length >= 7)
        {
            // AZURE/<svc>/QUEUES/<queueName-segments…>/<field>. SQS queue names may
            // contain (consecutive) underscores, so the name spans every segment
            // between QUEUES and the trailing field.
            var queueName = string.Join("__", path, 5, path.Length - 6);
            var queueField = path[^1].ToUpperInvariant();
            backend.Queues ??= new Dictionary<string, SqsQueueSettings>(StringComparer.OrdinalIgnoreCase);
            if (!backend.Queues.TryGetValue(queueName, out var settings) || settings is null)
            {
                settings = new SqsQueueSettings();
                backend.Queues[queueName] = settings;
            }
            if (queueField == "TRANSPORT" && TryParseEnum<SqsTransport>(value, out var qt))
            {
                settings.Transport = qt;
            }
        }
    }

    private static void ApplyTargetOverride(AzureTargetConfig target, string[] path, string? value)
    {
        switch (path[5].ToUpperInvariant())
        {
            case "ENDPOINT": target.Endpoint = value; return;
            case "ACCOUNTNAME": target.AccountName = value; return;
            case "NAMESPACE": target.Namespace = value; return;
            case "DATABASENAME": target.DatabaseName = value; return;
            case "MANAGEMENTENDPOINT": target.ManagementEndpoint = value; return;
            case "TOPICNAME": target.TopicName = value; return;
            case "VAULTURL": target.VaultUrl = value; return;
            case "TRANSPORT":
                if (TryParseEnum<SqsTransport>(value, out var transport)) target.Transport = transport;
                return;
            case "PREFERREDREGIONS":
                if (path.Length == 7 && int.TryParse(path[6], out var regionIndex))
                {
                    target.PreferredRegions ??= new List<string>();
                    while (target.PreferredRegions.Count <= regionIndex)
                    {
                        target.PreferredRegions.Add(string.Empty);
                    }
                    target.PreferredRegions[regionIndex] = value ?? string.Empty;
                }
                return;
        }
    }

    private static void ApplyAuthOverride(AzureAuthConfig auth, string field, string? value)
    {
        switch (field)
        {
            case "MODE":
                if (TryParseEnum<AzureAuthKind>(value, out var mode)) auth.Mode = mode;
                return;
            case "KEY": auth.Key = value; return;
            case "KEYNAME": auth.KeyName = value; return;
            case "TENANTID": auth.TenantId = value; return;
            case "CLIENTID": auth.ClientId = value; return;
            case "CLIENTSECRET": auth.ClientSecret = value; return;
            case "IDENTITY": auth.Identity = value; return;
        }
    }

    private static bool TryParseEnum<TEnum>(string? value, out TEnum result) where TEnum : struct, Enum
        => ConfigEnumParser.TryParse(value, out result);
}
