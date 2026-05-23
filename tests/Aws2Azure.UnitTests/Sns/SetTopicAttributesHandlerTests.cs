using System.Net;
using System.Net.Http;
using System.Text;
using Aws2Azure.Modules.Sns;
using Aws2Azure.Modules.Sns.Operations;
using Aws2Azure.Modules.Sns.WireProtocol;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.UnitTests.Sns;

public sealed class SetTopicAttributesHandlerTests
{
    [Theory]
    [InlineData("DisplayName")]
    [InlineData("Policy")]
    [InlineData("DeliveryPolicy")]
    [InlineData("EffectiveDeliveryPolicy")]
    [InlineData("KmsMasterKeyId")]
    [InlineData("SignatureVersion")]
    [InlineData("TracingConfig")]
    public async Task HandleAsync_returns_success_for_no_op_attributes(string attributeName)
    {
        var context = SnsManagementClientTestSupport.NewContext();
        await SetTopicAttributesHandler.HandleAsync(
            context,
            NewParseResult(attributeName, string.Empty),
            SnsManagementClientTestSupport.NewCredentials(),
            SnsManagementClientTestSupport.NewManagementClient((_, _) => throw new InvalidOperationException("HTTP should not be called.")),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Contains("<SetTopicAttributesResponse", SnsManagementClientTestSupport.ReadBody(context));
        Assert.DoesNotContain("SetTopicAttributesResult", SnsManagementClientTestSupport.ReadBody(context));
    }

    [Fact]
    public async Task HandleAsync_rejects_content_based_deduplication_changes()
    {
        var requests = 0;
        var managementClient = SnsManagementClientTestSupport.NewManagementClient((request, _) =>
        {
            requests++;
            Assert.Equal(HttpMethod.Get, request.Method);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SnsManagementClientTestSupport.BuildTopicEntry("orders", subscriptionCount: 1, requiresDuplicateDetection: false), Encoding.UTF8, "application/atom+xml"),
            });
        });

        var context = SnsManagementClientTestSupport.NewContext();
        await SetTopicAttributesHandler.HandleAsync(
            context,
            NewParseResult("ContentBasedDeduplication", "true"),
            SnsManagementClientTestSupport.NewCredentials(),
            managementClient,
            CancellationToken.None);

        Assert.Equal(1, requests);
        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Contains("cannot be changed after the Service Bus topic has been created", SnsManagementClientTestSupport.ReadBody(context));
    }

    [Fact]
    public async Task HandleAsync_rejects_unknown_attribute_names()
    {
        var context = SnsManagementClientTestSupport.NewContext();
        await SetTopicAttributesHandler.HandleAsync(
            context,
            NewParseResult("UnknownAttribute", "value"),
            SnsManagementClientTestSupport.NewCredentials(),
            SnsManagementClientTestSupport.NewManagementClient((_, _) => throw new InvalidOperationException("HTTP should not be called.")),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Contains("Invalid attribute name: UnknownAttribute", SnsManagementClientTestSupport.ReadBody(context));
    }

    private static SnsParseResult NewParseResult(string attributeName, string attributeValue)
        => new(
            SnsOperation.SetTopicAttributes,
            new Dictionary<string, string>
            {
                ["TopicArn"] = "arn:aws:sns:us-west-2:000000000000:orders",
                ["AttributeName"] = attributeName,
                ["AttributeValue"] = attributeValue,
            },
            null);
}
