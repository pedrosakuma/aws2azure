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

    public static ProxyConfig Load(
        string? jsonFilePath,
        IReadOnlyDictionary<string, string?>? envVars = null)
    {
        ConfigDocument document;

        if (!string.IsNullOrEmpty(jsonFilePath) && File.Exists(jsonFilePath))
        {
            using var stream = File.OpenRead(jsonFilePath);
            document = JsonSerializer.Deserialize(stream, ConfigDocumentJsonContext.Default.ConfigDocument)
                ?? new ConfigDocument();
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

            ApplyOverride(document, path, value);
        }
    }

    private static void ApplyOverride(ConfigDocument document, string[] path, string? value)
    {
        switch (path[0].ToUpperInvariant())
        {
            case "SERVICES":
                ApplyServiceOverride(document.Services, path, value);
                return;
            case "BINDINGS":
                ApplyBindingOverride(document, path, value);
                return;
        }
    }

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
        if (path.Length < 4 || !int.TryParse(path[1], out var index))
        {
            return;
        }

        while (document.Bindings.Count <= index)
        {
            document.Bindings.Add(new BindingEntry());
        }

        var binding = document.Bindings[index];
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
            case "VAULTURL": target.VaultUrl = value; return;
            case "TOPICNAME": target.TopicName = value; return;
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
    {
        if (!string.IsNullOrWhiteSpace(value)
            && Enum.TryParse(value, ignoreCase: true, out result)
            && Enum.IsDefined(result))
        {
            return true;
        }
        result = default;
        return false;
    }
}
