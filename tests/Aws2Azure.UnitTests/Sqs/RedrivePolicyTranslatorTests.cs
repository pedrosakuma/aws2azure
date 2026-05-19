using System.Collections.Generic;
using Aws2Azure.Modules.Sqs.Internal;
using Xunit;

namespace Aws2Azure.UnitTests.Sqs;

public class RedrivePolicyTranslatorTests
{
    [Fact]
    public void RedrivePolicy_round_trips_through_queue_description()
    {
        var attrs = new Dictionary<string, string>
        {
            ["RedrivePolicy"] =
                "{\"deadLetterTargetArn\":\"arn:aws:sqs:us-east-1:000000000000:my-dlq\",\"maxReceiveCount\":5}",
        };

        var err = QueueAttributeTranslator.ToServiceBusProperties("source", attrs, out var props);

        Assert.False(err.IsError);
        Assert.Equal("my-dlq", props.ForwardDeadLetteredMessagesTo);
        Assert.Equal(5, props.MaxDeliveryCount);

        var sqsRound = QueueAttributeTranslator.ToSqsAttributes(props);
        Assert.True(sqsRound.TryGetValue("RedrivePolicy", out var json));
        Assert.Contains("\"maxReceiveCount\":5", json);
        Assert.Contains(":my-dlq\"", json);
    }

    [Fact]
    public void RedrivePolicy_accepts_string_encoded_max_receive_count()
    {
        var attrs = new Dictionary<string, string>
        {
            ["RedrivePolicy"] =
                "{\"deadLetterTargetArn\":\"arn:aws:sqs:us-east-1:000000000000:dlq\",\"maxReceiveCount\":\"7\"}",
        };

        var err = QueueAttributeTranslator.ToServiceBusProperties("source", attrs, out var props);

        Assert.False(err.IsError);
        Assert.Equal(7, props.MaxDeliveryCount);
    }

    [Fact]
    public void RedrivePolicy_rejects_missing_arn()
    {
        var err = QueueAttributeTranslator.ToServiceBusProperties("source",
            new Dictionary<string, string> { ["RedrivePolicy"] = "{\"maxReceiveCount\":3}" }, out _);
        Assert.True(err.IsError);
        Assert.Equal("RedrivePolicy", err.AttributeName);
    }

    [Fact]
    public void RedrivePolicy_rejects_missing_max_receive_count()
    {
        var err = QueueAttributeTranslator.ToServiceBusProperties("source",
            new Dictionary<string, string>
            {
                ["RedrivePolicy"] = "{\"deadLetterTargetArn\":\"arn:aws:sqs:us-east-1:0:dlq\"}",
            }, out _);
        Assert.True(err.IsError);
        Assert.Equal("RedrivePolicy", err.AttributeName);
    }

    [Fact]
    public void RedrivePolicy_rejects_out_of_range_max_receive_count()
    {
        var err = QueueAttributeTranslator.ToServiceBusProperties("source",
            new Dictionary<string, string>
            {
                ["RedrivePolicy"] =
                    "{\"deadLetterTargetArn\":\"arn:aws:sqs:us-east-1:0:dlq\",\"maxReceiveCount\":0}",
            }, out _);
        Assert.True(err.IsError);
    }

    [Fact]
    public void RedrivePolicy_rejects_malformed_arn()
    {
        var err = QueueAttributeTranslator.ToServiceBusProperties("source",
            new Dictionary<string, string>
            {
                ["RedrivePolicy"] =
                    "{\"deadLetterTargetArn\":\"not-an-arn\",\"maxReceiveCount\":3}",
            }, out _);
        Assert.True(err.IsError);
    }

    [Fact]
    public void RedrivePolicy_rejects_arn_with_wrong_service()
    {
        var err = QueueAttributeTranslator.ToServiceBusProperties("source",
            new Dictionary<string, string>
            {
                ["RedrivePolicy"] =
                    "{\"deadLetterTargetArn\":\"arn:aws:sns:us-east-1:0:topic\",\"maxReceiveCount\":3}",
            }, out _);
        Assert.True(err.IsError);
    }

    [Fact]
    public void RedrivePolicy_rejects_arn_with_too_few_segments()
    {
        var err = QueueAttributeTranslator.ToServiceBusProperties("source",
            new Dictionary<string, string>
            {
                ["RedrivePolicy"] =
                    "{\"deadLetterTargetArn\":\"not:sqs:dlq\",\"maxReceiveCount\":3}",
            }, out _);
        Assert.True(err.IsError);
    }

