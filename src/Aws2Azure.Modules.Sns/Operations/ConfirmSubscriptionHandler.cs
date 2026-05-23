using Aws2Azure.Modules.Sns.WireProtocol;
using Aws2Azure.Modules.Sns.Xml;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.Sns.Operations;

internal static class ConfirmSubscriptionHandler
{
    public static async Task HandleAsync(HttpContext context, SnsParseResult parseResult)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(parseResult);

        if (!SnsTopicSupport.TryGetRequiredParameter(parseResult.Parameters, "TopicArn", out var topicArn, out var error)
            || !SnsPublishSupport.TryParsePublishTopicArn(topicArn, out var topicName, out error)
            || !SnsTopicSupport.TryGetRequiredParameter(parseResult.Parameters, "Token", out var token, out error))
        {
            await SnsTopicSupport.WriteInvalidParameterAsync(context, error!).ConfigureAwait(false);
            return;
        }

        var subscriptionArn = SnsSubscriptionSupport.ResolveConfirmSubscriptionArn(context, topicName, token);
        await SnsResponseWriter.WriteConfirmSubscriptionResponseAsync(context, subscriptionArn).ConfigureAwait(false);
    }
}
