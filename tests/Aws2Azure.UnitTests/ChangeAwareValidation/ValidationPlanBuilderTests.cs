using Aws2Azure.ChangeAwareValidation;

namespace Aws2Azure.UnitTests.ChangeAwareValidation;

public sealed class ValidationPlanBuilderTests
{
    [Theory]
    [InlineData(
        "src/Aws2Azure.Modules.S3/Operations/ObjectHandlers.cs",
        "integration",
        "required")]
    [InlineData(
        "src/Aws2Azure.Modules.S3/Operations/ObjectHandlers.cs",
        "perf",
        "required")]
    [InlineData(
        "src/Aws2Azure.Core/Azure/SharedKeyAuthenticator.cs",
        "real-azure",
        "required")]
    [InlineData(
        "src/Aws2Azure.Modules.S3/Internal/BlobClient.cs",
        "real-azure",
        "required")]
    [InlineData(
        "src/Aws2Azure.Amqp/Transport/AmqpFrameIO.cs",
        "integration",
        "required")]
    [InlineData(
        "src/Aws2Azure.Proxy/Program.cs",
        "footprint",
        "required")]
    [InlineData("Directory.Build.props", "footprint", "required")]
    [InlineData("global.json", "footprint", "required")]
    [InlineData(
        "tests/Aws2Azure.Conformance/S3/S3ConformanceTests.cs",
        "conformance",
        "required")]
    [InlineData(
        "tools/Aws2Azure.ChangeAwareValidation/ValidationPlanBuilder.cs",
        "unit",
        "required")]
    [InlineData(
        "docs/testing/real-azure-conformance.yaml",
        "real-azure",
        "required")]
    [InlineData("docs/perf/microbench-reference.json", "unit", "required")]
    [InlineData(".github/workflows/conformance.yml", "conformance", "required")]
    [InlineData(".github/workflows/qualification-real-azure.yml", "perf", "required")]
    [InlineData(".github/workflows/qualification-real-azure.yml", "real-azure", "required")]
    [InlineData(".github/workflows/workload-load-real-azure.yml", "perf", "required")]
    [InlineData(".github/workflows/workload-load-real-azure.yml", "real-azure", "required")]
    [InlineData(".github/workflows/rc-observation-real-azure.yml", "perf", "required")]
    [InlineData(".github/workflows/rc-observation-real-azure.yml", "real-azure", "required")]
    [InlineData("deploy/realazure/secretsmanager-load.bicep", "perf", "required")]
    [InlineData("deploy/realazure/s3-load.bicep", "perf", "required")]
    [InlineData("deploy/realazure/dynamodb-load.bicep", "perf", "required")]
    [InlineData(
        "tests/Aws2Azure.IntegrationTests/SecretsManager/SecretsManagerRealAzureLoadQualificationTests.cs",
        "perf",
        "required")]
    [InlineData(
        "tests/Aws2Azure.IntegrationTests/S3/S3RealAzureLoadQualificationTests.cs",
        "perf",
        "required")]
    [InlineData(
        "tests/Aws2Azure.IntegrationTests/DynamoDb/DynamoDbRealAzureLoadQualificationTests.cs",
        "perf",
        "required")]
    [InlineData(
        "tests/Aws2Azure.IntegrationTests/OperationalQualification/RealAzureWorkloadLoadEvidence.cs",
        "perf",
        "required")]
    [InlineData("docs/workloads/qualification/s3.yaml", "real-azure", "required")]
    [InlineData("docs/workloads/observation/s3-basic-object-crud.yaml", "perf", "required")]
    [InlineData("docs/workloads/observation/s3-basic-object-crud.yaml", "real-azure", "required")]
    [InlineData("docs/workloads/approved-runtimes/s3-basic-object-crud.yaml", "perf", "required")]
    [InlineData(
        "docs/workloads/approved-runtimes/s3-basic-object-crud.yaml",
        "real-azure",
        "required")]
    [InlineData("docs/workloads/approved-runtimes/dynamodb-basic-crud.yaml", "perf", "required")]
    [InlineData(
        "docs/workloads/approved-runtimes/dynamodb-basic-crud.yaml",
        "real-azure",
        "required")]
    [InlineData(
        "tests/Aws2Azure.IntegrationTests/S3/S3RealAzureRcObservationTests.cs",
        "perf",
        "required")]
    [InlineData(
        "tests/Aws2Azure.IntegrationTests/OperationalQualification/SecretsManagerRestartQualificationTests.cs",
        "real-azure",
        "required")]
    [InlineData("eng/validate.ps1", "integration", "required")]
    [InlineData(".github/copilot-instructions.md", "build", "optional")]
    [InlineData(".github/copilot-instructions.md", "integration", "not-applicable")]
    public void RepresentativePathsMapToExpectedGate(
        string path,
        string gateName,
        string expectedStatus)
    {
        var plan = ValidationPlanBuilder.Build([path]);

        var gate = Assert.Single(plan.Gates, gate => gate.Name == gateName);
        Assert.Equal(expectedStatus, gate.Status);
    }

    [Fact]
    public void SensitiveChangesReturnRequiredPullRequestLabels()
    {
        var plan = ValidationPlanBuilder.Build(
        [
            "src/Aws2Azure.Core/Azure/AzureHttpClient.cs",
            "src/Aws2Azure.Proxy/Program.cs"
        ]);

        Assert.Equal(
            ["run-footprint", "run-integration", "run-perf", "run-real-azure"],
            plan.RequiredLabels);
        Assert.Equal(7, plan.Gates.Length);
    }

    [Fact]
    public void BaselineChangesRequireEvidenceAndBlockAutomaticThresholdBumps()
    {
        var plan = ValidationPlanBuilder.Build(["docs/perf/baseline-reference.json"]);

        Assert.Equal("required", Gate(plan, "perf").Status);
        Assert.Contains(plan.Warnings, warning => warning.Contains("threshold", StringComparison.Ordinal));
        Assert.Contains(
            plan.FailurePolicy,
            policy => policy.Contains("Do not automatically bump", StringComparison.Ordinal));
        Assert.Contains(
            plan.FailurePolicy,
            policy => policy.Contains("main", StringComparison.Ordinal));
    }

    private static GateDecision Gate(ValidationPlan plan, string name)
    {
        return Assert.Single(plan.Gates, gate => gate.Name == name);
    }
}
