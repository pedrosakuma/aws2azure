using Aws2Azure.Modules.Sns.Operations;

namespace Aws2Azure.UnitTests.Sns;

public sealed class SnsSubscriptionFilterSupportTests
{
    [Fact]
    public void TryBuildRuleDescription_compiles_message_attribute_exact_match()
    {
        var metadata = new SnsSubscriptionMetadata
        {
            FilterPolicyJson = "{\"tenant\":[\"blue\"]}",
            FilterPolicyScope = SnsSubscriptionMetadata.MessageAttributesScope,
        };

        var success = SnsSubscriptionFilterSupport.TryBuildRuleDescription(metadata, out var rule, out var error);

        Assert.True(success, error);
        Assert.Equal(SnsSubscriptionFilterSupport.CustomRuleName, rule.RuleName);
        Assert.Contains("aws2azure_sns_attr_74656e616e74 = 'blue'", rule.SqlExpression);
        Assert.Contains("aws2azure_sns_attr_74656e616e74_arr = true", rule.SqlExpression);
        Assert.Contains("aws2azure_sns_attr_74656e616e74 LIKE '%\"blue\"%'", rule.SqlExpression);
    }

    [Fact]
    public void TryBuildRuleDescription_adds_string_array_fallback_for_message_attribute_exact_match()
    {
        var metadata = new SnsSubscriptionMetadata
        {
            FilterPolicyJson = "{\"sports\":[\"rugby\"]}",
            FilterPolicyScope = SnsSubscriptionMetadata.MessageAttributesScope,
        };

        var success = SnsSubscriptionFilterSupport.TryBuildRuleDescription(metadata, out var rule, out var error);

        Assert.True(success, error);
        Assert.Contains("aws2azure_sns_attr_73706f727473 = 'rugby'", rule.SqlExpression);
        Assert.Contains("aws2azure_sns_attr_73706f727473_arr = true", rule.SqlExpression);
        Assert.Contains("aws2azure_sns_attr_73706f727473 LIKE '%\"rugby\"%'", rule.SqlExpression);
    }

    [Fact]
    public void TryBuildRuleDescription_guards_prefix_and_anything_but_array_fallbacks()
    {
        var metadata = new SnsSubscriptionMetadata
        {
            FilterPolicyJson = "{\"tenant\":[{\"prefix\":\"bl\"},{\"anything-but\":\"green\"}]}",
            FilterPolicyScope = SnsSubscriptionMetadata.MessageAttributesScope,
        };

        var success = SnsSubscriptionFilterSupport.TryBuildRuleDescription(metadata, out var rule, out var error);

        Assert.True(success, error);
        Assert.Contains("aws2azure_sns_attr_74656e616e74 LIKE 'bl%'", rule.SqlExpression);
        Assert.Contains("aws2azure_sns_attr_74656e616e74_arr = true", rule.SqlExpression);
        Assert.Contains("NOT (aws2azure_sns_attr_74656e616e74_arr = true AND aws2azure_sns_attr_74656e616e74 LIKE '%\"green\"%')", rule.SqlExpression);
    }

    [Fact]
    public void TryBuildRuleDescription_compiles_message_body_nested_matchers()
    {
        var metadata = new SnsSubscriptionMetadata
        {
            FilterPolicyJson = "{\"detail\":{\"tenant\":[\"blue\"],\"priority\":[{\"numeric\":[\">=\",5]}]}}",
            FilterPolicyScope = SnsSubscriptionMetadata.MessageBodyScope,
        };

        var success = SnsSubscriptionFilterSupport.TryBuildRuleDescription(metadata, out var rule, out var error);

        Assert.True(success, error);
        Assert.Contains("aws2azure_sns_body_363a64657461696c7c363a74656e616e74 = 'blue'", rule.SqlExpression);
        Assert.Contains("aws2azure_sns_body_363a64657461696c7c383a7072696f72697479 IS NOT NULL", rule.SqlExpression);
        Assert.Contains(">= 5", rule.SqlExpression);
    }

    [Fact]
    public void TryBuildRuleDescription_rejects_unsupported_operator()
    {
        var metadata = new SnsSubscriptionMetadata
        {
            FilterPolicyJson = "{\"detail\":{\"tenant\":[{\"suffix\":\"blue\"}]}}",
            FilterPolicyScope = SnsSubscriptionMetadata.MessageBodyScope,
        };

        var success = SnsSubscriptionFilterSupport.TryBuildRuleDescription(metadata, out _, out var error);

        Assert.False(success);
        Assert.Contains("unsupported operator", error);
    }

    [Fact]
    public void TryBuildRuleDescription_uses_true_filter_when_policy_absent()
    {
        var success = SnsSubscriptionFilterSupport.TryBuildRuleDescription(new SnsSubscriptionMetadata(), out var rule, out var error);

        Assert.True(success, error);
        Assert.Equal(SnsSubscriptionFilterSupport.CustomRuleName, rule.RuleName);
        Assert.Null(rule.SqlExpression);
    }
}
