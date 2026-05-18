using System.Text.Json.Serialization;

namespace Aws2Azure.Core.Configuration;

/// <summary>
/// Root configuration POCO. Mirrors <c>appsettings.json</c> and the
/// <c>AWS2AZURE__*</c> env-var hierarchy.
/// </summary>
public sealed class ProxyConfig
{
    public Dictionary<string, ServiceToggle> Services { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<CredentialEntry> Credentials { get; set; } = new();
}

public sealed class ServiceToggle
{
    public bool Enabled { get; set; }
}

public sealed class CredentialEntry
{
    public string AwsAccessKeyId { get; set; } = string.Empty;
    public string AwsSecretAccessKey { get; set; } = string.Empty;
    public AzureCredentials Azure { get; set; } = new();
}

public sealed class AzureCredentials
{
    public BlobCredentials? Blob { get; set; }
    public ServiceBusCredentials? ServiceBus { get; set; }
    public CosmosCredentials? Cosmos { get; set; }
}

public sealed class BlobCredentials
{
    public string AccountName { get; set; } = string.Empty;
    public string AccountKey { get; set; } = string.Empty;

    /// <summary>
    /// Optional absolute Blob service endpoint. When empty, the canonical
    /// formula <c>https://{AccountName}.blob.core.windows.net</c> is used.
    /// Override for Azurite (<c>http://127.0.0.1:10000/devstoreaccount1</c>)
    /// or sovereign clouds (e.g. <c>blob.core.usgovcloudapi.net</c>).
    /// </summary>
    public string? ServiceEndpoint { get; set; }
}

public sealed class ServiceBusCredentials
{
    public string Namespace { get; set; } = string.Empty;
    public string SasKeyName { get; set; } = string.Empty;
    public string SasKey { get; set; } = string.Empty;
}

public sealed class CosmosCredentials
{
    public string Endpoint { get; set; } = string.Empty;
    public string PrimaryKey { get; set; } = string.Empty;
}

/// <summary>
/// System.Text.Json source-generated context for <see cref="ProxyConfig"/>.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(ProxyConfig))]
public sealed partial class ProxyConfigJsonContext : JsonSerializerContext
{
}
