namespace Aws2Azure.Modules.Sns.Operations;

internal static class SnsSubscriptionRuleSupport
{
    public static bool RequiresCustomRule(SnsSubscriptionMetadata metadata)
        => !string.IsNullOrWhiteSpace(metadata.FilterPolicyJson);

    public static bool RequiresDefaultRuleDeletion(SnsSubscriptionMetadata previousMetadata, SnsSubscriptionMetadata targetMetadata)
    {
        ArgumentNullException.ThrowIfNull(previousMetadata);
        ArgumentNullException.ThrowIfNull(targetMetadata);
        return !RequiresCustomRule(previousMetadata) && RequiresCustomRule(targetMetadata);
    }
}
