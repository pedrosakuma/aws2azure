using System.Net;
using System.Net.Http;
using Aws2Azure.Amqp.Framing;
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

    // ---------- FromAmqp (slice 8c.1) ----------

    [Fact]
    public void FromAmqp_NotFound_condition_maps_to_NonExistentQueue()
    {
        var m = SqsErrorMapping.FromAmqp(AmqpErrorKind.ServerFatal,
            AmqpErrorCondition.NotFound, "ReceiveMessage");
        Assert.Equal("AWS.SimpleQueueService.NonExistentQueue", m.Code);
        Assert.Equal(400, m.StatusCode);
    }

    [Fact]
    public void FromAmqp_ResourceDeleted_condition_maps_to_NonExistentQueue()
    {
        var m = SqsErrorMapping.FromAmqp(AmqpErrorKind.ServerFatal,
            AmqpErrorCondition.ResourceDeleted, "DeleteMessage");
        Assert.Equal("AWS.SimpleQueueService.NonExistentQueue", m.Code);
    }

    [Theory]
    [InlineData(AmqpErrorCondition.MessageLockLost)]
    [InlineData(AmqpErrorCondition.SessionLockLost)]
    public void FromAmqp_lock_lost_conditions_map_to_MessageNotInflight(string cond)
    {
        var m = SqsErrorMapping.FromAmqp(AmqpErrorKind.LockLost, cond, "DeleteMessage");
        Assert.Equal("MessageNotInflight", m.Code);
        Assert.Equal(400, m.StatusCode);
    }

    [Fact]
    public void FromAmqp_LockLost_kind_without_condition_maps_to_MessageNotInflight()
    {
        var m = SqsErrorMapping.FromAmqp(AmqpErrorKind.LockLost, null, "DeleteMessage");
        Assert.Equal("MessageNotInflight", m.Code);
    }

    [Fact]
    public void FromAmqp_EntityDisabled_maps_to_AccessDenied()
    {
        var m = SqsErrorMapping.FromAmqp(AmqpErrorKind.ServerFatal,
            AmqpErrorCondition.EntityDisabled, "ReceiveMessage");
        Assert.Equal("AccessDenied", m.Code);
        Assert.Equal(403, m.StatusCode);
    }

    [Fact]
    public void FromAmqp_Auth_kind_maps_to_AccessDenied()
    {
        var m = SqsErrorMapping.FromAmqp(AmqpErrorKind.Auth, null, "ReceiveMessage");
        Assert.Equal("AccessDenied", m.Code);
        Assert.Equal(403, m.StatusCode);
    }

    [Fact]
    public void FromAmqp_throttled_maps_to_503()
    {
        var m = SqsErrorMapping.FromAmqp(AmqpErrorKind.Throttled, null, "ReceiveMessage");
        Assert.Equal("ServiceUnavailable", m.Code);
        Assert.Equal(503, m.StatusCode);
    }

    [Fact]
    public void FromAmqp_transient_maps_to_503()
    {
        var m = SqsErrorMapping.FromAmqp(AmqpErrorKind.Transient, null, "ReceiveMessage");
        Assert.Equal("ServiceUnavailable", m.Code);
        Assert.Equal(503, m.StatusCode);
    }

    [Fact]
    public void FromAmqp_ClientFatal_maps_to_InvalidParameterValue()
    {
        var m = SqsErrorMapping.FromAmqp(AmqpErrorKind.ClientFatal, null, "ReceiveMessage");
        Assert.Equal("InvalidParameterValue", m.Code);
        Assert.Equal(400, m.StatusCode);
    }

    [Fact]
    public void FromAmqp_ServerFatal_without_override_maps_to_502()
    {
        var m = SqsErrorMapping.FromAmqp(AmqpErrorKind.ServerFatal, null, "ReceiveMessage");
        Assert.Equal("ServiceUnavailable", m.Code);
        Assert.Equal(502, m.StatusCode);
    }

    [Fact]
    public void FromAmqp_Redirect_maps_to_502()
    {
        var m = SqsErrorMapping.FromAmqp(AmqpErrorKind.Redirect, null, "ReceiveMessage");
        Assert.Equal(502, m.StatusCode);
    }

    [Fact]
    public void FromAmqp_unknown_kind_maps_to_InternalFailure_without_leaking_details()
    {
        var m = SqsErrorMapping.FromAmqp(AmqpErrorKind.Unknown, "weird:condition", "ReceiveMessage");
        Assert.Equal("InternalFailure", m.Code);
        Assert.Equal(500, m.StatusCode);
        Assert.DoesNotContain("weird:condition", m.Message);
    }
}

