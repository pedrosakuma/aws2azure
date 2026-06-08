namespace Aws2Azure.Core.Configuration;

/// <summary>
/// Logical Azure service backends that map 1:1 to a credential set in
/// <see cref="AzureCredentials"/>.
/// </summary>
public enum AzureService
{
    Blob,
    ServiceBus,
    ServiceBusTopics,
    Cosmos,
    EventHubs,
    EventGrid,
    KeyVault,
}
