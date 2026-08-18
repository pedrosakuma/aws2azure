using System.Text.Json.Serialization;

namespace Aws2Azure.Core.Configuration;

[JsonConverter(typeof(CaseInsensitiveStringEnumConverter<AzureAuthMode>))]
public enum AzureAuthMode
{
    ClientSecret = 0,
    ManagedIdentity = 1,
    WorkloadIdentity = 2,
}
