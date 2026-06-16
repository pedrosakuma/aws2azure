using Aws2Azure.Core.Configuration;
using Aws2Azure.Modules.Kinesis.EventHubsRest;

namespace Aws2Azure.TestSupport.Kinesis;

/// <summary>
/// Shared Kinesis management fake. The default description is intentionally
/// permissive (three partitions) so shard-aware tests can use the parameterless
/// constructor; tests that need exact metadata should pass a handler.
/// </summary>
public sealed class FakeManagementClient(
    Func<EventHubsCredentials, string, string, CancellationToken, ValueTask<EventHubDescription>>? handler = null)
    : IEventHubsManagementClient
{
    public List<KinesisManagementCall> Calls { get; } = [];

    public ValueTask<EventHubDescription> GetEventHubAsync(
        EventHubsCredentials credentials,
        string namespaceFqdn,
        string eventHubName,
        CancellationToken cancellationToken)
    {
        Calls.Add(new KinesisManagementCall(namespaceFqdn, eventHubName));
        return handler is null
            ? ValueTask.FromResult(DefaultDescription())
            : handler(credentials, namespaceFqdn, eventHubName, cancellationToken);
    }

    public static EventHubDescription DefaultDescription()
        => new(3, ["0", "1", "2"], 7, new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero));
}

/// <summary>
/// Shared Kinesis metadata-cache fake with the same default description as
/// <see cref="FakeManagementClient"/>.
/// </summary>
public sealed class FakeMetadataCache(
    Func<EventHubsCredentials, string, string, CancellationToken, ValueTask<EventHubDescription>>? handler = null)
    : IEventHubMetadataCache
{
    public List<KinesisManagementCall> Calls { get; } = [];

    public ValueTask<EventHubDescription> GetEventHubAsync(
        EventHubsCredentials credentials,
        string namespaceFqdn,
        string eventHubName,
        CancellationToken cancellationToken)
    {
        Calls.Add(new KinesisManagementCall(namespaceFqdn, eventHubName));
        return handler is null
            ? ValueTask.FromResult(FakeManagementClient.DefaultDescription())
            : handler(credentials, namespaceFqdn, eventHubName, cancellationToken);
    }
}

public sealed record KinesisManagementCall(string NamespaceFqdn, string EventHubName);
