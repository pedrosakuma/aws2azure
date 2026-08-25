using Aws2Azure.Core.Configuration;
using Aws2Azure.Modules.Sns.Management;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.Sns.Operations;

internal static class SnsTopicOwnershipSupport
{
    public static async Task<bool> EnsureTopicOwnershipAsync(
        HttpContext context,
        ServiceBusTopicsCredentials credentials,
        IServiceBusTopicsManagementClient managementClient,
        string topicName,
        string serviceBusTopicName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(managementClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(topicName);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceBusTopicName);

        if (!SnsTopicRouting.HasExactConfiguredServiceBusTopicAlias(credentials, topicName, out _))
        {
            return true;
        }

        ServiceBusTopicDescription? topic;
        try
        {
            topic = await managementClient.GetTopicAsync(
                    credentials,
                    SnsTopicSupport.ResolveNamespaceFqdn(credentials),
                    serviceBusTopicName,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ServiceBusTopicsManagementException ex)
        {
            await SnsTopicSupport.WriteManagementErrorAsync(context, ex).ConfigureAwait(false);
            return false;
        }

        if (topic is null)
        {
            return true;
        }

        var metadataTopicName = SnsTopicAttributeSupport.ParseMetadata(topic.UserMetadata).SnsTopicName;
        if (string.IsNullOrWhiteSpace(metadataTopicName)
            || string.Equals(metadataTopicName, topicName, StringComparison.Ordinal))
        {
            return true;
        }

        await SnsTopicSupport.WriteNotFoundAsync(
                context,
                $"Topic does not exist: {SnsTopicSupport.BuildTopicArn(context, topicName)}")
            .ConfigureAwait(false);
        return false;
    }

    public static async Task<string?> ResolveListedSnsTopicNameAsync(
        ServiceBusTopicsCredentials credentials,
        IServiceBusTopicsManagementClient managementClient,
        string serviceBusTopicName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(managementClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceBusTopicName);

        if (!SnsTopicRouting.TryResolveConfiguredSnsTopicName(credentials, serviceBusTopicName, out var configuredTopicName))
        {
            return serviceBusTopicName;
        }

        ServiceBusTopicDescription? topic = await managementClient.GetTopicAsync(
                credentials,
                SnsTopicSupport.ResolveNamespaceFqdn(credentials),
                serviceBusTopicName,
                cancellationToken)
            .ConfigureAwait(false);
        var metadataTopicName = SnsTopicAttributeSupport.ParseMetadata(topic?.UserMetadata).SnsTopicName;
        if (!string.IsNullOrWhiteSpace(metadataTopicName) && SnsTopicSupport.IsValidTopicName(metadataTopicName))
        {
            return string.Equals(metadataTopicName, configuredTopicName, StringComparison.Ordinal)
                ? metadataTopicName
                : null;
        }

        return configuredTopicName;
    }
}
