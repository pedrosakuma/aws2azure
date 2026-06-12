using Aws2Azure.Core.Configuration;
using Aws2Azure.Modules.Kinesis.EventHubsRest;

namespace Aws2Azure.UnitTests.Kinesis;

[Collection("EnvironmentVariables")]
public sealed class EventHubMetadataCacheTests
{
    [Fact]
    public async Task GetEventHubAsync_caches_values_within_ttl()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2024, 6, 20, 8, 45, 0, TimeSpan.Zero));
        var callCount = 0;
        var cache = new EventHubMetadataCache(
            new FakeManagementClient((_, _, _, _) =>
            {
                callCount++;
                return ValueTask.FromResult(new EventHubDescription(4, ["0", "1", "2", "3"], 7, clock.GetUtcNow()));
            }),
            clock,
            TimeSpan.FromMinutes(5));

        var first = await cache.GetEventHubAsync(NewCredentials(), "myns.servicebus.windows.net", "orders", CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(4));
        var second = await cache.GetEventHubAsync(NewCredentials(), "myns.servicebus.windows.net", "orders", CancellationToken.None);

        Assert.Equal(1, callCount);
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task GetEventHubAsync_refreshes_after_ttl_expires()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2024, 6, 20, 8, 45, 0, TimeSpan.Zero));
        var callCount = 0;
        var cache = new EventHubMetadataCache(
            new FakeManagementClient((_, _, _, _) =>
            {
                callCount++;
                return ValueTask.FromResult(new EventHubDescription(callCount, ["0"], 7, clock.GetUtcNow()));
            }),
            clock,
            TimeSpan.FromMinutes(5));

        var first = await cache.GetEventHubAsync(NewCredentials(), "myns.servicebus.windows.net", "orders", CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(6));
        var second = await cache.GetEventHubAsync(NewCredentials(), "myns.servicebus.windows.net", "orders", CancellationToken.None);

        Assert.Equal(2, callCount);
        Assert.NotEqual(first.PartitionCount, second.PartitionCount);
    }

    [Fact]
    public async Task GetEventHubAsync_coalesces_concurrent_refreshes()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2024, 6, 20, 8, 45, 0, TimeSpan.Zero));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callCount = 0;
        var cache = new EventHubMetadataCache(
            new FakeManagementClient(async (_, _, _, cancellationToken) =>
            {
                Interlocked.Increment(ref callCount);
                entered.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
                return new EventHubDescription(4, ["0", "1", "2", "3"], 7, clock.GetUtcNow());
            }),
            clock,
            TimeSpan.FromMinutes(5));

        var taskA = cache.GetEventHubAsync(NewCredentials(), "myns.servicebus.windows.net", "orders", CancellationToken.None).AsTask();
        var taskB = cache.GetEventHubAsync(NewCredentials(), "myns.servicebus.windows.net", "orders", CancellationToken.None).AsTask();
        var taskC = cache.GetEventHubAsync(NewCredentials(), "myns.servicebus.windows.net", "orders", CancellationToken.None).AsTask();

        await entered.Task;
        Assert.Equal(1, Volatile.Read(ref callCount));

        release.TrySetResult();
        var results = await Task.WhenAll(taskA, taskB, taskC);

        Assert.Equal(1, callCount);
        Assert.All(results, result => Assert.Equal(4, result.PartitionCount));
    }

    [Fact]
    public async Task GetEventHubAsync_separates_cache_entries_by_token_auth_mode()
    {
        var oldTenant = Environment.GetEnvironmentVariable("AZURE_TENANT_ID");
        var oldClient = Environment.GetEnvironmentVariable("AZURE_CLIENT_ID");
        var oldTokenFile = Environment.GetEnvironmentVariable("AZURE_FEDERATED_TOKEN_FILE");
        try
        {
            Environment.SetEnvironmentVariable("AZURE_TENANT_ID", "tenant");
            Environment.SetEnvironmentVariable("AZURE_CLIENT_ID", "workload-client");
            Environment.SetEnvironmentVariable("AZURE_FEDERATED_TOKEN_FILE", "token-file");

            var clock = new ManualTimeProvider(new DateTimeOffset(2024, 6, 20, 8, 45, 0, TimeSpan.Zero));
            var callCount = 0;
            var cache = new EventHubMetadataCache(
                new FakeManagementClient((_, _, _, _) =>
                {
                    callCount++;
                    return ValueTask.FromResult(new EventHubDescription(callCount, [callCount.ToString()], 7, clock.GetUtcNow()));
                }),
                clock,
                TimeSpan.FromMinutes(5));

            var managedIdentity = new EventHubsCredentials
            {
                Namespace = "myns",
                AuthMode = AzureAuthMode.ManagedIdentity,
            };
            var workloadIdentity = new EventHubsCredentials
            {
                Namespace = "myns",
                AuthMode = AzureAuthMode.WorkloadIdentity,
            };

            var first = await cache.GetEventHubAsync(managedIdentity, "myns.servicebus.windows.net", "orders", CancellationToken.None);
            var second = await cache.GetEventHubAsync(workloadIdentity, "myns.servicebus.windows.net", "orders", CancellationToken.None);

            Assert.Equal(2, callCount);
            Assert.NotEqual(first.PartitionCount, second.PartitionCount);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AZURE_TENANT_ID", oldTenant);
            Environment.SetEnvironmentVariable("AZURE_CLIENT_ID", oldClient);
            Environment.SetEnvironmentVariable("AZURE_FEDERATED_TOKEN_FILE", oldTokenFile);
        }
    }

    private static EventHubsCredentials NewCredentials() => new()
    {
        Namespace = "myns",
        SasKeyName = "Root",
        SasKey = "secret",
    };

    private sealed class FakeManagementClient(Func<EventHubsCredentials, string, string, CancellationToken, ValueTask<EventHubDescription>> handler)
        : IEventHubsManagementClient
    {
        public ValueTask<EventHubDescription> GetEventHubAsync(EventHubsCredentials credentials, string namespaceFqdn, string eventHubName, CancellationToken cancellationToken)
            => handler(credentials, namespaceFqdn, eventHubName, cancellationToken);
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }
}
