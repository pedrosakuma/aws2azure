using Aws2Azure.Core.Configuration;

namespace Aws2Azure.Modules.Kinesis.EventHubsAmqp;

internal static class EventHubsCredentialMarker
{
    public static string Build(EventHubsCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        if (!string.IsNullOrWhiteSpace(credentials.SasKeyName))
        {
            return "sas|" + credentials.SasKeyName.Trim();
        }

        return credentials.AuthMode switch
        {
            AzureAuthMode.ManagedIdentity => "managedIdentity|" + (credentials.ClientId ?? "system"),
            AzureAuthMode.WorkloadIdentity => "workloadIdentity|"
                + Environment.GetEnvironmentVariable("AZURE_TENANT_ID")
                + "|"
                + Environment.GetEnvironmentVariable("AZURE_CLIENT_ID"),
            _ => "clientSecret|" + credentials.TenantId + "|" + credentials.ClientId,
        };
    }
}
