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
        Assert.Equal(SnsSubscriptionFilterSupport.DefaultRuleName, rule.RuleName);
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
    public void TryBuildRuleDescription_escapes_string_array_fallback_values()
    {
        var metadata = new SnsSubscriptionMetadata
        {
            FilterPolicyJson = "{\"sports\":[\"a\\\"b\"]}",
            FilterPolicyScope = SnsSubscriptionMetadata.MessageAttributesScope,
        };

        var success = SnsSubscriptionFilterSupport.TryBuildRuleDescription(metadata, out var rule, out var error);

        Assert.True(success, error);
        Assert.Contains("aws2azure_sns_attr_73706f727473 LIKE", rule.SqlExpression);
        Assert.DoesNotContain("LIKE '%\"a\"b\"%'", rule.SqlExpression);
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
            FilterPolicyJson = "{\"detail\":{\"tenant\":[{\"unknown-op\":\"blue\"}]}}",
            FilterPolicyScope = SnsSubscriptionMetadata.MessageBodyScope,
        };

        var success = SnsSubscriptionFilterSupport.TryBuildRuleDescription(metadata, out _, out var error);

        Assert.False(success);
        Assert.Contains("unsupported operator", error);
    }

    [Fact]
    public void TryBuildRuleDescription_rejects_anything_but_prefix_form()
    {
        // AWS SNS supports a nested anything-but-prefix form ({"anything-but": {"prefix": "..."}}).
        // This translator only enforces scalar anything-but lists; the nested prefix form is a
        // structurally unsupported subset and is rejected rather than silently ignored.
        var metadata = new SnsSubscriptionMetadata
        {
            FilterPolicyJson = "{\"tenant\":[{\"anything-but\":{\"prefix\":\"tmp-\"}}]}",
            FilterPolicyScope = SnsSubscriptionMetadata.MessageAttributesScope,
        };

        var success = SnsSubscriptionFilterSupport.TryBuildRuleDescription(metadata, out _, out var error);

        Assert.False(success);
        Assert.Contains("anything-but", error);
    }

    [Fact]
    public void TryBuildRuleDescription_compiles_message_attribute_suffix_match()
    {
        var metadata = new SnsSubscriptionMetadata
        {
            FilterPolicyJson = "{\"file\":[{\"suffix\":\".png\"}]}",
            FilterPolicyScope = SnsSubscriptionMetadata.MessageAttributesScope,
        };

        var success = SnsSubscriptionFilterSupport.TryBuildRuleDescription(metadata, out var rule, out var error);

        Assert.True(success, error);
        Assert.Contains("LIKE '%.png'", rule.SqlExpression);
        Assert.Contains("_arr = true AND aws2azure_sns_attr_66696c65 LIKE '%.png\"'", rule.SqlExpression);
    }

    [Fact]
    public void TryBuildRuleDescription_compiles_message_body_suffix_match()
    {
        var metadata = new SnsSubscriptionMetadata
        {
            FilterPolicyJson = "{\"file\":[{\"suffix\":\".png\"}]}",
            FilterPolicyScope = SnsSubscriptionMetadata.MessageBodyScope,
        };

        var success = SnsSubscriptionFilterSupport.TryBuildRuleDescription(metadata, out var rule, out var error);

        Assert.True(success, error);
        Assert.Contains("LIKE '%.png'", rule.SqlExpression);
    }

    [Fact]
    public void TryBuildRuleDescription_compiles_equals_ignore_case_matcher()
    {
        var metadata = new SnsSubscriptionMetadata
        {
            FilterPolicyJson = "{\"tenant\":[{\"equals-ignore-case\":\"Blue\"}]}",
            FilterPolicyScope = SnsSubscriptionMetadata.MessageAttributesScope,
        };

        var success = SnsSubscriptionFilterSupport.TryBuildRuleDescription(metadata, out var rule, out var error);

        Assert.True(success, error);
        Assert.Contains("aws2azure_sns_attr_74656e616e74_ci = 'blue'", rule.SqlExpression);
    }

    [Fact]
    public void TryBuildRuleDescription_rejects_equals_ignore_case_non_string()
    {
        var metadata = new SnsSubscriptionMetadata
        {
            FilterPolicyJson = "{\"tenant\":[{\"equals-ignore-case\":5}]}",
            FilterPolicyScope = SnsSubscriptionMetadata.MessageAttributesScope,
        };

        var success = SnsSubscriptionFilterSupport.TryBuildRuleDescription(metadata, out _, out var error);

        Assert.False(success);
        Assert.Contains("equals-ignore-case", error);
    }

    [Fact]
    public void TryBuildRuleDescription_compiles_cidr_matcher()
    {
        var metadata = new SnsSubscriptionMetadata
        {
            FilterPolicyJson = "{\"clientIp\":[{\"cidr\":\"10.0.0.0/24\"}]}",
            FilterPolicyScope = SnsSubscriptionMetadata.MessageAttributesScope,
        };

        var success = SnsSubscriptionFilterSupport.TryBuildRuleDescription(metadata, out var rule, out var error);

        Assert.True(success, error);
        Assert.Contains("_ip IS NOT NULL", rule.SqlExpression);
        Assert.Contains(">= 167772160", rule.SqlExpression);
        Assert.Contains("<= 167772415", rule.SqlExpression);
    }

    [Fact]
    public void TryBuildRuleDescription_rejects_ipv6_cidr()
    {
        var metadata = new SnsSubscriptionMetadata
        {
            FilterPolicyJson = "{\"clientIp\":[{\"cidr\":\"2001:db8::/32\"}]}",
            FilterPolicyScope = SnsSubscriptionMetadata.MessageAttributesScope,
        };

        var success = SnsSubscriptionFilterSupport.TryBuildRuleDescription(metadata, out _, out var error);

        Assert.False(success);
        Assert.Contains("IPv4 CIDR", error);
    }

    [Fact]
    public void TryBuildRuleDescription_compiles_message_body_array_matching()
    {
        var metadata = new SnsSubscriptionMetadata
        {
            FilterPolicyJson = "{\"tags\":[\"vip\"]}",
            FilterPolicyScope = SnsSubscriptionMetadata.MessageBodyScope,
        };

        var success = SnsSubscriptionFilterSupport.TryBuildRuleDescription(metadata, out var rule, out var error);

        Assert.True(success, error);
        Assert.Contains("aws2azure_sns_body_343a74616773 = 'vip'", rule.SqlExpression);
        Assert.Contains("aws2azure_sns_body_343a74616773_arr = true", rule.SqlExpression);
        Assert.Contains("aws2azure_sns_body_343a74616773 LIKE '%\"vip\"%'", rule.SqlExpression);
    }

    [Fact]
    public void AddFilterProperties_stamps_body_string_array_and_lower_case_companions()
    {
        var applicationProperties = new Dictionary<string, object?>();

        SnsSubscriptionFilterSupport.AddFilterProperties(
            applicationProperties,
            Array.Empty<SnsMessageAttribute>(),
            "{\"tags\":[\"VIP\",\"Gold\"]}");

        Assert.Equal("[\"VIP\",\"Gold\"]", applicationProperties["aws2azure_sns_body_343a74616773"]);
        Assert.Equal(true, applicationProperties["aws2azure_sns_body_343a74616773_arr"]);
        Assert.Equal("[\"vip\",\"gold\"]", applicationProperties["aws2azure_sns_body_343a74616773_ci"]);
    }

    [Fact]
    public void AddFilterProperties_stamps_ip_address_companion_for_dotted_quad_values()
    {
        var applicationProperties = new Dictionary<string, object?>();
        var attributes = new List<SnsMessageAttribute>
        {
            new("clientIp", "String", "10.0.0.5", null),
        };

        SnsSubscriptionFilterSupport.AddFilterProperties(applicationProperties, attributes, string.Empty);

        Assert.Equal(167772165u, applicationProperties["aws2azure_sns_attr_636c69656e744970_ip"]);
    }

    [Fact]
    public void TryBuildRuleDescription_uses_true_filter_when_policy_absent()
    {
        var success = SnsSubscriptionFilterSupport.TryBuildRuleDescription(new SnsSubscriptionMetadata(), out var rule, out var error);

        Assert.True(success, error);
        Assert.Equal(SnsSubscriptionFilterSupport.DefaultRuleName, rule.RuleName);
        Assert.Null(rule.SqlExpression);
    }
}
