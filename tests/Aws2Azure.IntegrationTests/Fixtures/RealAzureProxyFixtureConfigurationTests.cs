using Xunit;

namespace Aws2Azure.IntegrationTests.Fixtures;

public sealed class RealAzureProxyFixtureConfigurationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Disabled")]
    public void Basic_and_query_default_configuration_keeps_sprocs_disabled(
        string? mode)
    {
        var options = RealAzureProxyFixture.BuildDynamoDbServiceOptions(
            configured: true,
            storedProcedureMode: mode);

        Assert.DoesNotContain(
            "\"useStoredProcedures\"",
            options,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Transaction_plan_enables_preferred_sprocs()
    {
        var options = RealAzureProxyFixture.BuildDynamoDbServiceOptions(
            configured: true,
            storedProcedureMode: "Preferred");

        Assert.Contains(
            "\"useStoredProcedures\": \"Preferred\"",
            options,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("dynamodb-basic-crud")]
    [InlineData("dynamodb-query-scan-indexes")]
    public void Dedicated_fixture_defaults_non_transaction_profiles_to_disabled(
        string profile)
    {
        var mode = DynamoDbRealAzureProxyFixture.ResolveStoredProcedureMode(
            profile,
            configuredMode: null);
        var options =
            DynamoDbRealAzureProxyFixture.BuildDynamoDbServiceOptions(mode);

        Assert.Equal("Disabled", mode);
        Assert.DoesNotContain(
            "\"useStoredProcedures\"",
            options,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Dedicated_fixture_honors_preferred_for_transaction_profile()
    {
        var mode = DynamoDbRealAzureProxyFixture.ResolveStoredProcedureMode(
            "dynamodb-single-partition-transactions",
            "Preferred");
        var options =
            DynamoDbRealAzureProxyFixture.BuildDynamoDbServiceOptions(mode);

        Assert.Equal("Preferred", mode);
        Assert.Contains(
            "\"useStoredProcedures\": \"Preferred\"",
            options,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Dedicated_fixture_honors_unfiltered_source_validation_plan()
    {
        var mode = DynamoDbRealAzureProxyFixture.ResolveStoredProcedureMode(
            "dynamodb-basic-crud",
            "Preferred",
            sourceValidation: true);

        Assert.Equal("Preferred", mode);
    }

    [Theory]
    [InlineData("dynamodb-basic-crud", "Preferred")]
    [InlineData("dynamodb-query-scan-indexes", "Preferred")]
    [InlineData("dynamodb-single-partition-transactions", null)]
    [InlineData("dynamodb-single-partition-transactions", "Disabled")]
    public void Dedicated_fixture_rejects_profile_mode_mismatches(
        string profile,
        string? mode)
    {
        Assert.Throws<InvalidDataException>(
            () => DynamoDbRealAzureProxyFixture.ResolveStoredProcedureMode(
                profile,
                mode));
    }
}
