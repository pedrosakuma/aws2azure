using System.Collections.Concurrent;
using System.Net;
using Amazon.Runtime;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Xunit;

namespace Aws2Azure.PerfTests.SecretsManager;

public sealed class SecretsCapacityHarnessTests
{
    private const string SeedVersion = "0123456789abcdef0123456789abcdef";
    [Fact]
    public void Catalog_covers_seven_operations_and_explicit_variants()
    {
        Assert.Equal(7, SecretsCapacityPlan.Scenarios.Select(s => s.Operation).Distinct().Count());
        Assert.Equal([1, 2, 5, 8], SecretsCapacityPlan.Concurrency);
        Assert.Equal(10, SecretsCapacityPlan.Scenarios.Length);
        Assert.All(SecretsCapacityPlan.Scenarios, s => Assert.Contains(s.Name, KnownPerfScenariosTests.All));
        Assert.False(SecretsCapacityPlan.Enabled(null));
        Assert.False(SecretsCapacityPlan.Enabled("0"));
        Assert.True(SecretsCapacityPlan.Enabled("1"));
        Assert.Throws<InvalidOperationException>(() => SecretsCapacityPlan.Enabled("true"));
        Assert.Throws<InvalidOperationException>(() => SecretsCapacityPlan.Select(null));
        Assert.Throws<InvalidOperationException>(() => SecretsCapacityPlan.Select("all"));
    }

    [Theory]
    [InlineData("create-fresh")]
    [InlineData("put-fresh-version")]
    [InlineData("update-fresh-version")]
    [InlineData("delete-force-fresh")]
    public async Task Single_use_inventory_is_finite_and_never_reused_under_concurrency(string id)
    {
        var inventory = new SecretsCapacityInventory(SecretsCapacityPlan.Select(id), 8, "offline");
        var claimed = new ConcurrentBag<int>();
        await Task.WhenAll(Enumerable.Range(0, inventory.Names.Length)
            .Select(_ => Task.Run(() => claimed.Add(inventory.Claim(0)))));
        Assert.Equal(inventory.Names.Length, claimed.Distinct().Count());
        Assert.Equal(264, claimed.Count);
        Assert.Throws<InvalidOperationException>(() => inventory.Claim(0));
    }

    [Fact]
    public void Worker_local_reads_do_not_accidentally_become_shared_contention()
    {
        var local = new SecretsCapacityInventory(SecretsCapacityPlan.Select("get-current-worker-local"), 8, "offline");
        var shared = new SecretsCapacityInventory(SecretsCapacityPlan.Select("get-current-shared"), 8, "offline");
        Assert.Equal(8, Enumerable.Range(0, 8).Select(local.Claim).Distinct().Count());
        Assert.Single(Enumerable.Range(0, 8).Select(shared.Claim).Distinct());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Runner_attempt_budget_is_exact_even_with_failures_and_concurrency(bool fail)
    {
        var calls = 0;
        var result = await PerfRunner.RunAsync("offline-budget-test", 8, TimeSpan.FromSeconds(1),
            async (_, _) =>
            {
                Interlocked.Increment(ref calls);
                await Task.Yield();
                if (fail) throw new InvalidOperationException("offline synthetic failure");
            }, maxAttempts: 17);
        Assert.Equal(17, calls);
        Assert.Equal(17, result.Completed + result.Failures + result.Throttled);
        Assert.Equal(fail ? 17 : 0, result.Failures);
    }

    [Fact]
    public async Task Runner_attempt_budget_counts_throttles_and_rejects_invalid_budget()
    {
        var result = await PerfRunner.RunAsync("offline-budget-test", 8, TimeSpan.FromSeconds(1),
            (_, _) => throw new AmazonSecretsManagerException("offline throttle") { StatusCode = HttpStatusCode.TooManyRequests },
            maxAttempts: 11);
        Assert.Equal(11, result.Throttled);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => PerfRunner.RunAsync(
            "offline-budget-test", 1, TimeSpan.FromSeconds(1), (_, _) => Task.CompletedTask, maxAttempts: 0));
    }

    [Fact]
    public void Early_exhaustion_errors_and_missing_evidence_are_policy_failures()
    {
        var result = new PerfResult(SecretsCapacityPlan.Scenarios[0].Name, 1, 0.1, 256, 0, 2560, 1, 1, 1, 1);
        Assert.Throws<InvalidOperationException>(() => SecretsCapacityPlan.AssertUsable(result, TimeSpan.FromSeconds(5), 256));
        Assert.Throws<Xunit.Sdk.XunitException>(() => SecretsCapacityPlan.AssertUsable(result with { Completed = 0 }, TimeSpan.FromSeconds(5), 256));
        Assert.Throws<Xunit.Sdk.XunitException>(() => SecretsCapacityPlan.AssertUsable(result with { Failures = 1 }, TimeSpan.FromSeconds(5), 256));
        Assert.Throws<InvalidOperationException>(() => SecretsCapacityPlan.AssertUsable(
            result with { ElapsedSeconds = 6 }, TimeSpan.FromSeconds(5), 256));
        SecretsCapacityPlan.AssertUsable(result with { Completed = 255, ElapsedSeconds = 5 }, TimeSpan.FromSeconds(5), 256);
    }

