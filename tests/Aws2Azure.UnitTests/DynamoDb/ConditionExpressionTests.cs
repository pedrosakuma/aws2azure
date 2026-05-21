using System.Collections.Generic;
using System.Text.Json;
using Aws2Azure.Modules.DynamoDb.Expressions;
using Xunit;

namespace Aws2Azure.UnitTests.DynamoDb;

public class ConditionExpressionTests
{
    private static JsonElement V(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static IReadOnlyDictionary<string, JsonElement> Item(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var d = new Dictionary<string, JsonElement>();
        foreach (var p in doc.RootElement.EnumerateObject()) d[p.Name] = p.Value.Clone();
        return d;
    }

    private static bool Eval(string expr, string item,
        IReadOnlyDictionary<string, string>? names = null,
        IReadOnlyDictionary<string, JsonElement>? values = null)
    {
        var node = ConditionExpressionParser.Parse(expr, names, values);
        return ConditionEvaluator.Evaluate(node, Item(item));
    }

    [Fact]
    public void Compare_eq_string() =>
        Assert.True(Eval("a = :v", "{\"a\":{\"S\":\"x\"}}",
            values: new Dictionary<string, JsonElement> { [":v"] = V("{\"S\":\"x\"}") }));

    [Fact]
    public void Compare_ne_when_missing_is_false() =>
        Assert.False(Eval("a <> :v", "{}",
            values: new Dictionary<string, JsonElement> { [":v"] = V("{\"S\":\"x\"}") }));

    [Fact]
    public void Compare_numeric_ordering() =>
        Assert.True(Eval("a < :v", "{\"a\":{\"N\":\"3\"}}",
            values: new Dictionary<string, JsonElement> { [":v"] = V("{\"N\":\"10\"}") }));

    [Fact]
    public void Attribute_exists_true() =>
        Assert.True(Eval("attribute_exists(a)", "{\"a\":{\"S\":\"x\"}}"));

    [Fact]
    public void Attribute_not_exists_on_missing() =>
        Assert.True(Eval("attribute_not_exists(a)", "{}"));

    [Fact]
    public void Begins_with_string() =>
        Assert.True(Eval("begins_with(a, :p)", "{\"a\":{\"S\":\"hello world\"}}",
            values: new Dictionary<string, JsonElement> { [":p"] = V("{\"S\":\"hello\"}") }));

    [Fact]
    public void Contains_string_set() =>
        Assert.True(Eval("contains(tags, :t)", "{\"tags\":{\"SS\":[\"a\",\"b\",\"c\"]}}",
            values: new Dictionary<string, JsonElement> { [":t"] = V("{\"S\":\"b\"}") }));

    [Fact]
    public void Between_numeric_inclusive() =>
        Assert.True(Eval("a BETWEEN :lo AND :hi", "{\"a\":{\"N\":\"5\"}}",
            values: new Dictionary<string, JsonElement>
            {
                [":lo"] = V("{\"N\":\"1\"}"),
                [":hi"] = V("{\"N\":\"5\"}"),
            }));

    [Fact]
    public void In_membership() =>
        Assert.True(Eval("a IN (:x, :y, :z)", "{\"a\":{\"S\":\"y\"}}",
            values: new Dictionary<string, JsonElement>
            {
                [":x"] = V("{\"S\":\"x\"}"),
                [":y"] = V("{\"S\":\"y\"}"),
                [":z"] = V("{\"S\":\"z\"}"),
            }));

    [Fact]
    public void And_or_not_precedence()
    {
        // NOT binds tighter than AND, AND tighter than OR.
        // expr: NOT a = :a OR b = :b AND c = :c
        // => (NOT (a=:a)) OR (b=:b AND c=:c)
        // a=1 so NOT (a=:a) is false; b=2 c=3 so second term true → overall true.
        Assert.True(Eval(
            "NOT a = :a OR b = :b AND c = :c",
            "{\"a\":{\"N\":\"1\"},\"b\":{\"N\":\"2\"},\"c\":{\"N\":\"3\"}}",
            values: new Dictionary<string, JsonElement>
            {
                [":a"] = V("{\"N\":\"1\"}"),
                [":b"] = V("{\"N\":\"2\"}"),
                [":c"] = V("{\"N\":\"3\"}"),
            }));
    }

    [Fact]
    public void Size_function_compares_as_numeric() =>
        Assert.True(Eval("size(a) > :n", "{\"a\":{\"S\":\"hello\"}}",
            values: new Dictionary<string, JsonElement> { [":n"] = V("{\"N\":\"3\"}") }));

    [Fact]
    public void Attribute_type_matches_tag() =>
        Assert.True(Eval("attribute_type(a, :t)", "{\"a\":{\"S\":\"x\"}}",
            values: new Dictionary<string, JsonElement> { [":t"] = V("{\"S\":\"S\"}") }));

    [Fact]
    public void Parens_change_precedence() =>
        Assert.False(Eval(
            "(NOT a = :a OR b = :b) AND c = :c",
            "{\"a\":{\"N\":\"1\"},\"b\":{\"N\":\"99\"},\"c\":{\"N\":\"3\"}}",
            values: new Dictionary<string, JsonElement>
            {
                [":a"] = V("{\"N\":\"1\"}"),
                [":b"] = V("{\"N\":\"2\"}"),
                [":c"] = V("{\"N\":\"3\"}"),
            }));

    [Fact]
    public void Expression_attribute_names_aliased()
    {
        var names = new Dictionary<string, string> { ["#k"] = "with space" };
        var values = new Dictionary<string, JsonElement> { [":v"] = V("{\"S\":\"x\"}") };
        Assert.True(Eval("#k = :v", "{\"with space\":{\"S\":\"x\"}}", names, values));
    }

    [Fact]
    public void Empty_expression_throws() =>
        Assert.Throws<ExpressionSyntaxException>(() =>
            ConditionExpressionParser.Parse("   ", null, null));

    [Fact]
    public void Trailing_tokens_throw() =>
        Assert.Throws<ExpressionSyntaxException>(() =>
            ConditionExpressionParser.Parse("a = :v AND", null,
                new Dictionary<string, JsonElement> { [":v"] = V("{\"S\":\"x\"}") }));

    [Fact]
    public void Type_mismatch_ordered_compare_throws()
    {
        Assert.Throws<ConditionEvaluationException>(() =>
            Eval("a < :v", "{\"a\":{\"S\":\"x\"}}",
                values: new Dictionary<string, JsonElement> { [":v"] = V("{\"N\":\"1\"}") }));
    }

    [Fact]
    public void Eq_across_different_tags_is_false() =>
        Assert.False(Eval("a = :v", "{\"a\":{\"S\":\"1\"}}",
            values: new Dictionary<string, JsonElement> { [":v"] = V("{\"N\":\"1\"}") }));

    [Fact]
    public void Numeric_eq_is_value_based_not_textual() =>
        Assert.True(Eval("a = :v", "{\"a\":{\"N\":\"1\"}}",
            values: new Dictionary<string, JsonElement> { [":v"] = V("{\"N\":\"1.0\"}") }));

    [Fact]
    public void Numeric_eq_with_trailing_zeros_and_sign() =>
        Assert.True(Eval("a = :v", "{\"a\":{\"N\":\"100.00\"}}",
            values: new Dictionary<string, JsonElement> { [":v"] = V("{\"N\":\"+100\"}") }));

    [Fact]
    public void Numeric_ne_distinguishes_high_precision_values() =>
        Assert.True(Eval("a <> :v",
            "{\"a\":{\"N\":\"0.12345678901234567890123456789012345678\"}}",
            values: new Dictionary<string, JsonElement>
            {
                [":v"] = V("{\"N\":\"0.12345678901234567890123456789012345677\"}"),
            }));

    [Fact]
    public void Numeric_compare_handles_exponent_form() =>
        Assert.True(Eval("a = :v", "{\"a\":{\"N\":\"1000\"}}",
            values: new Dictionary<string, JsonElement> { [":v"] = V("{\"N\":\"1e3\"}") }));

    [Fact]
    public void Numeric_compare_negative_zero_equals_zero() =>
        Assert.True(Eval("a = :v", "{\"a\":{\"N\":\"-0.00\"}}",
            values: new Dictionary<string, JsonElement> { [":v"] = V("{\"N\":\"0\"}") }));

    [Fact]
    public void Numeric_ordered_high_precision()
    {
        // 38-significant-digit operands beyond System.Decimal capacity must
        // still compare lossless.
        Assert.True(Eval("a < :v",
            "{\"a\":{\"N\":\"0.12345678901234567890123456789012345677\"}}",
            values: new Dictionary<string, JsonElement>
            {
                [":v"] = V("{\"N\":\"0.12345678901234567890123456789012345678\"}"),
            }));
    }
}
