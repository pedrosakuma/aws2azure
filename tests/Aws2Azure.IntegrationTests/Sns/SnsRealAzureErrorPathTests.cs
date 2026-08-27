using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
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
    private const string ServiceBusApiVersion = "2021-05";

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
    public async Task CreateTopic_rejects_names_that_live_service_bus_topic_rest_rejects()
    {
        Skip.IfNot(fixture.SnsConfigured,
            "AZURE_SB_CONNSTR not set — skipping real-Azure SNS conformance.");

        using var client = fixture.CreateSnsClient();
        var invalidNames = new[]
        {
            "-" + SnsQueryApiClient.CreateTopicName("sns-real-invalidprefix"),
            "_" + SnsQueryApiClient.CreateTopicName("sns-real-invalidprefix"),
            SnsQueryApiClient.CreateTopicName("sns-real-invalidsuffix") + "-",
            SnsQueryApiClient.CreateTopicName("sns-real-invalidsuffix") + "_",
            "-" + SnsQueryApiClient.CreateTopicName("sns-real-invalidfifo") + ".fifo",
        };

        foreach (var topicName in invalidNames)
        {
            var proxyResponse = await SnsQueryApiClient.CreateTopicAsync(client, topicName).ConfigureAwait(false);
            Assert.Equal(HttpStatusCode.BadRequest, proxyResponse.StatusCode);
            Assert.Equal("InvalidParameter", SnsQueryApiClient.ReadErrorCode(proxyResponse));

            var azureResponse = await SendCreateTopicRestProbeAsync(topicName).ConfigureAwait(false);
            Assert.False(
                azureResponse.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created or HttpStatusCode.Conflict,
                $"Live Service Bus unexpectedly accepted topic '{topicName}' via the 2021-05 management REST API. "
                    + $"Status={(int)azureResponse.StatusCode}; "
                    + $"Body={azureResponse.Body}");
        }
    }

    private async Task<RestProbeResponse> SendCreateTopicRestProbeAsync(string topicName)
    {
        var connectionString = fixture.CreateServiceBusConnectionString();
        var parts = ParseServiceBusConnectionString(connectionString);
        var namespaceUri = new Uri(parts.Endpoint);
        var requestUri = new Uri(
            $"https://{namespaceUri.Host}/{Uri.EscapeDataString(topicName)}?api-version={ServiceBusApiVersion}",
            UriKind.Absolute);

        using var request = new HttpRequestMessage(HttpMethod.Put, requestUri);
        request.Headers.TryAddWithoutValidation("Accept", "application/atom+xml");
        request.Headers.TryAddWithoutValidation(
            "Authorization",
            GenerateSharedAccessSignature(requestUri, parts.SharedAccessKeyName, parts.SharedAccessKey, DateTimeOffset.UtcNow.AddMinutes(20)));
        request.Content = new StringContent(BuildTopicDescriptionEntryXml(), Encoding.UTF8, "application/atom+xml");
        request.Content.Headers.ContentType!.Parameters.Add(new NameValueHeaderValue("type", "entry"));

        using var httpClient = new HttpClient();
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        return new RestProbeResponse(response.StatusCode, body);
    }

    private static string BuildTopicDescriptionEntryXml()
        => """
           <?xml version="1.0" encoding="utf-8"?>
           <entry xmlns="http://www.w3.org/2005/Atom">
             <content type="application/xml">
               <TopicDescription xmlns:i="http://www.w3.org/2001/XMLSchema-instance" xmlns="http://schemas.microsoft.com/netservices/2010/10/servicebus/connect">
                 <RequiresDuplicateDetection>false</RequiresDuplicateDetection>
               </TopicDescription>
             </content>
           </entry>
           """;

    private static ServiceBusConnectionStringParts ParseServiceBusConnectionString(string connectionString)
    {
        string? endpoint = null;
        string? sharedAccessKeyName = null;
        string? sharedAccessKey = null;

        foreach (var segment in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = segment.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = segment[..separator];
            var value = segment[(separator + 1)..];
            switch (key)
            {
                case "Endpoint":
                    endpoint = value;
                    break;
                case "SharedAccessKeyName":
                    sharedAccessKeyName = value;
                    break;
                case "SharedAccessKey":
                    sharedAccessKey = value;
                    break;
            }
        }

        Assert.False(string.IsNullOrWhiteSpace(endpoint), "Service Bus connection string did not contain Endpoint.");
        Assert.False(string.IsNullOrWhiteSpace(sharedAccessKeyName), "Service Bus connection string did not contain SharedAccessKeyName.");
        Assert.False(string.IsNullOrWhiteSpace(sharedAccessKey), "Service Bus connection string did not contain SharedAccessKey.");
        return new ServiceBusConnectionStringParts(endpoint!, sharedAccessKeyName!, sharedAccessKey!);
    }

    private static string GenerateSharedAccessSignature(
        Uri resourceUri,
        string keyName,
        string keyValue,
        DateTimeOffset expiry)
    {
        var resource = resourceUri.GetLeftPart(UriPartial.Path).TrimEnd('/').ToLowerInvariant();
        var encodedResource = WebUtility.UrlEncode(resource);
        var expirySeconds = expiry.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var stringToSign = encodedResource + "\n" + expirySeconds;

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(keyValue));
        var signature = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(stringToSign)));
        return "SharedAccessSignature sr=" + encodedResource
             + "&sig=" + WebUtility.UrlEncode(signature)
             + "&se=" + expirySeconds
             + "&skn=" + WebUtility.UrlEncode(keyName);
    }

    private sealed record ServiceBusConnectionStringParts(
        string Endpoint,
        string SharedAccessKeyName,
        string SharedAccessKey);

    private sealed record RestProbeResponse(
        HttpStatusCode StatusCode,
        string Body);
}
