using Aws2Azure.Core.Configuration;
using Aws2Azure.Modules.Sns.Amqp;
using Aws2Azure.Modules.Sns.Errors;
using Aws2Azure.Modules.Sns.WireProtocol;
using Aws2Azure.Modules.Sns.Xml;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.Sns.Operations;

internal static class PublishHandler
{
    public static async Task HandleAsync(
        HttpContext context,
        SnsParseResult parseResult,
        ServiceBusTopicsCredentials credentials,
        ISnsAmqpSender amqpSender,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(parseResult);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(amqpSender);

        if (!SnsPublishSupport.TryParsePublishRequest(parseResult.Parameters, out var request, out var error))
        {
            await SnsTopicSupport.WriteInvalidParameterAsync(context, error!).ConfigureAwait(false);
            return;
        }

        var messageId = Guid.NewGuid();
        try
        {
            await amqpSender.SendAsync(
                credentials,
                SnsTopicSupport.ResolveNamespaceFqdn(credentials),
                request.TopicName,
                SnsPublishSupport.CreateAmqpMessage(request, messageId),
                cancellationToken).ConfigureAwait(false);
        }
        catch (SnsAmqpException exception)
        {
            await SnsPublishErrorMapper.WriteSendErrorAsync(context, exception).ConfigureAwait(false);
            return;
        }

        await SnsResponseWriter.WritePublishResponseAsync(context, messageId.ToString()).ConfigureAwait(false);
    }
}
