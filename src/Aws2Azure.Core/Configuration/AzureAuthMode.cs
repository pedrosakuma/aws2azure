using System.Text.Json.Serialization;

namespace Aws2Azure.Core.Configuration;

[JsonConverter(typeof(JsonStringEnumConverter<AzureAuthMode>))]
public enum AzureAuthMode
{
    ClientSecret = 0,
    ManagedIdentity = 1,
    WorkloadIdentity = 2,
}