    [Theory]
    [InlineData("create-fresh")]
    [InlineData("describe-worker-local")]
    [InlineData("get-current-worker-local")]
    [InlineData("get-version-worker-local")]
    [InlineData("get-current-shared")]
    [InlineData("put-fresh-version")]
    [InlineData("update-fresh-version")]
    [InlineData("list-first-page-32")]
    [InlineData("list-second-page-32")]
    [InlineData("delete-force-fresh")]
    public async Task Each_action_dispatches_exactly_one_selected_sdk_operation_without_setup_or_polling(string id)
    {
        var scenario = SecretsCapacityPlan.Select(id);
        var inventory = new SecretsCapacityInventory(scenario, 2, "offline");
        using var client = new RecordingClient(inventory.Names);
        var versions = Enumerable.Repeat(SeedVersion, inventory.Names.Length).ToArray();
        await SecretsManagerCapacityTests.InvokeAsync(client, scenario, inventory, versions, "page-2", 1, CancellationToken.None);
        Assert.Equal([scenario.Operation], client.Operations);
        Assert.Equal(id == "get-version-worker-local" ? SeedVersion : null, client.Version);
        Assert.Equal(id == "list-second-page-32" ? "page-2" : null, client.Page);
        if (scenario.Operation == "DeleteSecret") Assert.True(client.ForceDelete);
        if (scenario.SingleUse)
        {
            var first = client.Name;
            await SecretsManagerCapacityTests.InvokeAsync(client, scenario, inventory, versions, "page-2", 1, CancellationToken.None);
            Assert.NotEqual(first, client.Name);
        }
    }

    [Fact]
    public async Task Cleanup_attempts_remaining_owned_names_on_failure_and_never_deletes_unrelated_inventory()
    {
        var calls = new List<string>();
        using var http = new HttpClient(new Handler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            calls.Add($"{request.Method} {path}");
            return new HttpResponseMessage(path.EndsWith("/first", StringComparison.Ordinal)
                ? HttpStatusCode.Forbidden : HttpStatusCode.NotFound);
        })) { BaseAddress = new Uri("https://offline.invalid/") };
        using var backend = new SecretsCapacityBackend(http);
        await Assert.ThrowsAsync<AggregateException>(() => backend.CleanupAsync(["first", "second"], CancellationToken.None));
        Assert.Contains("DELETE /secrets/second", calls);
        Assert.DoesNotContain(calls, c => c.Contains("unrelated", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Preflight_rejects_nonempty_vault_without_mutating_it()
    {
        using var http = new HttpClient(new Handler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"value":[{"id":"unrelated"}]}""") };
        })) { BaseAddress = new Uri("https://offline.invalid/") };
        using var backend = new SecretsCapacityBackend(http);
        await Assert.ThrowsAsync<InvalidOperationException>(() => backend.AssertEmptyAsync(CancellationToken.None));
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(send(request));
    }

    private sealed class RecordingClient(string[] names)
        : AmazonSecretsManagerClient(new AnonymousAWSCredentials(), new AmazonSecretsManagerConfig { ServiceURL = "http://offline.invalid" })
    {
        public List<string> Operations { get; } = [];
        public string? Name { get; private set; }
        public string? Version { get; private set; }
        public string? Page { get; private set; }
        public bool ForceDelete { get; private set; }
        private Task<T> Record<T>(string operation, string? name, T response)
        {
            Operations.Add(operation);
            Name = name;
            return Task.FromResult(response);
        }
        public override Task<CreateSecretResponse> CreateSecretAsync(CreateSecretRequest request, CancellationToken cancellationToken = default) =>
            Record("CreateSecret", request.Name, new CreateSecretResponse());
        public override Task<DescribeSecretResponse> DescribeSecretAsync(DescribeSecretRequest request, CancellationToken cancellationToken = default) =>
            Record("DescribeSecret", request.SecretId, new DescribeSecretResponse { Name = request.SecretId });
        public override Task<GetSecretValueResponse> GetSecretValueAsync(GetSecretValueRequest request, CancellationToken cancellationToken = default)
        {
            Version = request.VersionId;
            return Record("GetSecretValue", request.SecretId, new GetSecretValueResponse
            { VersionId = SeedVersion, SecretString = "capacity-not-a-secret" });
        }
        public override Task<PutSecretValueResponse> PutSecretValueAsync(PutSecretValueRequest request, CancellationToken cancellationToken = default) =>
            Record("PutSecretValue", request.SecretId, new PutSecretValueResponse());
        public override Task<UpdateSecretResponse> UpdateSecretAsync(UpdateSecretRequest request, CancellationToken cancellationToken = default) =>
            Record("UpdateSecret", request.SecretId, new UpdateSecretResponse());
        public override Task<ListSecretsResponse> ListSecretsAsync(ListSecretsRequest request, CancellationToken cancellationToken = default)
        {
            Page = request.NextToken;
            return Record("ListSecrets", null, new ListSecretsResponse
            {
                SecretList = names.Skip(Page is null ? 0 : 16).Take(16).Select(n => new SecretListEntry { Name = n }).ToList(),
                NextToken = Page is null ? "page-2" : null,
            });
        }
        public override Task<DeleteSecretResponse> DeleteSecretAsync(DeleteSecretRequest request, CancellationToken cancellationToken = default)
        {
            ForceDelete = request.ForceDeleteWithoutRecovery == true;
            return Record("DeleteSecret", request.SecretId, new DeleteSecretResponse());
        }
    }
}
