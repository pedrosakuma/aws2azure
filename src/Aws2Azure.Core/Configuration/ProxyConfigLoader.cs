using System.Text.Json;

namespace Aws2Azure.Core.Configuration;

/// <summary>
/// Loads <see cref="ProxyConfig"/> from a JSON file with <c>AWS2AZURE__*</c>
/// environment-variable overrides. Reflection-free: the JSON is parsed via
/// the <see cref="ProxyConfigJsonContext"/> source generator and env vars
/// are applied as a typed overlay.
/// </summary>
/// <remarks>
/// Env-var convention (same as ASP.NET Core's <c>__</c> separator):
/// <list type="bullet">
/// <item><c>AWS2AZURE__SERVICES__S3__ENABLED=true</c></item>
/// <item><c>AWS2AZURE__CREDENTIALS__0__AWSACCESSKEYID=AKIA...</c></item>
/// <item><c>AWS2AZURE__CREDENTIALS__0__AWSSECRETACCESSKEY=...</c></item>
/// <item><c>AWS2AZURE__CREDENTIALS__0__AZURE__BLOB__ACCOUNTNAME=...</c></item>
/// <item><c>AWS2AZURE__CREDENTIALS__0__AZURE__BLOB__ACCOUNTKEY=...</c></item>
/// </list>
/// </remarks>
public static class ProxyConfigLoader
{
    public const string EnvPrefix = "AWS2AZURE__";

