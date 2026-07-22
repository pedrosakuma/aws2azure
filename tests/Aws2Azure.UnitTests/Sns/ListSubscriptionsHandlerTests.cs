using System.Net;
using System.Net.Http;
using System.Text;
using Aws2Azure.Modules.Sns;
using Aws2Azure.Modules.Sns.Operations;
using Aws2Azure.Modules.Sns.WireProtocol;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.UnitTests.Sns;

public sealed class ListSubscriptionsHandlerTests
{
    [Fact]
    public async Task HandleAsync_flattens_subscriptions_across_topics_and_round_trips_pagination()
    {
        var orders = Enumerable.Range(0, 60)
            .Select(i => ($"orders-{i:D3}", SnsManagementClientTestSupport.SerializeMetadata("https", $"https://orders.example/{i}")))
            .ToArray();
        var payments = Enumerable.Range(0, 45)
            .Select(i => ($"payments-{i:D3}", SnsManagementClientTestSupport.SerializeMetadata("sqs", $"arn:aws:sqs:us-west-2:000000000000:payments-{i:D3}")))
            .ToArray();

        var managementClient = SnsManagementClientTestSupport.NewManagementClient((request, _) =>
        {
            var uri = request.RequestUri!.ToString();
            return uri switch
            {
                "https://myns.servicebus.windows.net/$Resources/topics?api-version=2021-05&$skip=0&$top=100"
                    => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(SnsManagementClientTestSupport.BuildTopicsFeed("orders", "payments"), Encoding.UTF8, "application/atom+xml"),
                    }),
                "https://myns.servicebus.windows.net/orders/subscriptions?api-version=2021-05&$skip=0&$top=100"
                    => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(SnsManagementClientTestSupport.BuildSubscriptionsFeed(orders), Encoding.UTF8, "application/atom+xml"),
                    }),
                "https://myns.servicebus.windows.net/payments/subscriptions?api-version=2021-05&$skip=0&$top=40"
                    => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(SnsManagementClientTestSupport.BuildSubscriptionsFeed(payments[..40]), Encoding.UTF8, "application/atom+xml"),
                    }),
                "https://myns.servicebus.windows.net/$Resources/topics?api-version=2021-05&$skip=1&$top=100"
                    => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(SnsManagementClientTestSupport.BuildTopicsFeed("payments"), Encoding.UTF8, "application/atom+xml"),
                    }),
                "https://myns.servicebus.windows.net/payments/subscriptions?api-version=2021-05&$skip=40&$top=100"
                    => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(SnsManagementClientTestSupport.BuildSubscriptionsFeed(payments[40..]), Encoding.UTF8, "application/atom+xml"),
                    }),
                _ => throw new Xunit.Sdk.XunitException("Unexpected URI: " + uri)
            };
        });

        var firstContext = SnsManagementClientTestSupport.NewContext();
        await ListSubscriptionsHandler.HandleAsync(
            firstContext,
            new SnsParseResult(SnsOperation.ListSubscriptions, new Dictionary<string, string>(), null),
            SnsManagementClientTestSupport.NewCredentials(),
            managementClient,
            SigningKey,
            CancellationToken.None);

        var firstBody = SnsManagementClientTestSupport.ReadBody(firstContext);
        Assert.Contains("arn:aws:sns:us-west-2:000000000000:orders:orders-059", firstBody);
        Assert.Contains("arn:aws:sns:us-west-2:000000000000:payments:payments-039", firstBody);
        var nextToken = SnsManagementClientTestSupport.ReadElementValue(firstBody, "NextToken");
        var decoded = DecodeNextToken(nextToken);
        Assert.Equal(1, decoded.TopicSkip);
        Assert.Equal(40, decoded.SubscriptionSkipWithinTopic);

        var secondContext = SnsManagementClientTestSupport.NewContext();
        await ListSubscriptionsHandler.HandleAsync(
            secondContext,
            new SnsParseResult(SnsOperation.ListSubscriptions, new Dictionary<string, string> { ["NextToken"] = nextToken }, null),
            SnsManagementClientTestSupport.NewCredentials(),
            managementClient,
            SigningKey,
            CancellationToken.None);

        var secondBody = SnsManagementClientTestSupport.ReadBody(secondContext);
        Assert.Contains("arn:aws:sns:us-west-2:000000000000:payments:payments-044", secondBody);
        Assert.DoesNotContain("<NextToken>", secondBody);
    }

    private static SnsListSubscriptionsNextToken DecodeNextToken(string nextToken)
    {
        Assert.True(SnsSubscriptionSupport.TryDecodeNextToken(
            nextToken,
            SigningKey,
            SnsOperation.ListSubscriptions,
            expectedTopicName: null,
            out var decoded));
        return decoded;
    }

    private const string SigningKey = "unit-test-pagination-signing-key";
}
