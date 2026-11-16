using Aws2Azure.Modules.Sns.Operations;

namespace Aws2Azure.UnitTests.Sns;

public sealed class SnsFifoPublishSupportTests
{
    [Fact]
    public void Topic_scope_preserves_the_effective_deduplication_id_as_broker_identity()
    {
        var state = NewState(SnsTopicAttributeSupport.TopicFifoThroughputScope, contentBasedDeduplication: false);

        var resolved = SnsFifoPublishSupport.TryResolveBrokerMessageId(
            state,
            "group-a",
            "dedup-1",
            "body",
            out var brokerMessageId,
            out var effectiveDeduplicationId,
            out var error);

        Assert.True(resolved, error);
        Assert.Equal("dedup-1", brokerMessageId);
        Assert.Equal("dedup-1", effectiveDeduplicationId);
    }

    [Fact]
    public void MessageGroup_scope_is_deterministic_and_separates_identical_ids_across_groups()
    {
        var state = NewState(SnsTopicAttributeSupport.MessageGroupFifoThroughputScope, contentBasedDeduplication: false);

        Assert.True(SnsFifoPublishSupport.TryResolveBrokerMessageId(
            state,
            "group-a",
            "shared-dedup",
            "body-a",
            out var firstBrokerMessageId,
            out var firstEffectiveDeduplicationId,
            out var firstError), firstError);
        Assert.True(SnsFifoPublishSupport.TryResolveBrokerMessageId(
            state,
            "group-a",
            "shared-dedup",
            "retry-body",
            out var retryBrokerMessageId,
            out var retryEffectiveDeduplicationId,
            out var retryError), retryError);
        Assert.True(SnsFifoPublishSupport.TryResolveBrokerMessageId(
            state,
            "group-b",
            "shared-dedup",
            "body-b",
            out var secondGroupBrokerMessageId,
            out var secondGroupEffectiveDeduplicationId,
            out var secondGroupError), secondGroupError);

        Assert.Equal(firstBrokerMessageId, retryBrokerMessageId);
        Assert.NotEqual(firstBrokerMessageId, secondGroupBrokerMessageId);
        Assert.Equal(64, firstBrokerMessageId.Length);
        Assert.Equal("shared-dedup", firstEffectiveDeduplicationId);
        Assert.Equal(firstEffectiveDeduplicationId, retryEffectiveDeduplicationId);
        Assert.Equal(firstEffectiveDeduplicationId, secondGroupEffectiveDeduplicationId);
    }

    [Fact]
    public void MessageGroup_scope_applies_to_content_based_deduplication()
    {
        var state = NewState(SnsTopicAttributeSupport.MessageGroupFifoThroughputScope, contentBasedDeduplication: true);

        Assert.True(SnsFifoPublishSupport.TryResolveBrokerMessageId(
            state,
            "group-a",
            null,
            "hello",
            out var firstBrokerMessageId,
            out var firstEffectiveDeduplicationId,
            out var firstError), firstError);
        Assert.True(SnsFifoPublishSupport.TryResolveBrokerMessageId(
            state,
            "group-b",
            null,
            "hello",
            out var secondBrokerMessageId,
            out var secondEffectiveDeduplicationId,
            out var secondError), secondError);

        Assert.NotEqual(firstBrokerMessageId, secondBrokerMessageId);
        Assert.Equal(
            "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824",
            firstEffectiveDeduplicationId);
        Assert.Equal(firstEffectiveDeduplicationId, secondEffectiveDeduplicationId);
    }

    [Theory]
    [InlineData("MessageGroupId", 129, false)]
    [InlineData("MessageDeduplicationId", 129, false)]
    [InlineData("MessageGroupId", 128, true)]
    [InlineData("MessageDeduplicationId", 128, true)]
    public void Fifo_identifiers_enforce_the_aws_length_limit(
        string parameterName,
        int length,
        bool expectedSuccess)
    {
        var state = NewState(SnsTopicAttributeSupport.TopicFifoThroughputScope, contentBasedDeduplication: false);
        var groupId = parameterName == "MessageGroupId" ? new string('g', length) : "group";
        var deduplicationId = parameterName == "MessageDeduplicationId" ? new string('d', length) : "dedup";

        var resolved = SnsFifoPublishSupport.TryResolveBrokerMessageId(
            state,
            groupId,
            deduplicationId,
            "body",
            out _,
            out _,
            out var error);

        Assert.Equal(expectedSuccess, resolved);
        if (!expectedSuccess)
        {
            Assert.Contains(parameterName, error);
            Assert.Contains("128", error);
        }
    }

    [Theory]
    [InlineData("group id", "dedup", "MessageGroupId")]
    [InlineData("group", "dedup id", "MessageDeduplicationId")]
    [InlineData("grüup", "dedup", "MessageGroupId")]
    [InlineData("group", "dédüp", "MessageDeduplicationId")]
    public void Fifo_identifiers_reject_characters_outside_the_aws_ascii_set(
        string groupId,
        string deduplicationId,
        string expectedParameter)
    {
        var state = NewState(SnsTopicAttributeSupport.TopicFifoThroughputScope, contentBasedDeduplication: false);

        var resolved = SnsFifoPublishSupport.TryResolveBrokerMessageId(
            state,
            groupId,
            deduplicationId,
            "body",
            out _,
            out _,
            out var error);

        Assert.False(resolved);
        Assert.Contains(expectedParameter, error);
        Assert.Contains("ASCII", error);
    }

    private static ServiceBusFifoTopicState NewState(
        string scope,
        bool contentBasedDeduplication)
        => new(
            IsFifoTopic: true,
            RequiresDuplicateDetection: true,
            ContentBasedDeduplication: contentBasedDeduplication,
            FifoThroughputScope: scope);
}
