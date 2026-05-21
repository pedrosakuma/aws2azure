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
                if (!Uri.TryCreate(blob.ServiceEndpoint, UriKind.Absolute, out var endpointUri))
                {
                    errors.Add($"{prefix}.blob.serviceEndpoint: must be an absolute URI when set.");
                }
                else if (endpointUri.Scheme != Uri.UriSchemeHttp && endpointUri.Scheme != Uri.UriSchemeHttps)
                {
                    errors.Add($"{prefix}.blob.serviceEndpoint: must use http or https scheme.");
                }
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

            var hasKey = !string.IsNullOrWhiteSpace(cosmos.PrimaryKey);
            var hasAad = !string.IsNullOrWhiteSpace(cosmos.TenantId)
                && !string.IsNullOrWhiteSpace(cosmos.ClientId)
                && !string.IsNullOrWhiteSpace(cosmos.ClientSecret);
            if (!hasKey && !hasAad)
            {
                errors.Add($"{prefix}.cosmos: either primaryKey OR (tenantId+clientId+clientSecret) is required.");
            }
            if (hasKey && hasAad)
            {
                errors.Add($"{prefix}.cosmos: primaryKey and AAD fields are mutually exclusive — supply one shape.");
            }
        }
    }
}
