using System.IO;
using System.Net;
using System.Text;
using Aws2Azure.Modules.Kinesis.EventHubsRest;
using Aws2Azure.Modules.Kinesis.Operations;
using Microsoft.AspNetCore.Http;
using Xunit;
using static Aws2Azure.TestSupport.Http.TestHttpContext;

namespace Aws2Azure.UnitTests.Kinesis;

public sealed class KinesisManagementThrottleTests
{
    [Fact]
    public async Task TooManyRequests_maps_to_LimitExceededException()
    {
        // The shared AzureHttpClient passes 429 (Event Hubs management throttling)
        // through without internal retry, so the mapper must surface the Kinesis
        // control-plane throttle the AWS SDK retries with back-off.
        var context = CreateContext();
        var ex = new EventHubsManagementException(HttpStatusCode.TooManyRequests, responseBody: null);

        await KinesisMetadataSupport.WriteManagementErrorAsync(context, ex, "orders");

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Contains("LimitExceededException", ReadBody(context));
    }


}
