using System.Net;
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
}