    [Fact]
    public void RedrivePolicy_rejects_fifo_source_targeting_standard_dlq()
    {
        var err = QueueAttributeTranslator.ToServiceBusProperties("orders.fifo",
            new Dictionary<string, string>
            {
                ["FifoQueue"] = "true",
                ["RedrivePolicy"] =
                    "{\"deadLetterTargetArn\":\"arn:aws:sqs:us-east-1:0:plain-dlq\",\"maxReceiveCount\":3}",
            }, out _);
        Assert.True(err.IsError);
        Assert.Contains("FIFO", err.Message);
    }

    [Fact]
    public void RedrivePolicy_rejects_standard_source_targeting_fifo_dlq()
    {
        var err = QueueAttributeTranslator.ToServiceBusProperties("plain",
            new Dictionary<string, string>
            {
                ["RedrivePolicy"] =
                    "{\"deadLetterTargetArn\":\"arn:aws:sqs:us-east-1:0:dlq.fifo\",\"maxReceiveCount\":3}",
            }, out _);
        Assert.True(err.IsError);
    }

    [Fact]
    public void RedrivePolicy_accepts_fifo_source_with_fifo_dlq()
    {
        var err = QueueAttributeTranslator.ToServiceBusProperties("orders.fifo",
            new Dictionary<string, string>
            {
                ["FifoQueue"] = "true",
                ["RedrivePolicy"] =
                    "{\"deadLetterTargetArn\":\"arn:aws:sqs:us-east-1:0:dlq.fifo\",\"maxReceiveCount\":3}",
            }, out var props);
        Assert.False(err.IsError);
        Assert.Equal("dlq.fifo", props.ForwardDeadLetteredMessagesTo);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("{}")]
    [InlineData("\"\"")]
    public void RedrivePolicy_empty_marker_signals_clear(string marker)
    {
        var err = QueueAttributeTranslator.ToServiceBusProperties("source",
            new Dictionary<string, string> { ["RedrivePolicy"] = marker }, out var props);
        Assert.False(err.IsError);
        Assert.True(props.ClearDeadLetter);
        Assert.Null(props.ForwardDeadLetteredMessagesTo);
    }

    [Fact]
    public void Merge_clear_flag_unsets_existing_dlq()
    {
        var existing = new QueueDescriptionProperties
        {
            ForwardDeadLetteredMessagesTo = "old",
            MaxDeliveryCount = 4,
        };
        var patch = new QueueDescriptionProperties { ClearDeadLetter = true };
        var merged = QueueAttributeTranslator.Merge(existing, patch);
        Assert.Null(merged.ForwardDeadLetteredMessagesTo);
        Assert.Null(merged.MaxDeliveryCount);
    }

    [Fact]
    public void RedrivePolicy_rejects_invalid_queue_name_in_arn()
    {
        var err = QueueAttributeTranslator.ToServiceBusProperties("source",
            new Dictionary<string, string>
            {
                ["RedrivePolicy"] =
                    "{\"deadLetterTargetArn\":\"arn:aws:sqs:us-east-1:0:!!\",\"maxReceiveCount\":3}",
            }, out _);
        Assert.True(err.IsError);
    }

    [Fact]
    public void Merge_passes_dlq_fields_through_when_patch_is_empty()
    {
        var existing = new QueueDescriptionProperties
        {
            ForwardDeadLetteredMessagesTo = "dlq",
            MaxDeliveryCount = 4,
        };
        var patch = new QueueDescriptionProperties();
        var merged = QueueAttributeTranslator.Merge(existing, patch);
        Assert.Equal("dlq", merged.ForwardDeadLetteredMessagesTo);
        Assert.Equal(4, merged.MaxDeliveryCount);
    }

    [Fact]
    public void Merge_prefers_patch_dlq_fields_when_set()
    {
        var existing = new QueueDescriptionProperties
        {
            ForwardDeadLetteredMessagesTo = "old",
            MaxDeliveryCount = 4,
        };
        var patch = new QueueDescriptionProperties
        {
            ForwardDeadLetteredMessagesTo = "new",
            MaxDeliveryCount = 10,
        };
        var merged = QueueAttributeTranslator.Merge(existing, patch);
        Assert.Equal("new", merged.ForwardDeadLetteredMessagesTo);
        Assert.Equal(10, merged.MaxDeliveryCount);
    }
}
