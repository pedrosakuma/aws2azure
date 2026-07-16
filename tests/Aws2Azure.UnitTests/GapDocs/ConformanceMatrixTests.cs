using Aws2Azure.GapDocs;

namespace Aws2Azure.UnitTests.GapDocs;

public sealed class ConformanceMatrixTests
{
    [Fact]
    public void Validate_accepts_complete_registered_service_matrix()
    {
        var (matrix, operations) = ValidMatrix();

        var errors = ConformanceMatrixValidator.Validate(matrix, operations);

        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_rejects_unknown_service_and_operation_references()
    {
        var (matrix, operations) = ValidMatrix();
        var replacedService = matrix.Services[0].Service;
        matrix.Services[0].Service = "not-registered";
        matrix.Services[1].Scenarios[0].Operations[0] = "MissingOperation";

        var errors = ConformanceMatrixValidator.Validate(matrix, operations);

        Assert.Contains(errors, error => error.Contains("unregistered service 'not-registered'", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("unknown operation 'MissingOperation'", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains($"missing registered service '{replacedService}'", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_rejects_invalid_scenario_shape_and_missing_service()
    {
        var (matrix, operations) = ValidMatrix();
        matrix.Services.RemoveAll(service => service.Service == "sns");
        var scenario = matrix.Services[0].Scenarios[0];
        scenario.Priority = "urgent";
        scenario.Category = "smoke";
        scenario.EvidenceSource = "emulator";
        scenario.Tests = ["not-qualified"];

        var errors = ConformanceMatrixValidator.Validate(matrix, operations);

        Assert.Contains(errors, error => error.Contains("missing registered service 'sns'", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("invalid priority 'urgent'", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("invalid category 'smoke'", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("invalid evidence_source 'emulator'", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("fully-qualified test identity", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_requires_explicit_evidence_source()
    {
        var (matrix, operations) = ValidMatrix();
        matrix.Services[0].Scenarios[0].EvidenceSource = string.Empty;

        var errors = ConformanceMatrixValidator.Validate(matrix, operations);

        Assert.Contains(errors, error => error.Contains("invalid evidence_source ''", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_requires_explicit_establishes_verification()
    {
        var (matrix, operations) = ValidMatrix();
        matrix.Services[0].Scenarios[0].EstablishesVerification = null;

        var errors = ConformanceMatrixValidator.Validate(matrix, operations);

        Assert.Contains(errors, error => error.Contains("establishes_verification missing", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("deterministic", "core")]
    [InlineData("real_azure", "invalid_credentials")]
    public void Validate_rejects_verification_from_non_positive_live_azure_scenarios(
        string evidenceSource,
        string category)
    {
        var (matrix, operations) = ValidMatrix();
        var scenario = matrix.Services[0].Scenarios[0];
        scenario.EvidenceSource = evidenceSource;
        scenario.Category = category;

        var errors = ConformanceMatrixValidator.Validate(matrix, operations);

        Assert.Contains(errors, error => error.Contains(
            "establishes_verification may be true only for positive real-Azure",
            StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("real_azure", "core", true)]
    [InlineData("deterministic", "core", false)]
    [InlineData("real_azure", "invalid_credentials", false)]
    public void Validate_rejects_optional_coverage_outside_non_establishing_positive_real_azure_scenarios(
        string evidenceSource,
        string category,
        bool establishesVerification)
    {
        var (matrix, operations) = ValidMatrix();
        var scenario = matrix.Services[0].Scenarios[0];
        scenario.EvidenceSource = evidenceSource;
        scenario.Category = category;
        scenario.EstablishesVerification = establishesVerification;
        scenario.OptionalCoverage = true;

        var errors = ConformanceMatrixValidator.Validate(matrix, operations);

        Assert.Contains(errors, error => error.Contains(
            "optional_coverage may be true only for non-establishing positive real-Azure scenarios",
            StringComparison.Ordinal));
    }

    private static (RealAzureConformanceMatrix Matrix, IReadOnlyList<OperationDoc> Operations) ValidMatrix()
    {
        var matrix = new RealAzureConformanceMatrix
        {
            SchemaVersion = 1,
            SourceFile = "matrix.yaml"
        };
        var operations = new List<OperationDoc>();
        foreach (var service in RealAzureConformanceValues.RegisteredServices.OrderBy(value => value, StringComparer.Ordinal))
        {
            matrix.Services.Add(new RealAzureService
            {
                Service = service,
                Scenarios =
                [
                    new RealAzureScenario
                    {
                        Id = "core",
                        Priority = "p0",
                        Category = "core",
                        EvidenceSource = "real_azure",
                        EstablishesVerification = true,
                        Description = "Core operation.",
                        Operations = ["KnownOperation"],
                        Tests = [$"Aws2Azure.IntegrationTests.{service}.Tests.Known_test"]
                    }
                ]
            });
            operations.Add(new OperationDoc
            {
                Service = service,
                Operation = "KnownOperation",
                AzureEquivalent = "Azure",
                Status = "implemented"
            });
        }
        return (matrix, operations);
    }
}
