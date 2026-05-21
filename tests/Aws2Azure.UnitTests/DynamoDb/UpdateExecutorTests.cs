using System.Collections.Generic;
using System.Text.Json;
using Aws2Azure.Modules.DynamoDb.Expressions;
using Aws2Azure.Modules.DynamoDb.Operations;
using Xunit;

namespace Aws2Azure.UnitTests.DynamoDb;

/// <summary>
/// Behavioural tests for <see cref="UpdateExecutor"/>. Each case parses
/// a real UpdateExpression and asserts the resulting Cosmos-item-map
/// mutation matches DynamoDB semantics (preserving type tags, set
/// uniqueness, list_append concatenation, if_not_exists fallback,
/// REMOVE no-op on missing, path-overlap rejection).
/// </summary>
public class UpdateExecutorTests
{
    private static JsonElement V(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static Dictionary<string, JsonElement> Item(params (string name, string json)[] attrs)
    {
        var d = new Dictionary<string, JsonElement>();
        foreach (var (n, j) in attrs) d[n] = V(j);
        return d;
    }

    private static UpdateExecutor.ExecutionResult Run(
        string expr,
        Dictionary<string, JsonElement>? item,
        IReadOnlyDictionary<string, JsonElement>? values = null)
    {
        var ast = UpdateExpressionParser.Parse(expr, null, values);
        return UpdateExecutor.Apply(ast, item);
    }

    [Fact]
    public void Set_top_level_creates_attribute_when_missing()
    {
        var item = Item(("pk", "{\"S\":\"k\"}"));
        var r = Run("SET name = :n", item,
            new Dictionary<string, JsonElement> { [":n"] = V("{\"S\":\"bob\"}") });
        Assert.Equal("bob", r.NewItem["name"].GetProperty("S").GetString());
        Assert.Contains("name", r.UpdatedAttributes);
    }

    [Fact]
    public void Set_arithmetic_increments_existing_number()
    {
        var item = Item(("pk", "{\"S\":\"k\"}"), ("counter", "{\"N\":\"5\"}"));
        var r = Run("SET counter = counter + :i", item,
            new Dictionary<string, JsonElement> { [":i"] = V("{\"N\":\"3\"}") });
        Assert.Equal("8", r.NewItem["counter"].GetProperty("N").GetString());
    }

    [Fact]
    public void Set_arithmetic_with_if_not_exists_zero_default_initialises()
    {
        var item = Item(("pk", "{\"S\":\"k\"}"));
        var r = Run("SET counter = if_not_exists(counter, :zero) + :i", item,
            new Dictionary<string, JsonElement>
            {
                [":zero"] = V("{\"N\":\"0\"}"),
                [":i"] = V("{\"N\":\"1\"}"),
            });
        Assert.Equal("1", r.NewItem["counter"].GetProperty("N").GetString());
    }

    [Fact]
    public void Remove_top_level_drops_attribute()
    {
        var item = Item(("pk", "{\"S\":\"k\"}"), ("tmp", "{\"S\":\"x\"}"));
        var r = Run("REMOVE tmp", item);
        Assert.False(r.NewItem.ContainsKey("tmp"));
    }

    [Fact]
    public void Remove_missing_attribute_is_no_op()
    {
        var item = Item(("pk", "{\"S\":\"k\"}"));
        var r = Run("REMOVE absent", item);
        Assert.Single(r.NewItem); // pk only
    }

    [Fact]
    public void Add_initialises_missing_numeric_attribute()
    {
        var item = Item(("pk", "{\"S\":\"k\"}"));
        var r = Run("ADD c :i", item,
            new Dictionary<string, JsonElement> { [":i"] = V("{\"N\":\"7\"}") });
        Assert.Equal("7", r.NewItem["c"].GetProperty("N").GetString());
    }

    [Fact]
    public void Add_unions_string_set_without_duplicates_and_preserves_order()
    {
        var item = Item(("pk", "{\"S\":\"k\"}"), ("tags", "{\"SS\":[\"a\",\"b\"]}"));
        var r = Run("ADD tags :more", item,
            new Dictionary<string, JsonElement> { [":more"] = V("{\"SS\":[\"b\",\"c\"]}") });
        var values = r.NewItem["tags"].GetProperty("SS").EnumerateArray();
        var list = new List<string>();
        foreach (var e in values) list.Add(e.GetString()!);
        Assert.Equal(new[] { "a", "b", "c" }, list);
    }

    [Fact]
    public void Delete_subtracts_set_members_and_removes_empty_set()
    {
        var item = Item(("pk", "{\"S\":\"k\"}"), ("tags", "{\"SS\":[\"a\",\"b\"]}"));
        var r = Run("DELETE tags :all", item,
            new Dictionary<string, JsonElement> { [":all"] = V("{\"SS\":[\"a\",\"b\"]}") });
        Assert.False(r.NewItem.ContainsKey("tags"));
    }

    [Fact]
    public void List_append_concatenates_lists()
    {
        var item = Item(("pk", "{\"S\":\"k\"}"), ("xs", "{\"L\":[{\"N\":\"1\"}]}"));
        var r = Run("SET xs = list_append(xs, :more)", item,
            new Dictionary<string, JsonElement> { [":more"] = V("{\"L\":[{\"N\":\"2\"},{\"N\":\"3\"}]}") });
        var arr = r.NewItem["xs"].GetProperty("L");
        Assert.Equal(3, arr.GetArrayLength());
    }

    [Fact]
    public void If_not_exists_returns_existing_value_when_present()
    {
        var item = Item(("pk", "{\"S\":\"k\"}"), ("status", "{\"S\":\"keep\"}"));
        var r = Run("SET status = if_not_exists(status, :def)", item,
            new Dictionary<string, JsonElement> { [":def"] = V("{\"S\":\"new\"}") });
        Assert.Equal("keep", r.NewItem["status"].GetProperty("S").GetString());
    }

    [Fact]
    public void Path_overlap_is_rejected()
    {
        var item = Item(("pk", "{\"S\":\"k\"}"));
        var ex = Assert.Throws<UpdateValidationException>(() =>
            Run("SET a = :v REMOVE a", item,
                new Dictionary<string, JsonElement> { [":v"] = V("{\"S\":\"x\"}") }));
        Assert.Contains("overlap", ex.Message);
    }

    [Fact]
    public void Arithmetic_requires_numeric_operands()
    {
        var item = Item(("pk", "{\"S\":\"k\"}"), ("s", "{\"S\":\"x\"}"));
        Assert.Throws<UpdateValidationException>(() =>
            Run("SET s = s + :i", item,
                new Dictionary<string, JsonElement> { [":i"] = V("{\"N\":\"1\"}") }));
    }

    [Fact]
    public void Old_image_clone_is_independent_of_mutation()
    {
        var item = Item(("pk", "{\"S\":\"k\"}"), ("v", "{\"N\":\"1\"}"));
        var r = Run("SET v = :v", item,
            new Dictionary<string, JsonElement> { [":v"] = V("{\"N\":\"99\"}") });
        Assert.NotNull(r.OldItem);
        Assert.Equal("1", r.OldItem!["v"].GetProperty("N").GetString());
        Assert.Equal("99", r.NewItem["v"].GetProperty("N").GetString());
    }

    [Fact]
    public void Nested_set_requires_parent_attribute_to_exist()
    {
        var item = Item(("pk", "{\"S\":\"k\"}"));
        Assert.Throws<UpdateValidationException>(() =>
            Run("SET addr.zip = :z", item,
                new Dictionary<string, JsonElement> { [":z"] = V("{\"S\":\"00000\"}") }));
    }

    [Fact]
    public void Decimal_arithmetic_preserves_precision_within_range()
    {
        var item = Item(("pk", "{\"S\":\"k\"}"), ("v", "{\"N\":\"1.5\"}"));
        var r = Run("SET v = v + :d", item,
            new Dictionary<string, JsonElement> { [":d"] = V("{\"N\":\"0.25\"}") });
        Assert.Equal("1.75", r.NewItem["v"].GetProperty("N").GetString());
    }
}
