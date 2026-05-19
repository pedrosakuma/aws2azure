using System.Collections.Generic;
using Aws2Azure.Modules.Sqs.Internal;
using Xunit;

namespace Aws2Azure.UnitTests.Sqs;

public class QueueAttributeTranslatorTests
{
    [Fact]
    public void Standard_queue_translation_round_trips_through_iso_durations()
    {
        var attrs = new Dictionary<string, string>
        {
            ["VisibilityTimeout"] = "45",
            ["MessageRetentionPeriod"] = "3600",
            ["MaximumMessageSize"] = "200000",
            ["DelaySeconds"] = "10",
            ["ReceiveMessageWaitTimeSeconds"] = "5",
        };

        var err = QueueAttributeTranslator.ToServiceBusProperties("q1", attrs, out var props);

        Assert.False(err.IsError);
        Assert.Equal("PT45S", props.LockDuration);
        Assert.Equal("PT1H", props.DefaultMessageTimeToLive);
        Assert.Equal(200000, props.MaxMessageSizeBytes);
        Assert.Equal(10, props.DelaySeconds);
        Assert.Equal(5, props.ReceiveMessageWaitTimeSeconds);
    }

    [Fact]
    public void Fifo_name_triggers_session_and_dedup()
    {
        var err = QueueAttributeTranslator.ToServiceBusProperties("orders.fifo",
            new Dictionary<string, string> { ["FifoQueue"] = "true" }, out var props);
        Assert.False(err.IsError);
        Assert.True(props.RequiresSession);
        Assert.True(props.RequiresDuplicateDetection);
    }

    [Fact]
    public void FifoTrue_on_non_fifo_name_rejected()
    {
        var err = QueueAttributeTranslator.ToServiceBusProperties("plain",
            new Dictionary<string, string> { ["FifoQueue"] = "true" }, out _);
        Assert.True(err.IsError);
        Assert.Equal(QueueAttributeTranslator.QueueAttributeError.UnsupportedFifoConfiguration, err.Kind);
    }

    [Fact]
    public void Unknown_attribute_rejected()
    {
        var err = QueueAttributeTranslator.ToServiceBusProperties("q",
            new Dictionary<string, string> { ["NotARealAttribute"] = "x" }, out _);
        Assert.True(err.IsError);
        Assert.Equal(QueueAttributeTranslator.QueueAttributeError.UnknownAttribute, err.Kind);
    }

    [Fact]
    public void Out_of_range_visibility_timeout_rejected()
    {
        var err = QueueAttributeTranslator.ToServiceBusProperties("q",
            new Dictionary<string, string> { ["VisibilityTimeout"] = "100000" }, out _);
        Assert.True(err.IsError);
        Assert.Equal(QueueAttributeTranslator.QueueAttributeError.InvalidValue, err.Kind);
    }

    [Fact]
    public void To_sqs_emits_defaults_when_props_absent()
    {
        var attrs = QueueAttributeTranslator.ToSqsAttributes(new QueueDescriptionProperties());
        Assert.Equal("30", attrs["VisibilityTimeout"]);
        Assert.Equal("345600", attrs["MessageRetentionPeriod"]);
        Assert.Equal("1048576", attrs["MaximumMessageSize"]);
        Assert.Equal("0", attrs["DelaySeconds"]);
        Assert.Equal("0", attrs["ReceiveMessageWaitTimeSeconds"]);
        Assert.False(attrs.ContainsKey("FifoQueue"));
    }

    [Fact]
    public void To_sqs_surfaces_fifo_when_session_required()
    {
        var props = new QueueDescriptionProperties
        {
            RequiresSession = true,
            RequiresDuplicateDetection = true,
        };
        var attrs = QueueAttributeTranslator.ToSqsAttributes(props);
        Assert.Equal("true", attrs["FifoQueue"]);
        Assert.Equal("true", attrs["ContentBasedDeduplication"]);
    }

    [Fact]
    public void Iso8601_formatter_produces_compact_form()
    {
        Assert.Equal("PT30S", QueueAttributeTranslator.FormatIso8601Seconds(30));
        Assert.Equal("PT1H", QueueAttributeTranslator.FormatIso8601Seconds(3600));
        Assert.Equal("P14D", QueueAttributeTranslator.FormatIso8601Seconds(14 * 24 * 3600));
    }
}
