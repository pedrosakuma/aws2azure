using System.Net;
using System.Net.Http;
using System.Text;
using Aws2Azure.Modules.Sns;
using Aws2Azure.Modules.Sns.Operations;
using Aws2Azure.Modules.Sns.WireProtocol;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.UnitTests.Sns;

public sealed class ListSubscriptionsByTopicHandlerTests
{
    [Fact]
    public async Task HandleAsync_lists_subscriptions_for_topic()
    {
        var managementClient = SnsManagementClientTestSupport.NewManagementClient((request, _) =>
        {
            Assert.Equal("https://myns.servicebus.windows.net/orders/subscriptions?api-version=2021-05&$skip=0&$top=100", request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SnsManagementClientTestSupport.BuildSubscriptionsFeed(
                    ("sub-1", SnsManagementClientTestSupport.SerializeMetadata("https", "https://example.com/a")),
                    ("sub-2", SnsManagementClientTestSupport.SerializeMetadata("sqs", "arn:aws:sqs:us-west-2:000000000000:queue"))), Encoding.UTF8, "application/atom+xml"),
            });
        });

        var context = SnsManagementClientTestSupport.NewContext();
        await ListSubscriptionsByTopicHandler.HandleAsync(
            context,
            NewParseResult(null),
            SnsManagementClientTestSupport.NewCredentials(),
            managementClient,
            CancellationToken.None);

        var body = SnsManagementClientTestSupport.ReadBody(context);
        Assert.Contains("arn:aws:sns:us-west-2:000000000000:orders:sub-1", body);
        Assert.Contains("https://example.com/a", body);
        Assert.Contains("arn:aws:sns:us-west-2:000000000000:orders", body);
    }

    [Fact]
    public async Task HandleAsync_round_trips_pagination_token()
    {
        var firstPage = Enumerable.Range(0, 100)
            .Select(i => ($"sub-{i:D3}", SnsManagementClientTestSupport.SerializeMetadata("https", $"https://example.com/{i}")))
            .ToArray();

        var managementClient = SnsManagementClientTestSupport.NewManagementClient((request, _) =>
        {
            var uri = request.RequestUri!.ToString();
            return uri switch
            {
                "https://myns.servicebus.windows.net/orders/subscriptions?api-version=2021-05&$skip=0&$top=100"
                    => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(SnsManagementClientTestSupport.BuildSubscriptionsFeed(firstPage), Encoding.UTF8, "application/atom+xml"),
                    }),
                "https://myns.servicebus.windows.net/orders/subscriptions?api-version=2021-05&$skip=100&$top=100"
                    => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(SnsManagementClientTestSupport.BuildSubscriptionsFeed(("sub-100", SnsManagementClientTestSupport.SerializeMetadata("https", "https://example.com/100"))), Encoding.UTF8, "application/atom+xml"),
                    }),
                _ => throw new Xunit.Sdk.XunitException("Unexpected URI: " + uri)
            };
        });

        var firstContext = SnsManagementClientTestSupport.NewContext();
        await ListSubscriptionsByTopicHandler.HandleAsync(firstContext, NewParseResult(null), SnsManagementClientTestSupport.NewCredentials(), managementClient, CancellationToken.None);
        var firstBody = SnsManagementClientTestSupport.ReadBody(firstContext);
        var nextToken = SnsManagementClientTestSupport.ReadElementValue(firstBody, "NextToken");
        Assert.True(SnsSubscriptionSupport.TryDecodeNextToken(nextToken, out var decoded));
        Assert.Equal(100, decoded.SubscriptionSkipWithinTopic);

        var secondContext = SnsManagementClientTestSupport.NewContext();
        await ListSubscriptionsByTopicHandler.HandleAsync(secondContext, NewParseResult(nextToken), SnsManagementClientTestSupport.NewCredentials(), managementClient, CancellationToken.None);
        var secondBody = SnsManagementClientTestSupport.ReadBody(secondContext);
        Assert.Contains("arn:aws:sns:us-west-2:000000000000:orders:sub-100", secondBody);
        Assert.DoesNotContain("<NextToken>", secondBody);
    }

    [Fact]
    public async Task HandleAsync_returns_empty_list_for_topic_without_subscriptions()
    {
        var context = SnsManagementClientTestSupport.NewContext();
        await ListSubscriptionsByTopicHandler.HandleAsync(
            context,
            NewParseResult(null),
            SnsManagementClientTestSupport.NewCredentials(),
            SnsManagementClientTestSupport.NewManagementClient((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SnsManagementClientTestSupport.BuildSubscriptionsFeed(), Encoding.UTF8, "application/atom+xml"),
            })),
            CancellationToken.None);

        var body = SnsManagementClientTestSupport.ReadBody(context);
        Assert.Contains("<Subscriptions />", body);
        Assert.DoesNotContain("<NextToken>", body);
    }

    private static SnsParseResult NewParseResult(string? nextToken)
    {
        var parameters = new Dictionary<string, string>
        {
            ["TopicArn"] = "arn:aws:sns:us-west-2:000000000000:orders"
        };

        if (nextToken is not null)
        {
            parameters["NextToken"] = nextToken;
        }

        return new SnsParseResult(SnsOperation.ListSubscriptionsByTopic, parameters, null);
    }
}
