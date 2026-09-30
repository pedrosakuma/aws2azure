using System.Diagnostics;
using Amazon;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Aws2Azure.IntegrationTests.SecretsManager;
using Xunit;

namespace Aws2Azure.IntegrationTests.OperationalQualification;

[Trait("Category", "RcObservationOffline")]
public sealed class SecretsManagerDiagnosticWorkerTests
{
    [Fact]
    public async Task Diagnostic_reuses_complete_observation_lifecycle_and_value_assertions()
    {
        using var client = new Client(false);
        var tracker = new RealAzureWorkloadLoadTracker("secretsmanager", SecretsManagerRealAzureRcObservationTests.Operations);
        var clock = Stopwatch.StartNew();
        await SecretsManagerRealAzureRcObservationTests.RunWorkerAsync(
            client, tracker, "candidate", 0, TimeSpan.FromSeconds(10), clock, CancellationToken.None,
            strictDiagnostic: true);
        Assert.Equal(SecretsManagerRealAzureRcObservationTests.LifecycleOperationSchedule, client.Calls);
        Assert.Equal(3, tracker.Snapshot("GetSecretValue").Completions);
        Assert.Equal(1, tracker.Snapshot("DeleteSecret").Completions);
    }

    [Fact]
    public async Task Diagnostic_failure_propagates_without_repair_cleanup_or_another_iteration()
    {
        using var client = new Client(true);
        var tracker = new RealAzureWorkloadLoadTracker("secretsmanager", SecretsManagerRealAzureRcObservationTests.Operations);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            SecretsManagerRealAzureRcObservationTests.RunWorkerAsync(
                client, tracker, "candidate", 0, TimeSpan.FromSeconds(10), Stopwatch.StartNew(),
                CancellationToken.None, strictDiagnostic: true));
        Assert.Equal(new[] { "CreateSecret", "DescribeSecret" }, client.Calls);
        Assert.Equal(1, tracker.Snapshot("DescribeSecret").Failures);
        Assert.Equal(0, tracker.Snapshot("DeleteSecret").Completions);
    }

    private sealed class Client(bool failDescribe) : AmazonSecretsManagerClient("key", "secret", RegionEndpoint.USEast1)
    {
        public List<string> Calls { get; } = [];
        private string _value = "";

        public override Task<CreateSecretResponse> CreateSecretAsync(CreateSecretRequest request, CancellationToken cancellationToken = default)
        {
            Calls.Add("CreateSecret");
            _value = request.SecretString;
            return Task.FromResult(new CreateSecretResponse { Name = request.Name });
        }

        public override Task<DescribeSecretResponse> DescribeSecretAsync(DescribeSecretRequest request, CancellationToken cancellationToken = default)
        {
            Calls.Add("DescribeSecret");
            if (failDescribe) throw new InvalidDataException("fixture failure");
            return Task.FromResult(new DescribeSecretResponse { Name = request.SecretId });
        }

        public override Task<GetSecretValueResponse> GetSecretValueAsync(GetSecretValueRequest request, CancellationToken cancellationToken = default)
        {
            Calls.Add("GetSecretValue");
            return Task.FromResult(new GetSecretValueResponse { SecretString = _value });
        }

        public override Task<PutSecretValueResponse> PutSecretValueAsync(PutSecretValueRequest request, CancellationToken cancellationToken = default)
        {
            Calls.Add("PutSecretValue");
            _value = request.SecretString;
            return Task.FromResult(new PutSecretValueResponse { VersionId = "00000000000000000000000000000002" });
        }

        public override Task<UpdateSecretResponse> UpdateSecretAsync(UpdateSecretRequest request, CancellationToken cancellationToken = default)
        {
            Calls.Add("UpdateSecret");
            _value = request.SecretString;
            return Task.FromResult(new UpdateSecretResponse { VersionId = "00000000000000000000000000000003" });
        }

        public override Task<ListSecretsResponse> ListSecretsAsync(ListSecretsRequest request, CancellationToken cancellationToken = default)
        {
            Calls.Add("ListSecrets");
            return Task.FromResult(new ListSecretsResponse());
        }

        public override async Task<DeleteSecretResponse> DeleteSecretAsync(DeleteSecretRequest request, CancellationToken cancellationToken = default)
        {
            Calls.Add("DeleteSecret");
            Assert.True(request.ForceDeleteWithoutRecovery);
            // Finish the in-flight lifecycle beyond its admission window, as the real worker does.
            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            return new DeleteSecretResponse();
        }
    }
}
