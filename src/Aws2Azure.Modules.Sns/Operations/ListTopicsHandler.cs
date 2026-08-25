using Aws2Azure.Core.Configuration;
using Aws2Azure.Modules.Sns.Management;
using Aws2Azure.Modules.Sns.WireProtocol;
using Aws2Azure.Modules.Sns.Xml;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.Sns.Operations;

internal static class ListTopicsHandler
{
    public static async Task HandleAsync(
        HttpContext context,
        SnsParseResult parseResult,
        ServiceBusTopicsCredentials credentials,
        IServiceBusTopicsManagementClient managementClient,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(parseResult);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(managementClient);

        var skip = 0;
        if (parseResult.Parameters.TryGetValue("NextToken", out var nextToken)
            && !string.IsNullOrWhiteSpace(nextToken))
        {
            if (!SnsTopicSupport.TryDecodeNextToken(nextToken, out skip))
            {
                await SnsTopicSupport.WriteInvalidParameterAsync(context, "Parameter 'NextToken' was not valid.")
                    .ConfigureAwait(false);
                return;
            }
        }

        ServiceBusTopicPage page;
        try
        {
            page = await managementClient.ListTopicsAsync(
                    credentials,
                    SnsTopicSupport.ResolveNamespaceFqdn(credentials),
                    skip,
                    SnsTopicSupport.ListTopicsPageSize,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ServiceBusTopicsManagementException ex)
        {
            await SnsTopicSupport.WriteManagementErrorAsync(context, ex).ConfigureAwait(false);
            return;
        }

        var topicArns = new List<string>(page.TopicNames.Count);
        var region = SnsTopicSupport.ResolveRegion(context);
        for (var i = 0; i < page.TopicNames.Count; i++)
        {
            var serviceBusTopicName = page.TopicNames[i];
            string? snsTopicName;
            try
            {
                snsTopicName = await SnsTopicOwnershipSupport.ResolveListedSnsTopicNameAsync(
                        credentials,
                        managementClient,
                        serviceBusTopicName,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (ServiceBusTopicsManagementException ex)
            {
                await SnsTopicSupport.WriteManagementErrorAsync(context, ex).ConfigureAwait(false);
                return;
            }

            if (!string.IsNullOrWhiteSpace(snsTopicName))
            {
                topicArns.Add(SnsTopicSupport.BuildTopicArn(region, snsTopicName));
            }
        }

        var responseNextToken = page.TopicNames.Count == SnsTopicSupport.ListTopicsPageSize
            ? SnsTopicSupport.EncodeNextToken(skip + SnsTopicSupport.ListTopicsPageSize)
            : null;

        await SnsResponseWriter.WriteListTopicsResponseAsync(context, topicArns, responseNextToken).ConfigureAwait(false);
    }
}
