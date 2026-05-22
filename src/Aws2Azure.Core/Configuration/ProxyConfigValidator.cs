using System.Text;

namespace Aws2Azure.Core.Configuration;

/// <summary>
/// Validates a <see cref="ProxyConfig"/> before the host starts. Accumulates
/// every issue and throws <see cref="ProxyConfigException"/> with all of
/// them so misconfigurations don't have to be fixed one at a time.
/// </summary>
public static class ProxyConfigValidator
{
    public static void Validate(ProxyConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var errors = new List<string>();

        if (config.Credentials.Count == 0)
        {
            errors.Add("credentials: at least one entry is required.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < config.Credentials.Count; i++)
        {
            var entry = config.Credentials[i];
            var prefix = $"credentials[{i}]";

            if (string.IsNullOrWhiteSpace(entry.AwsAccessKeyId))
            {
                errors.Add($"{prefix}.awsAccessKeyId: required.");
            }
            else if (!seen.Add(entry.AwsAccessKeyId))
            {
                errors.Add($"{prefix}.awsAccessKeyId: duplicate value '{entry.AwsAccessKeyId}'.");
            }

            if (string.IsNullOrWhiteSpace(entry.AwsSecretAccessKey))
            {
                errors.Add($"{prefix}.awsSecretAccessKey: required.");
            }

            ValidateAzure(entry.Azure, prefix + ".azure", errors);
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

    private static void ValidateAzure(AzureCredentials azure, string prefix, List<string> errors)
    {
        if (azure.Blob is { } blob)
        {
            if (string.IsNullOrWhiteSpace(blob.AccountName))
            {
                errors.Add($"{prefix}.blob.accountName: required.");
            }
            if (string.IsNullOrWhiteSpace(blob.AccountKey))
            {
                errors.Add($"{prefix}.blob.accountKey: required.");
            }
            if (!string.IsNullOrWhiteSpace(blob.ServiceEndpoint))
            {
                ValidateAbsoluteUri(blob.ServiceEndpoint, $"{prefix}.blob.serviceEndpoint", errors, Uri.UriSchemeHttp, Uri.UriSchemeHttps);
            }
        }

        if (azure.ServiceBus is { } sb)
        {
            if (string.IsNullOrWhiteSpace(sb.Namespace))
            {
                errors.Add($"{prefix}.serviceBus.namespace: required.");
            }
            if (string.IsNullOrWhiteSpace(sb.SasKeyName))
            {
                errors.Add($"{prefix}.serviceBus.sasKeyName: required.");
            }
            if (string.IsNullOrWhiteSpace(sb.SasKey))
            {
                errors.Add($"{prefix}.serviceBus.sasKey: required.");
            }
            if (!Enum.IsDefined(typeof(SqsTransport), sb.Transport))
            {
                errors.Add($"{prefix}.serviceBus.transport: unknown value '{(int)sb.Transport}'.");
            }
            if (sb.Queues is { } queues)
            {
                foreach (var (name, settings) in queues)
                {
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        errors.Add($"{prefix}.serviceBus.queues: queue name must be non-empty.");
                        continue;
                    }
                    if (settings is null)
                    {
                        errors.Add($"{prefix}.serviceBus.queues.{name}: entry is null.");
                        continue;
                    }
                    if (settings.Transport is { } t && !Enum.IsDefined(typeof(SqsTransport), t))
                    {
                        errors.Add($"{prefix}.serviceBus.queues.{name}.transport: unknown value '{(int)t}'.");
                    }
                }
            }
        }

        if (azure.ServiceBusTopics is { } serviceBusTopics)
        {
            ValidateServiceBusTopics(serviceBusTopics, prefix + ".serviceBusTopics", errors);
        }

        if (azure.Cosmos is { } cosmos)
        {
            if (string.IsNullOrWhiteSpace(cosmos.Endpoint))
            {
                errors.Add($"{prefix}.cosmos.endpoint: required.");
            }
            if (string.IsNullOrWhiteSpace(cosmos.DatabaseName))
            {
                errors.Add($"{prefix}.cosmos.databaseName: required.");
            }

            ValidateDualAuth(
                prefix + ".cosmos",
                errors,
                sasLabel: "primaryKey",
                hasSasPart1: !string.IsNullOrWhiteSpace(cosmos.PrimaryKey),
                hasSasPart2: !string.IsNullOrWhiteSpace(cosmos.PrimaryKey),
                sasRequirementMessage: "either primaryKey OR (tenantId+clientId+clientSecret) is required.",
                sasPairRequirementMessage: null,
                hasTenant: !string.IsNullOrWhiteSpace(cosmos.TenantId),
                hasClientId: !string.IsNullOrWhiteSpace(cosmos.ClientId),
                hasClientSecret: !string.IsNullOrWhiteSpace(cosmos.ClientSecret));
        }

        if (azure.EventHubs is { } eh)
        {
            if (string.IsNullOrWhiteSpace(eh.Namespace))
            {
                errors.Add($"{prefix}.eventHubs.namespace: required.");
            }

            if (!string.IsNullOrEmpty(eh.Endpoint))
            {
                ValidateAbsoluteUri(eh.Endpoint, $"{prefix}.eventHubs.endpoint", errors,
                    Uri.UriSchemeHttp, Uri.UriSchemeHttps, "amqp", "amqps");
            }

            ValidateDualAuth(
                prefix + ".eventHubs",
                errors,
                sasLabel: "SAS",
                hasSasPart1: !string.IsNullOrWhiteSpace(eh.SasKeyName),
                hasSasPart2: !string.IsNullOrWhiteSpace(eh.SasKey),
                sasRequirementMessage: "either (sasKeyName+sasKey) OR (tenantId+clientId+clientSecret) is required.",
                sasPairRequirementMessage: "SAS auth requires both sasKeyName and sasKey.",
                hasTenant: !string.IsNullOrWhiteSpace(eh.TenantId),
                hasClientId: !string.IsNullOrWhiteSpace(eh.ClientId),
                hasClientSecret: !string.IsNullOrWhiteSpace(eh.ClientSecret));

            if (eh.Streams is { } streams)
            {
                foreach (var (name, settings) in streams)
                {
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        errors.Add($"{prefix}.eventHubs.streams: stream name must be non-empty.");
                        continue;
                    }
                    if (settings is null)
                    {
                        errors.Add($"{prefix}.eventHubs.streams.{name}: entry is null.");
                        continue;
                    }

                    if (settings.PartitionCount is <= 0)
                    {
                        errors.Add($"{prefix}.eventHubs.streams.{name}.partitionCount: must be greater than zero when set.");
                    }
                }
            }
        }

        if (azure.EventGrid is { } eventGrid)
        {
            ValidateEventGrid(eventGrid, prefix + ".eventGrid", errors);
        }
    }

    private static void ValidateServiceBusTopics(ServiceBusTopicsCredentials credentials, string prefix, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(credentials.Namespace))
        {
            errors.Add($"{prefix}.namespace: required.");
        }

        if (!string.IsNullOrWhiteSpace(credentials.Endpoint))
        {
            ValidateAbsoluteUri(credentials.Endpoint, $"{prefix}.endpoint", errors,
                Uri.UriSchemeHttp, Uri.UriSchemeHttps, "amqp", "amqps");
        }

        ValidateDualAuth(
            prefix,
            errors,
            sasLabel: "SAS",
            hasSasPart1: !string.IsNullOrWhiteSpace(credentials.SasKeyName),
            hasSasPart2: !string.IsNullOrWhiteSpace(credentials.SasKey),
            sasRequirementMessage: "either (sasKeyName+sasKey) OR (tenantId+clientId+clientSecret) is required.",
            sasPairRequirementMessage: "SAS auth requires both sasKeyName and sasKey.",
            hasTenant: !string.IsNullOrWhiteSpace(credentials.TenantId),
            hasClientId: !string.IsNullOrWhiteSpace(credentials.ClientId),
            hasClientSecret: !string.IsNullOrWhiteSpace(credentials.ClientSecret));

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
            }
        }
    }

    private static void ValidateEventGrid(EventGridCredentials credentials, string prefix, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(credentials.Endpoint))
        {
            errors.Add($"{prefix}.endpoint: required.");
        }
        else
        {
            ValidateAbsoluteUri(credentials.Endpoint, $"{prefix}.endpoint", errors, Uri.UriSchemeHttps);
        }

        ValidateDualAuth(
            prefix,
            errors,
            sasLabel: "AccessKey",
            hasSasPart1: !string.IsNullOrWhiteSpace(credentials.AccessKey),
            hasSasPart2: !string.IsNullOrWhiteSpace(credentials.AccessKey),
            sasRequirementMessage: "either accessKey OR (tenantId+clientId+clientSecret) is required.",
            sasPairRequirementMessage: null,
            hasTenant: !string.IsNullOrWhiteSpace(credentials.TenantId),
            hasClientId: !string.IsNullOrWhiteSpace(credentials.ClientId),
            hasClientSecret: !string.IsNullOrWhiteSpace(credentials.ClientSecret));
    }

    private static void ValidateDualAuth(
        string prefix,
        List<string> errors,
        string sasLabel,
        bool hasSasPart1,
        bool hasSasPart2,
        string sasRequirementMessage,
        string? sasPairRequirementMessage,
        bool hasTenant,
        bool hasClientId,
        bool hasClientSecret)
    {
        var hasAnySas = hasSasPart1 || hasSasPart2;
        var hasCompleteSas = hasSasPart1 && hasSasPart2;
        var hasAnyAad = hasTenant || hasClientId || hasClientSecret;
        var hasCompleteAad = hasTenant && hasClientId && hasClientSecret;

        if (!hasCompleteSas && !hasCompleteAad)
        {
            if (hasAnySas && !hasCompleteSas && sasPairRequirementMessage is not null)
            {
                errors.Add($"{prefix}: {sasPairRequirementMessage}");
            }
            else if (hasAnyAad && !hasCompleteAad)
            {
                errors.Add($"{prefix}: AAD requires tenantId, clientId, and clientSecret together.");
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
