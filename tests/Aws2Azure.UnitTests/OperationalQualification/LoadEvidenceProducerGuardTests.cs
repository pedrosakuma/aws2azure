using Aws2Azure.TestSupport.OperationalQualification;

namespace Aws2Azure.UnitTests.OperationalQualification;

public sealed class LoadEvidenceProducerGuardTests
{
    [Fact]
    public async Task PublishAsync_does_not_publish_failed_producer_evidence()
    {
        var published = false;
        var successfulOperations = new[]
        {
            new LoadOperationOutcome("PutObject", 1, 0, null),
        };

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            LoadEvidenceProducerGuard.PublishAsync(
                0,
                successfulOperations,
                string.Empty,
                () =>
                {
                    published = true;
                    return Task.CompletedTask;
                }));
        Assert.False(published);

        var failedOperations = new[]
        {
            new LoadOperationOutcome("PutObject", 1, 1, "request failed"),
        };
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            LoadEvidenceProducerGuard.PublishAsync(
                1,
                failedOperations,
                "proxy diagnostics",
                () =>
                {
                    published = true;
                    return Task.CompletedTask;
                }));
        Assert.False(published);

        var incompleteOperations = new[]
        {
            new LoadOperationOutcome("PutObject", 1, 0, null),
            new LoadOperationOutcome("DeleteObject", 0, 0, null),
        };
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            LoadEvidenceProducerGuard.PublishAsync(
                1,
                incompleteOperations,
                string.Empty,
                () =>
                {
                    published = true;
                    return Task.CompletedTask;
                }));
        Assert.False(published);
    }

    [Fact]
    public async Task PublishAsync_publishes_only_valid_evidence()
    {
        var published = false;

        await LoadEvidenceProducerGuard.PublishAsync(
            1,
            [new LoadOperationOutcome("PutObject", 1, 0, null)],
            string.Empty,
            () =>
            {
                published = true;
                return Task.CompletedTask;
            });

        Assert.True(published);
    }

    [Fact]
    public async Task PublishAsync_requires_complete_scenarios_and_one_rollback_proof()
    {
        var published = false;
        var operations = new[]
        {
            new LoadOperationOutcome("TransactWriteItems", 1, 0, null),
            new LoadOperationOutcome("TransactGetItems", 1, 0, null),
        };
        var required = new[] { "representative-load", "rollback" };

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            LoadEvidenceProducerGuard.PublishAsync(
                1,
                operations,
                required,
                ["representative-load"],
                1,
                string.Empty,
                () =>
                {
                    published = true;
                    return Task.CompletedTask;
                }));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            LoadEvidenceProducerGuard.PublishAsync(
                1,
                operations,
                required,
                required,
                0,
                string.Empty,
                () =>
                {
                    published = true;
                    return Task.CompletedTask;
                }));

        Assert.False(published);
    }

    [Theory]
    [InlineData("wrong-profile", "rollback", "Preferred", true, true)]
    [InlineData("dynamodb-single-partition-transactions", "source_validation", "Preferred", true, true)]
    [InlineData("dynamodb-single-partition-transactions", "rollback", "Disabled", true, true)]
    [InlineData("dynamodb-single-partition-transactions", "rollback", "Preferred", true, false)]
    public void Transaction_context_rejects_wrong_profile_mode_or_missing_prior(
        string profile,
        string mode,
        string storedProcedureMode,
        bool candidateConfigured,
        bool priorConfigured)
    {
        Assert.Throws<InvalidDataException>(() =>
            LoadEvidenceProducerGuard.ValidateTransactionQualificationContext(
                profile,
                mode,
                storedProcedureMode,
                candidateConfigured,
                priorConfigured,
                "sha256:candidate",
                "sha256:prior"));
    }
}
