using System.Net;
using System.Net.Http;
using Aws2Azure.Modules.Sqs.Errors;
using Xunit;

namespace Aws2Azure.UnitTests.Sqs;

public class SqsErrorMappingTests
{
    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Gone)] // SB returns 410 after a recent delete; SQS clients expect NonExistentQueue.
    public void NotFound_and_gone_map_to_non_existent_queue(HttpStatusCode status)
    {
        using var resp = new HttpResponseMessage(status);
        var m = SqsErrorMapping.FromServiceBus(resp);
        Assert.Equal("AWS.SimpleQueueService.NonExistentQueue", m.Code);
        Assert.Equal(400, m.StatusCode);
    }

    [Fact]
    public void Conflict_maps_to_queue_already_exists()
    {
        using var resp = new HttpResponseMessage(HttpStatusCode.Conflict);
        var m = SqsErrorMapping.FromServiceBus(resp);
        Assert.Equal("QueueAlreadyExists", m.Code);
    }

    [Fact]
    public void Server_error_maps_to_service_unavailable()
    {
        using var resp = new HttpResponseMessage(HttpStatusCode.InternalServerError);
        var m = SqsErrorMapping.FromServiceBus(resp);
        Assert.Equal("ServiceUnavailable", m.Code);
        Assert.Equal(502, m.StatusCode);
    }
}

