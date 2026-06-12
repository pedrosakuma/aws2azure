using Aws2Azure.Core.Configuration;

namespace Aws2Azure.Modules.Sns.EventGrid;

internal sealed record EventGridPublishDestination(
    string Endpoint,
    string? AccessKey,
    AzureAuthMode AuthMode,
    string? TenantId,
    string? ClientId,
    string? ClientSecret)
{
    public EventGridPublishDestination(
        string endpoint,
        string? accessKey,
        string? tenantId,
        string? clientId,
        string? clientSecret)
        : this(endpoint, accessKey, AzureAuthMode.ClientSecret, tenantId, clientId, clientSecret)
    {
    }
}
