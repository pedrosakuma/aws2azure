namespace Aws2Azure.Modules.Sns;

/// <summary>
/// SNS operations recognised by the Slice-1 AWS Query parser.
/// Everything outside this set resolves to <see cref="Unknown"/> and
/// surfaces as <c>InvalidAction</c>.
/// </summary>
public enum SnsOperation
{
    Unknown = 0,
    CreateTopic,
    DeleteTopic,
    ListTopics,
    Publish,
    PublishBatch,
    Subscribe,
    Unsubscribe,
    ListSubscriptions,
    ListSubscriptionsByTopic,
    GetTopicAttributes,
    SetTopicAttributes,
}
