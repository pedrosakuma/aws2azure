namespace Aws2Azure.PerfTests.SecretsManager;

internal sealed record SecretsCapacityScenario(string Id, string Operation, bool SingleUse = false)
{
    public string Name => $"secretsmanager.capacity.{Id}";
}

internal static class SecretsCapacityPlan
{
    public static readonly int[] Concurrency = [1, 2, 5, 8];
    public static readonly SecretsCapacityScenario[] Scenarios =
    [
        new("create-fresh", "CreateSecret", true),
        new("describe-worker-local", "DescribeSecret"),
        new("get-current-worker-local", "GetSecretValue"),
        new("get-version-worker-local", "GetSecretValue"),
        new("get-current-shared", "GetSecretValue"),
        new("put-fresh-version", "PutSecretValue", true),
        new("update-fresh-version", "UpdateSecret", true),
        new("list-first-page-32", "ListSecrets"),
        new("list-second-page-32", "ListSecrets"),
        new("delete-force-fresh", "DeleteSecret", true),
    ];

    public const int WarmupAttempts = 8;
    public const int MeasurementAttempts = 256;
    public static readonly TimeSpan MeasurementDuration = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    // Shipping DeleteSecret's detached purge loop has a 30-second lifetime.
    // This quiet interval is not proof of a successful purge; cleanup checks both endpoints.
    public static readonly TimeSpan PurgeQuietInterval = TimeSpan.FromSeconds(35);
    public static readonly TimeSpan PhaseTimeout = TimeSpan.FromMinutes(5);

    public static SecretsCapacityScenario Select(string? id) =>
        Scenarios.SingleOrDefault(s => s.Id == id)
        ?? throw new InvalidOperationException("Select exactly one AWS2AZURE_SECRETS_CAPACITY_SCENARIO from the documented catalog.");

    public static bool Enabled(string? value) => value switch
    {
        null or "" or "0" => false,
        "1" => true,
        _ => throw new InvalidOperationException("AWS2AZURE_SECRETS_CAPACITY must be 0 or 1."),
    };

    public static void AssertUsable(PerfResult result, TimeSpan duration, int attemptLimit)
    {
        result.AssertHealthy(maxFailureRate: 0);
        result.AssertNoRegression();
        if (result.Completed == 0)
            throw new InvalidOperationException("No successful capacity evidence (including a fully throttled window).");
        if (result.Completed + result.Failures + result.Throttled >= attemptLimit)
            throw new InvalidOperationException("Finite request/inventory budget exhausted. Evidence may be truncated, not capacity-qualified; do not replenish during measurement.");
        if (result.ElapsedSeconds < duration.TotalSeconds)
            throw new InvalidOperationException("The requested measurement interval was not completed.");
    }
}

internal sealed class SecretsCapacityInventory(SecretsCapacityScenario scenario, int concurrency, string prefix)
{
    private int _next = -1;
    public string[] Names { get; } = Enumerable.Range(0, scenario.SingleUse
            ? SecretsCapacityPlan.WarmupAttempts + SecretsCapacityPlan.MeasurementAttempts
            : scenario.Operation == "ListSecrets" ? 32 : scenario.Id == "get-current-shared" ? 1 : 8)
        .Select(i => $"{prefix}-{i}").ToArray();

    public int Claim(int worker)
    {
        if ((uint)worker >= (uint)concurrency)
            throw new ArgumentOutOfRangeException(nameof(worker));
        var index = scenario.SingleUse ? Interlocked.Increment(ref _next)
            : scenario.Id == "get-current-shared" ? 0 : worker % Names.Length;
        if ((uint)index >= (uint)Names.Length)
            throw new InvalidOperationException("Preallocated secret inventory exhausted; setup inside the measured interval is forbidden.");
        return index;
    }
}
