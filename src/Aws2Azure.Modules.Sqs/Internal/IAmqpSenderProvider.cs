using System.Threading;
using System.Threading.Tasks;
using Aws2Azure.Amqp.ServiceBus;

namespace Aws2Azure.Modules.Sqs.Internal;

/// <summary>
/// Provider abstraction for AMQP sender links, symmetric to
/// <see cref="IAmqpReceiverProvider"/>. Wraps the pool keyed by a
/// resolved <see cref="Aws2Azure.Core.Configuration.ServiceBusCredentials"/>
/// tuple so per-request handlers only need the queue name.
/// </summary>
internal interface IAmqpSenderProvider
{
    /// <summary>
    /// Returns the shared sender for <paramref name="queueName"/>,
    /// opening the AMQP connection / link on first request. Subsequent
    /// callers see the cached sender.
    /// </summary>
    Task<ServiceBusAmqpSender> GetSenderAsync(string queueName, CancellationToken cancellationToken);

    /// <summary>
    /// Evicts the cached sender for <paramref name="queueName"/> after
    /// a link- or connection-level failure. When
    /// <paramref name="closeConnection"/> is true the whole connection
    /// is torn down (next call rebuilds from scratch); otherwise only
    /// the sender link is detached and the connection stays warm.
    /// </summary>
    Task InvalidateSenderAsync(string queueName, bool closeConnection);
}
