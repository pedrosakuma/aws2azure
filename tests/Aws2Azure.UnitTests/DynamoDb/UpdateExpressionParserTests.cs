using System.Collections.Generic;
using System.Text.Json;
using Aws2Azure.Modules.DynamoDb.Expressions;
using Xunit;

namespace Aws2Azure.UnitTests.DynamoDb;

/// <summary>
/// Grammar coverage for <see cref="UpdateExpressionParser"/>. Verifies
/// the AST shape returned for happy-path expressions and pins the
/// validation behaviour that callers (boto3, AWS CLI) actually depend
/// on — clause exclusivity, single-+/- per assignment, placeholder
/// resolution, function name case-insensitivity.
/// </summary>
public class UpdateExpressionParserTests
{
    private static JsonElement Val(string json)
        => JsonDocument.Parse(json).RootElement.Clone();

    private static UpdateExpressionAst Parse(string expr,
        IReadOnlyDictionary<string, string>? names = null,
        IReadOnlyDictionary<string, JsonElement>? values = null)
        => UpdateExpressionParser.Parse(expr, names, values);

    [Fact]
    public void Parses_single_set_with_value_ref()
    {
        var ast = Parse("SET name = :n",
            values: new Dictionary<string, JsonElement> { [":n"] = Val("{\"S\":\"bob\"}") });
        Assert.NotNull(ast.Set);
        Assert.Single(ast.Set!.Actions);
        var a = ast.Set.Actions[0];
        Assert.Equal("name", a.Path.Root);
        var v = Assert.IsType<ValueRefOperand>(a.Value);
        Assert.Equal(":n", v.Placeholder);
    }

    [Fact]
    public void Parses_set_with_arithmetic_increment()
    {
        var ast = Parse("SET counter = counter + :i",
            values: new Dictionary<string, JsonElement> { [":i"] = Val("{\"N\":\"1\"}") });
        var arith = Assert.IsType<ArithmeticOperand>(ast.Set!.Actions[0].Value);
        Assert.Equal(ArithmeticOp.Add, arith.Op);
        Assert.IsType<PathOperand>(arith.Left);
        Assert.IsType<ValueRefOperand>(arith.Right);
    }

    [Fact]
    public void Rejects_chained_arithmetic()
    {
        var ex = Assert.Throws<ExpressionSyntaxException>(() =>
            Parse("SET x = a + b + :c",
                values: new Dictionary<string, JsonElement> { [":c"] = Val("{\"N\":\"1\"}") }));
        Assert.Contains("Only one", ex.Message);
    }

    [Fact]
    public void Parses_if_not_exists_case_insensitive()
    {
        var ast = Parse("SET tag = IF_NOT_EXISTS(tag, :def)",
            values: new Dictionary<string, JsonElement> { [":def"] = Val("{\"S\":\"x\"}") });
        Assert.IsType<IfNotExistsOperand>(ast.Set!.Actions[0].Value);
    }

    [Fact]
    public void Parses_list_append()
    {
        var ast = Parse("SET items = list_append(items, :more)",
            values: new Dictionary<string, JsonElement>
            {
                [":more"] = Val("{\"L\":[{\"S\":\"x\"}]}"),
            });
        Assert.IsType<ListAppendOperand>(ast.Set!.Actions[0].Value);
    }

    [Fact]
    public void Parses_combined_set_remove_add_delete()
    {
        var ast = Parse("SET a = :a REMOVE b ADD counter :i DELETE tags :t",
            values: new Dictionary<string, JsonElement>
            {
                [":a"] = Val("{\"S\":\"v\"}"),
                [":i"] = Val("{\"N\":\"1\"}"),
                [":t"] = Val("{\"SS\":[\"old\"]}"),
            });
        Assert.NotNull(ast.Set);
        Assert.NotNull(ast.Remove);
        Assert.NotNull(ast.Add);
        Assert.NotNull(ast.Delete);
        Assert.Single(ast.Set!.Actions);
        Assert.Single(ast.Remove!.Paths);
        Assert.Single(ast.Add!.Actions);
        Assert.Single(ast.Delete!.Actions);
    }

    [Fact]
    public void Rejects_undefined_value_placeholder()
    {
        var ex = Assert.Throws<ExpressionSyntaxException>(() =>
            Parse("SET a = :missing"));
        Assert.Contains(":missing", ex.Message);
    }

    [Fact]
    public void Rejects_undefined_name_placeholder()
    {
        var ex = Assert.Throws<ExpressionSyntaxException>(() =>
            Parse("SET #x = :v",
                values: new Dictionary<string, JsonElement> { [":v"] = Val("{\"S\":\"y\"}") }));
        Assert.Contains("#x", ex.Message);
    }

    [Fact]
    public void Resolves_name_placeholder_into_attribute_segment()
    {
        var ast = Parse("SET #status.code = :v",
            names: new Dictionary<string, string> { ["#status"] = "Status" },
            values: new Dictionary<string, JsonElement> { [":v"] = Val("{\"N\":\"7\"}") });
        var path = ast.Set!.Actions[0].Path;
        Assert.Equal("Status", path.Root);
        Assert.False(path.IsTopLevel);
    }

    [Fact]
    public void Rejects_empty_expression()
    {
        Assert.Throws<ExpressionSyntaxException>(() => Parse("   "));
    }

    [Fact]
    public void Parses_nested_path_with_index()
    {
        var ast = Parse("SET items[2].name = :v",
            values: new Dictionary<string, JsonElement> { [":v"] = Val("{\"S\":\"x\"}") });
        var p = ast.Set!.Actions[0].Path;
        Assert.Equal(3, p.Segments.Count);
        Assert.Equal("items[2].name", p.Display);
    }

    [Fact]
    public void Add_requires_value_ref_operand()
    {
        Assert.Throws<ExpressionSyntaxException>(() =>
            Parse("ADD a b"));
    }
}
