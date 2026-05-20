using System.Threading;
using System.Threading.Tasks;
using Aws2Azure.Amqp.ServiceBus;

namespace Aws2Azure.Modules.Sqs.Internal;

/// <summary>
/// Thin abstraction over the AMQP connection pool the SQS handlers
/// reach for when a queue's transport is configured as
/// <see cref="Aws2Azure.Core.Configuration.SqsTransport.Amqp"/>.
///
/// <para>Decouples the handlers from the concrete
/// <see cref="ServiceBusAmqpPool"/> so unit tests can substitute an
/// in-memory implementation that returns a receiver wired to the
/// pipe-pair broker simulator. The production
/// <see cref="ServiceBusAmqpReceiverProvider"/> wraps the pool with
/// the (namespace, sasKeyName, sasKey) tuple from the resolved
/// <see cref="Aws2Azure.Core.Configuration.ServiceBusCredentials"/>.</para>
/// </summary>
internal interface IAmqpReceiverProvider
{
    /// <summary>
    /// Returns the shared receiver for <paramref name="queueName"/>,
    /// opening the AMQP connection / link on first request. Subsequent
    /// callers see the cached receiver and its in-flight delivery
    /// cache.
    /// </summary>
    Task<ServiceBusReceiver> GetReceiverAsync(string queueName, CancellationToken cancellationToken);

    /// <summary>
    /// Returns the shared session-bound receiver for the
    /// (<paramref name="queueName"/>, <paramref name="sessionId"/>)
    /// pair, opening the AMQP connection / link on first request and
    /// caching the link for subsequent calls under the same session
    /// id. Used by the SQS FIFO handlers when settling messages back
    /// to the specific session that issued them.
    /// </summary>
    Task<ServiceBusReceiver> GetSessionReceiverAsync(
        string queueName,
        string sessionId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Evicts the cached session receiver for
    /// (<paramref name="queueName"/>, <paramref name="sessionId"/>)
    /// after a link-level failure. The connection (and other session /
    /// non-session receivers) stays warm.
    /// </summary>
    Task InvalidateSessionReceiverAsync(string queueName, string sessionId);

    /// <summary>
    /// Returns the shared <c>$management</c> request-response client for
    /// <paramref name="queueName"/>'s audience. Opens on first request
    /// and caches per (connection, queue). Used by
    /// <c>ChangeMessageVisibility</c> to drive
    /// <c>com.microsoft:renew-lock</c>.
    /// </summary>
    Task<ServiceBusManagementClient> GetManagementClientAsync(string queueName, CancellationToken cancellationToken);

    /// <summary>
    /// Evicts the receiver for <paramref name="queueName"/> after a
    /// link- or connection-level failure. When
    /// <paramref name="closeConnection"/> is true the whole connection
    /// is torn down (next call rebuilds from scratch); otherwise only
    /// the receiver link is detached and the connection stays warm.
    /// </summary>
    Task InvalidateAsync(string queueName, bool closeConnection);

    /// <summary>
    /// Evicts the management client for <paramref name="queueName"/>'s
    /// audience after a link-level failure on the management link. The
    /// connection (and any cached receiver) stays warm.
    /// </summary>
    Task InvalidateManagementClientAsync(string queueName);
}
