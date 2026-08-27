using System.Net;
using Aws2Azure.Modules.Sns.Management;
using Xunit;

namespace Aws2Azure.IntegrationTests.Sns;

// Kept as a separate file (rather than growing SnsRealAzureConformanceTests.cs,
// already at the 700-line hard cap) to keep future error-path additions
// sustainable to review and extend independently of the happy-path suite.
[Trait("Category", "RealAzure")]
[Collection(RealAzureCollection.Name)]
public sealed class SnsRealAzureErrorPathTests(RealAzureProxyFixture fixture)
{
    [SkippableFact]
    public async Task Publish_to_nonexistent_topic_returns_native_not_found_error()
    {
        Skip.IfNot(fixture.SnsConfigured,
            "AZURE_SB_CONNSTR not set — skipping real-Azure SNS conformance.");

        using var client = fixture.CreateSnsClient();
        var missingTopicName = SnsQueryApiClient.CreateTopicName("sns-missing-topic");
        var missingTopicArn = $"arn:aws:sns:us-east-1:000000000000:{missingTopicName}";

        var response = await SnsQueryApiClient.SendActionAsync(
            client,
            "Publish",
            [new("TopicArn", missingTopicArn), new("Message", "should-not-be-delivered")],
            RealAzureProxyFixture.AwsAccessKey,
            RealAzureProxyFixture.AwsSecret).ConfigureAwait(false);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("NotFound", response.Xml?.Descendants().FirstOrDefault(x => x.Name.LocalName == "Code")?.Value);
    }

    [SkippableFact]
    public async Task CreateTopic_preserves_aws_valid_boundary_names_that_live_service_bus_topic_rest_accepts()
    {
        Skip.IfNot(fixture.SnsConfigured,
            "AZURE_SB_CONNSTR not set — skipping real-Azure SNS conformance.");

        using var client = fixture.CreateSnsClient();
        var boundaryCases = new[]
        {
            new TopicBoundaryCase("-" + SnsQueryApiClient.CreateTopicName("sns-real-leadinghyphen"), false),
            new TopicBoundaryCase("_" + SnsQueryApiClient.CreateTopicName("sns-real-leadingunderscore"), false),
            new TopicBoundaryCase(SnsQueryApiClient.CreateTopicName("sns-real-trailinghyphen") + "-", false),
            new TopicBoundaryCase(SnsQueryApiClient.CreateTopicName("sns-real-trailingunderscore") + "_", false),
            new TopicBoundaryCase("-" + SnsQueryApiClient.CreateTopicName("sns-real-leadinghyphenfifo") + ".fifo", true),
        };

        foreach (var boundaryCase in boundaryCases)
        {
            var azureResponse = await SnsRealAzureManagementRestProbe.CreateTopicAsync(
                fixture.CreateServiceBusConnectionString(),
                boundaryCase.TopicName).ConfigureAwait(false);
            Assert.False(
                azureResponse.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.Created or HttpStatusCode.Conflict),
                $"Live Service Bus did not accept topic '{boundaryCase.TopicName}' via the 2021-05 management REST API. "
                    + azureResponse.FormatDiagnosticSummary());
            var azureDelete = await SnsRealAzureManagementRestProbe.DeleteTopicAsync(
                fixture.CreateServiceBusConnectionString(),
                boundaryCase.TopicName).ConfigureAwait(false);
            Assert.True(
                azureDelete.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent or HttpStatusCode.NotFound,
                $"Live Service Bus cleanup failed for topic '{boundaryCase.TopicName}'. "
                    + azureDelete.FormatDiagnosticSummary());

            var proxyResponse = boundaryCase.IsFifo
                ? await SnsQueryApiClient.CreateTopicAsync(
                    client,
                    boundaryCase.TopicName,
                    RealAzureProxyFixture.AwsAccessKey,
                    RealAzureProxyFixture.AwsSecret,
                    ("FifoTopic", "true")).ConfigureAwait(false)
                : await SnsQueryApiClient.SendActionAsync(
                    client,
                    "CreateTopic",
                    [new("Name", boundaryCase.TopicName)],
                    RealAzureProxyFixture.AwsAccessKey,
                    RealAzureProxyFixture.AwsSecret).ConfigureAwait(false);
            SnsServiceBusTestSupport.AssertStatus(proxyResponse, HttpStatusCode.OK, $"CreateTopic[{boundaryCase.TopicName}]");
            var proxyDelete = await SnsQueryApiClient.DeleteTopicAsync(
                client,
                SnsQueryApiClient.ReadTopicArn(proxyResponse),
                RealAzureProxyFixture.AwsAccessKey,
                RealAzureProxyFixture.AwsSecret).ConfigureAwait(false);
            SnsServiceBusTestSupport.AssertStatus(proxyDelete, HttpStatusCode.OK, $"DeleteTopic[{boundaryCase.TopicName}]");
        }
    }

    private sealed record TopicBoundaryCase(
        string TopicName,
        bool IsFifo);
}
