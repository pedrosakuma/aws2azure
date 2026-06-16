using System.Net;
using Aws2Azure.Amqp.Connection;
using Aws2Azure.Amqp.Framing;
using Aws2Azure.Amqp.Security;
using Aws2Azure.Amqp.ServiceBus;
using Aws2Azure.Core.Azure;

namespace Aws2Azure.Modules.Kinesis.EventHubsAmqp;

internal enum EventHubsAmqpOperation
{
    Send = 0,
    Receive,
}

internal static class EventHubsAmqpExceptionMapper
{
    public static bool TryWrap(
        Exception exception,
        EventHubsAmqpOperation operation,
        out EventHubsAmqpException wrapped)
    {
        switch (exception)
        {
            case EventHubsAmqpException alreadyWrapped:
                wrapped = alreadyWrapped;
                return true;
            case CbsAuthenticationException cbsAuthentication:
                wrapped = new EventHubsAmqpException(
                    "Event Hubs AMQP authorization failed.",
                    cbsAuthentication,
                    EventHubsAmqpFailureKind.Auth,
                    description: cbsAuthentication.StatusDescription);
                return true;
            case EntraIdTokenException tokenException:
                wrapped = new EventHubsAmqpException(
                    "Event Hubs AMQP authorization failed.",
                    tokenException,
                    MapTokenStatus(tokenException.BackendStatus));
                return true;
            case ServiceBusSendException sendException:
                wrapped = new EventHubsAmqpException(
                    "Event Hubs rejected the AMQP transfer.",
                    sendException,
                    MapOutcome(sendException.Outcome));
                return true;
            case AmqpLinkException linkException:
                wrapped = new EventHubsAmqpException(
                    linkException.Message,
                    linkException,
                    MapKind(linkException.Kind),
                    linkException.PeerCondition,
                    linkException.PeerDescription);
                return true;
            case AmqpConnectionException connectionException:
                wrapped = new EventHubsAmqpException(
                    connectionException.Message,
                    connectionException,
                    MapKind(connectionException.Kind));
                return true;
            case TimeoutException timeoutException:
                var operationName = operation == EventHubsAmqpOperation.Receive ? "receive" : "send";
                wrapped = new EventHubsAmqpException(
                    "Event Hubs AMQP " + operationName + " timed out.",
                    timeoutException,
                    EventHubsAmqpFailureKind.Transient,
                    AmqpErrorCondition.Timeout,
                    timeoutException.Message);
                return true;
            case ObjectDisposedException disposedException when operation == EventHubsAmqpOperation.Send:
                wrapped = new EventHubsAmqpException(
                    "Event Hubs AMQP sender link was closed during send.",
                    disposedException,
                    EventHubsAmqpFailureKind.Transient);
                return true;
            default:
                wrapped = default!;
                return false;
        }
    }

    private static EventHubsAmqpFailureKind MapKind(AmqpErrorKind kind) => kind switch
    {
        AmqpErrorKind.Transient => EventHubsAmqpFailureKind.Transient,
        AmqpErrorKind.Throttled => EventHubsAmqpFailureKind.Throttled,
        AmqpErrorKind.Auth => EventHubsAmqpFailureKind.Auth,
        AmqpErrorKind.ClientFatal => EventHubsAmqpFailureKind.ClientFatal,
        AmqpErrorKind.ServerFatal => EventHubsAmqpFailureKind.ServerFatal,
        AmqpErrorKind.Redirect => EventHubsAmqpFailureKind.Redirect,
        _ => EventHubsAmqpFailureKind.Unknown,
    };

    private static EventHubsAmqpFailureKind MapTokenStatus(HttpStatusCode backendStatus) => backendStatus switch
    {
        HttpStatusCode.TooManyRequests => EventHubsAmqpFailureKind.Throttled,
        HttpStatusCode.ServiceUnavailable => EventHubsAmqpFailureKind.Transient,
        _ => EventHubsAmqpFailureKind.Auth,
    };

    private static EventHubsAmqpFailureKind MapOutcome(AmqpDispositionOutcome outcome) => outcome switch
    {
        AmqpDispositionOutcome.Released => EventHubsAmqpFailureKind.Transient,
        AmqpDispositionOutcome.Modified => EventHubsAmqpFailureKind.Throttled,
        _ => EventHubsAmqpFailureKind.Unknown,
    };
}
