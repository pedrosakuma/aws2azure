using Aws2Azure.Amqp.ServiceBus;
using Aws2Azure.Core.Configuration;

namespace Aws2Azure.Modules.Kinesis.EventHubsAmqp;

internal static class EventHubsAmqpEndpointResolver
{
    public static ServiceBusAmqpEndpoint Resolve(EventHubsCredentials credentials, string namespaceFqdn)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceFqdn);

        if (!string.IsNullOrWhiteSpace(credentials.Endpoint)
            && Uri.TryCreate(credentials.Endpoint, UriKind.Absolute, out var endpointUri))
        {
            var logicalNamespace = namespaceFqdn.Trim().ToLowerInvariant();
            if (string.Equals(endpointUri.Scheme, "amqp", StringComparison.OrdinalIgnoreCase)
                || string.Equals(endpointUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            {
                return ServiceBusAmqpEndpoint.Plain(
                    endpointUri.Host,
                    endpointUri.IsDefaultPort ? ServiceBusEndpoint.AmqpPort : endpointUri.Port,
                    logicalNamespace);
            }

            return ServiceBusAmqpEndpoint.Tls(
                endpointUri.Host,
                endpointUri.IsDefaultPort ? ServiceBusEndpoint.AmqpsPort : endpointUri.Port,
                logicalNamespace);
        }

        if (Uri.TryCreate("amqps://" + namespaceFqdn.Trim(), UriKind.Absolute, out var namespaceUri))
        {
            var port = namespaceUri.Port > 0 ? namespaceUri.Port : ServiceBusEndpoint.AmqpsPort;
            return ServiceBusAmqpEndpoint.Tls(
                namespaceUri.Host,
                port,
                namespaceFqdn.Trim().ToLowerInvariant());
        }

        return ServiceBusAmqpEndpoint.Tls(namespaceFqdn.Trim().ToLowerInvariant());
    }

    public static string BuildAudience(string namespaceFqdn, string entityPath)
        => "amqps://" + namespaceFqdn.Trim().TrimEnd('/') + "/" + entityPath.Trim().TrimStart('/');
}
