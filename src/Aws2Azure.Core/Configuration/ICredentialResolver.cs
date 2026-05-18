namespace Aws2Azure.Core.Configuration;

/// <summary>
/// Maps AWS access keys to (a) their AWS secret (used by SigV4 validation in
/// issue #2) and (b) the Azure credentials to use for downstream calls
/// (used by the Azure REST client in issue #5).
/// </summary>
public interface ICredentialResolver
{
    bool TryGetAwsSecret(string awsAccessKeyId, out string awsSecretAccessKey);

    /// <summary>
    /// Returns the Azure credential object for the given access key, or
    /// <c>null</c> if no credentials are configured for that service.
    /// The return type depends on <paramref name="service"/>:
    /// <see cref="BlobCredentials"/>, <see cref="ServiceBusCredentials"/>,
    /// or <see cref="CosmosCredentials"/>.
    /// </summary>
    object? GetAzureCredentialsFor(string awsAccessKeyId, AzureService service);
}

/// <summary>
/// Immutable resolver built once from a validated <see cref="ProxyConfig"/>.
/// </summary>
public sealed class StaticCredentialResolver : ICredentialResolver
{
    private readonly Dictionary<string, CredentialEntry> _byAccessKey;

    public StaticCredentialResolver(ProxyConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _byAccessKey = new Dictionary<string, CredentialEntry>(StringComparer.Ordinal);
        foreach (var entry in config.Credentials)
        {
            _byAccessKey[entry.AwsAccessKeyId] = entry;
        }
    }

    public bool TryGetAwsSecret(string awsAccessKeyId, out string awsSecretAccessKey)
    {
        if (_byAccessKey.TryGetValue(awsAccessKeyId, out var entry))
        {
            awsSecretAccessKey = entry.AwsSecretAccessKey;
            return true;
        }

        awsSecretAccessKey = string.Empty;
        return false;
    }

    public object? GetAzureCredentialsFor(string awsAccessKeyId, AzureService service)
    {
        if (!_byAccessKey.TryGetValue(awsAccessKeyId, out var entry))
        {
            return null;
        }

        return service switch
        {
            AzureService.Blob       => entry.Azure.Blob,
            AzureService.ServiceBus => entry.Azure.ServiceBus,
            AzureService.Cosmos     => entry.Azure.Cosmos,
            _ => null,
        };
    }
}