    public static ProxyConfig Load(
        string? jsonFilePath,
        IReadOnlyDictionary<string, string?>? envVars = null)
    {
        ProxyConfig config;

        if (!string.IsNullOrEmpty(jsonFilePath) && File.Exists(jsonFilePath))
        {
            using var stream = File.OpenRead(jsonFilePath);
            config = JsonSerializer.Deserialize(stream, ProxyConfigJsonContext.Default.ProxyConfig)
                ?? new ProxyConfig();
        }
        else
        {
            config = new ProxyConfig();
        }

        var source = envVars ?? CaptureEnvironment();
        ApplyEnvOverrides(config, source);

        return config;
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

    private static void ApplyEnvOverrides(ProxyConfig config, IReadOnlyDictionary<string, string?> envVars)
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

            ApplyOverride(config, path, value);
        }
    }

    private static void ApplyOverride(ProxyConfig config, string[] path, string? value)
    {
        var head = path[0].ToUpperInvariant();

        if (head == "SERVICES" && path.Length == 3)
        {
            var serviceName = path[1].ToLowerInvariant();
            if (!config.Services.TryGetValue(serviceName, out var toggle) || toggle is null)
            {
                toggle = new ServiceToggle();
                config.Services[serviceName] = toggle;
            }

            if (path[2].Equals("ENABLED", StringComparison.OrdinalIgnoreCase)
                && bool.TryParse(value, out var enabled))
            {
                toggle.Enabled = enabled;
            }
            return;
        }

        // AWS2AZURE__DYNAMODB__USESTOREDPROCEDURES=preferred
        if (head == "DYNAMODB" && path.Length == 2)
        {
            var field = path[1].ToUpperInvariant();
            if (field == "USESTOREDPROCEDURES" && TryParseSprocMode(value, out var mode))
            {
                config.DynamoDb.UseStoredProcedures = mode;
            }
            else if (field == "CONSISTENCYCHECK" && TryParseConsistencyCheckMode(value, out var ccMode))
            {
                config.DynamoDb.ConsistencyCheck = ccMode;
            }
            return;
        }

        if (head == "CREDENTIALS" && path.Length >= 3 && int.TryParse(path[1], out var index))
        {
            while (config.Credentials.Count <= index)
            {
                config.Credentials.Add(new CredentialEntry());
            }

            var entry = config.Credentials[index];
            var leaf = path[2].ToUpperInvariant();

            switch (path.Length, leaf)
            {
                case (3, "AWSACCESSKEYID"):
                    entry.AwsAccessKeyId = value ?? string.Empty;
                    return;
                case (3, "AWSSECRETACCESSKEY"):
                    entry.AwsSecretAccessKey = value ?? string.Empty;
                    return;
            }

            if (leaf == "AZURE" && path.Length >= 5)
            {
                ApplyAzureOverride(entry.Azure, path[3].ToUpperInvariant(), path, value);
            }
        }
    }

    private static void ApplyAzureOverride(AzureCredentials azure, string serviceSegment, string[] path, string? value)
    {
        // path = [CREDENTIALS, idx, AZURE, <service>, <field>, ...]
        var fieldSegment = path[4].ToUpperInvariant();
        switch (serviceSegment)
        {
            case "BLOB":
                azure.Blob ??= new BlobCredentials();
                if (fieldSegment == "ACCOUNTNAME") azure.Blob.AccountName = value ?? string.Empty;
                else if (fieldSegment == "ACCOUNTKEY") azure.Blob.AccountKey = value ?? string.Empty;
                return;
            case "SERVICEBUS":
                azure.ServiceBus ??= new ServiceBusCredentials();
                if (fieldSegment == "NAMESPACE") azure.ServiceBus.Namespace = value ?? string.Empty;
                else if (fieldSegment == "SASKEYNAME") azure.ServiceBus.SasKeyName = value ?? string.Empty;
                else if (fieldSegment == "SASKEY") azure.ServiceBus.SasKey = value ?? string.Empty;
                else if (fieldSegment == "TRANSPORT")
                {
                    if (TryParseTransport(value, out var t)) azure.ServiceBus.Transport = t;
                }
                else if (fieldSegment == "QUEUES" && path.Length >= 7)
                {
                    // AZURE/SERVICEBUS/QUEUES/<queueName-segments…>/<field>
                    // SQS queue names may contain underscores (incl. consecutive `__`),
                    // so the queue name spans every segment between QUEUES and the
                    // trailing field. Re-join them with the `__` separator so a
                    // user with a `orders__dlq` queue can still address it via
                    // `…__QUEUES__orders__dlq__TRANSPORT=amqp`.
                    var queueName = string.Join("__", path, 5, path.Length - 6);
                    var queueField = path[path.Length - 1].ToUpperInvariant();
                    azure.ServiceBus.Queues ??= new Dictionary<string, SqsQueueSettings>(StringComparer.OrdinalIgnoreCase);
                    if (!azure.ServiceBus.Queues.TryGetValue(queueName, out var settings) || settings is null)
                    {
                        settings = new SqsQueueSettings();
                        azure.ServiceBus.Queues[queueName] = settings;
                    }
                    if (queueField == "TRANSPORT" && TryParseTransport(value, out var qt))
                    {
                        settings.Transport = qt;
                    }
                }
                return;
            case "SERVICEBUSTOPICS":
                azure.ServiceBusTopics ??= new ServiceBusTopicsCredentials();
                if (fieldSegment == "NAMESPACE") azure.ServiceBusTopics.Namespace = value ?? string.Empty;
                else if (fieldSegment == "ENDPOINT") azure.ServiceBusTopics.Endpoint = value;
                else if (fieldSegment == "MANAGEMENTENDPOINT") azure.ServiceBusTopics.ManagementEndpoint = value;
                else if (fieldSegment == "SASKEYNAME") azure.ServiceBusTopics.SasKeyName = value ?? string.Empty;
                else if (fieldSegment == "SASKEY") azure.ServiceBusTopics.SasKey = value ?? string.Empty;
                else if (fieldSegment == "TENANTID") azure.ServiceBusTopics.TenantId = value;
                else if (fieldSegment == "CLIENTID") azure.ServiceBusTopics.ClientId = value;
                else if (fieldSegment == "CLIENTSECRET") azure.ServiceBusTopics.ClientSecret = value;
                return;
            case "COSMOS":
                azure.Cosmos ??= new CosmosCredentials();
                if (fieldSegment == "ENDPOINT") azure.Cosmos.Endpoint = value ?? string.Empty;
                else if (fieldSegment == "PRIMARYKEY") azure.Cosmos.PrimaryKey = value ?? string.Empty;
                else if (fieldSegment == "DATABASENAME") azure.Cosmos.DatabaseName = value ?? string.Empty;
                else if (fieldSegment == "TENANTID") azure.Cosmos.TenantId = value;
                else if (fieldSegment == "CLIENTID") azure.Cosmos.ClientId = value;
                else if (fieldSegment == "CLIENTSECRET") azure.Cosmos.ClientSecret = value;
                else if (fieldSegment == "PREFERREDREGIONS"
                    && path.Length == 6
                    && int.TryParse(path[5], out var regionIndex))
                {
                    azure.Cosmos.PreferredRegions ??= new List<string>();
                    while (azure.Cosmos.PreferredRegions.Count <= regionIndex)
                    {
                        azure.Cosmos.PreferredRegions.Add(string.Empty);
                    }
                    azure.Cosmos.PreferredRegions[regionIndex] = value ?? string.Empty;
                }
                return;
        }
    }

    private static bool TryParseTransport(string? value, out SqsTransport transport)
    {
        if (!string.IsNullOrWhiteSpace(value)
            && Enum.TryParse<SqsTransport>(value, ignoreCase: true, out transport)
            && Enum.IsDefined(typeof(SqsTransport), transport))
        {
            return true;
        }
        transport = default;
        return false;
    }

    private static bool TryParseSprocMode(string? value, out StoredProcedureMode mode)
    {
        if (!string.IsNullOrWhiteSpace(value)
            && Enum.TryParse<StoredProcedureMode>(value, ignoreCase: true, out mode)
            && Enum.IsDefined(typeof(StoredProcedureMode), mode))
        {
            return true;
        }
        mode = default;
        return false;
    }

    private static bool TryParseConsistencyCheckMode(string? value, out ConsistencyCheckMode mode)
    {
        if (!string.IsNullOrWhiteSpace(value)
            && Enum.TryParse<ConsistencyCheckMode>(value, ignoreCase: true, out mode)
            && Enum.IsDefined(typeof(ConsistencyCheckMode), mode))
        {
            return true;
        }
        mode = default;
        return false;
    }
}
