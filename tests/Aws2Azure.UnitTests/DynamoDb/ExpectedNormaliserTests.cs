using System.Collections.Generic;
using System.Text.Json;
using Aws2Azure.Modules.DynamoDb.Expressions;
using Aws2Azure.Modules.DynamoDb.Operations;
using Xunit;

namespace Aws2Azure.UnitTests.DynamoDb;

public class ExpectedNormaliserTests
{
    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement.Clone();

    private static IReadOnlyDictionary<string, JsonElement> Item(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var d = new Dictionary<string, JsonElement>();
        foreach (var p in doc.RootElement.EnumerateObject()) d[p.Name] = p.Value.Clone();
        return d;
    }

    [Fact]
    public void Null_when_expected_missing() =>
        Assert.Null(ExpectedNormaliser.Build(null, null));

    [Fact]
    public void Exists_true_means_attribute_exists()
    {
        var node = ExpectedNormaliser.Build(
            Json("{\"a\":{\"Exists\":true}}"), null);
        Assert.NotNull(node);
        Assert.True(ConditionEvaluator.Evaluate(node!, Item("{\"a\":{\"S\":\"x\"}}")));
        Assert.False(ConditionEvaluator.Evaluate(node!, Item("{}")));
    }

    [Fact]
    public void Exists_false_means_attribute_not_exists()
    {
        var node = ExpectedNormaliser.Build(
            Json("{\"a\":{\"Exists\":false}}"), null);
        Assert.True(ConditionEvaluator.Evaluate(node!, Item("{}")));
        Assert.False(ConditionEvaluator.Evaluate(node!, Item("{\"a\":{\"S\":\"x\"}}")));
    }

    [Fact]
    public void Implicit_eq_via_value()
    {
        var node = ExpectedNormaliser.Build(
            Json("{\"a\":{\"Value\":{\"S\":\"hello\"}}}"), null);
        Assert.True(ConditionEvaluator.Evaluate(node!, Item("{\"a\":{\"S\":\"hello\"}}")));
        Assert.False(ConditionEvaluator.Evaluate(node!, Item("{\"a\":{\"S\":\"bye\"}}")));
    }

    [Fact]
    public void Explicit_eq_comparison_operator()
    {
        var node = ExpectedNormaliser.Build(
            Json("{\"a\":{\"ComparisonOperator\":\"EQ\",\"AttributeValueList\":[{\"N\":\"5\"}]}}"), null);
        Assert.True(ConditionEvaluator.Evaluate(node!, Item("{\"a\":{\"N\":\"5\"}}")));
    }

    [Fact]
    public void Between_two_values()
    {
        var node = ExpectedNormaliser.Build(
            Json("{\"a\":{\"ComparisonOperator\":\"BETWEEN\",\"AttributeValueList\":[{\"N\":\"1\"},{\"N\":\"10\"}]}}"), null);
        Assert.True(ConditionEvaluator.Evaluate(node!, Item("{\"a\":{\"N\":\"5\"}}")));
        Assert.False(ConditionEvaluator.Evaluate(node!, Item("{\"a\":{\"N\":\"11\"}}")));
    }

    [Fact]
    public void Conditional_operator_or()
    {
        var node = ExpectedNormaliser.Build(
            Json("{\"a\":{\"Value\":{\"S\":\"x\"}},\"b\":{\"Value\":{\"S\":\"y\"}}}"),
            "OR");
        Assert.True(ConditionEvaluator.Evaluate(node!, Item("{\"a\":{\"S\":\"x\"}}")));
        Assert.False(ConditionEvaluator.Evaluate(node!, Item("{\"a\":{\"S\":\"n\"}}")));
    }

    [Fact]
    public void Unsupported_conditional_operator_throws() =>
        Assert.Throws<ExpressionSyntaxException>(() =>
            ExpectedNormaliser.Build(Json("{\"a\":{\"Value\":{\"S\":\"x\"}}}"), "XOR"));
}
