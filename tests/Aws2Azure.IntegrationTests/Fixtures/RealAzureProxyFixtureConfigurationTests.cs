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
}
