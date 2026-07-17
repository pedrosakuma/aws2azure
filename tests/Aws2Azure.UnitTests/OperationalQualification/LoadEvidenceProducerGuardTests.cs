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
}
