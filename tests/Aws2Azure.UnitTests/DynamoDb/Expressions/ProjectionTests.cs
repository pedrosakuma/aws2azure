using System.Collections.Generic;
using System.Text.Json;
using Aws2Azure.Modules.DynamoDb.Expressions;
using Xunit;

namespace Aws2Azure.UnitTests.DynamoDb.Expressions;

/// <summary>
/// Behaviour of the compiled <see cref="Projection"/> engine that backs
/// <c>ProjectionExpression</c> for Query/Scan/BatchGetItem/TransactGetItems.
/// Pins the DynamoDB semantics that clients depend on: whole top-level
/// attributes are kept by reference, projected maps keep only referenced
/// members, projected lists are compacted to referenced indices (ascending),
/// paths that do not exist or type-mismatch are silently omitted, and
/// overlapping paths are rejected with a ValidationException.
/// </summary>
public class ProjectionTests
{
    // --- Top-level (regression parity with the pre-nested behaviour) -------

    [Fact]
    public void TopLevel_single_attribute_kept_by_reference()
    {
        var item = Item("{\"a\":{\"S\":\"x\"},\"b\":{\"N\":\"1\"}}");
        var result = Parse("a").Apply(item);

        Assert.Single(result);
        Assert.Equal("{\"S\":\"x\"}", Raw(result["a"]));
    }

    [Fact]
    public void TopLevel_multiple_attributes()
    {
        var item = Item("{\"a\":{\"S\":\"x\"},\"b\":{\"N\":\"1\"},\"c\":{\"BOOL\":true}}");
        var result = Parse("a, c").Apply(item);

        Assert.Equal(2, result.Count);
        Assert.True(result.ContainsKey("a"));
        Assert.True(result.ContainsKey("c"));
        Assert.False(result.ContainsKey("b"));
    }

    [Fact]
    public void TopLevel_missing_attribute_is_omitted()
    {
        var item = Item("{\"a\":{\"S\":\"x\"}}");
        var result = Parse("a, missing").Apply(item);

        Assert.Single(result);
        Assert.True(result.ContainsKey("a"));
    }

    [Fact]
    public void Alias_reference_is_resolved()
    {
        var item = Item("{\"Name\":{\"S\":\"x\"},\"b\":{\"N\":\"1\"}}");
        var names = new Dictionary<string, string> { ["#n"] = "Name" };
        var result = ProjectionExpressionParser.Parse("#n", names).Apply(item);

        Assert.Single(result);
        Assert.True(result.ContainsKey("Name"));
    }

    // --- Nested map --------------------------------------------------------

    [Fact]
    public void Nested_map_member_keeps_only_referenced_member()
    {
        var item = Item("{\"a\":{\"M\":{\"b\":{\"S\":\"x\"},\"c\":{\"S\":\"y\"}}}}");
        var result = Parse("a.b").Apply(item);

        Assert.Single(result);
        AssertJsonEqual("{\"M\":{\"b\":{\"S\":\"x\"}}}", result["a"]);
    }

    [Fact]
    public void Nested_map_two_members_of_same_root()
    {
        var item = Item("{\"a\":{\"M\":{\"b\":{\"S\":\"x\"},\"c\":{\"S\":\"y\"},\"d\":{\"S\":\"z\"}}}}");
        var result = Parse("a.b, a.d").Apply(item);

        Assert.Single(result);
        AssertJsonEqual("{\"M\":{\"b\":{\"S\":\"x\"},\"d\":{\"S\":\"z\"}}}", result["a"]);
    }

    [Fact]
    public void Nested_map_deep_path()
    {
        var item = Item("{\"a\":{\"M\":{\"b\":{\"M\":{\"c\":{\"S\":\"x\"},\"d\":{\"S\":\"y\"}}}}}}");
        var result = Parse("a.b.c").Apply(item);

        AssertJsonEqual("{\"M\":{\"b\":{\"M\":{\"c\":{\"S\":\"x\"}}}}}", result["a"]);
    }

    [Fact]
    public void Nested_map_missing_member_is_omitted()
    {
        var item = Item("{\"a\":{\"M\":{\"b\":{\"S\":\"x\"}}}}");
        var result = Parse("a.zzz").Apply(item);

        Assert.Empty(result);
    }

    [Fact]
    public void Nested_path_on_scalar_type_mismatch_is_omitted()
    {
        // 'a' is a String, so 'a.b' cannot resolve.
        var item = Item("{\"a\":{\"S\":\"x\"}}");
        var result = Parse("a.b").Apply(item);

        Assert.Empty(result);
    }

    // --- Nested list -------------------------------------------------------

    [Fact]
    public void List_index_compacts_to_single_element()
    {
        var item = Item("{\"a\":{\"L\":[{\"S\":\"z0\"},{\"S\":\"z1\"},{\"S\":\"z2\"}]}}");
        var result = Parse("a[0]").Apply(item);

        AssertJsonEqual("{\"L\":[{\"S\":\"z0\"}]}", result["a"]);
    }

    [Fact]
    public void List_multiple_indices_compact_in_ascending_order()
    {
        // Requested out of order; DynamoDB compacts to matched positions in
        // ascending index order (positions are NOT preserved).
        var item = Item("{\"a\":{\"L\":[{\"S\":\"z0\"},{\"S\":\"z1\"},{\"S\":\"z2\"},{\"S\":\"z3\"}]}}");
        var result = Parse("a[2], a[0]").Apply(item);

        AssertJsonEqual("{\"L\":[{\"S\":\"z0\"},{\"S\":\"z2\"}]}", result["a"]);
    }

    [Fact]
    public void List_index_out_of_range_is_omitted()
    {
        var item = Item("{\"a\":{\"L\":[{\"S\":\"z0\"}]}}");
        var result = Parse("a[5]").Apply(item);

        Assert.Empty(result);
    }

    [Fact]
    public void Mixed_map_then_list_index()
    {
        var item = Item("{\"a\":{\"M\":{\"b\":{\"L\":[{\"S\":\"z0\"},{\"S\":\"z1\"}]}}}}");
        var result = Parse("a.b[1]").Apply(item);

        AssertJsonEqual("{\"M\":{\"b\":{\"L\":[{\"S\":\"z1\"}]}}}", result["a"]);
    }

    // --- Overlap rejection -------------------------------------------------

    [Theory]
    [InlineData("a, a.b")]     // longer descends from a terminated node
    [InlineData("a.b, a")]     // shorter terminates where a longer path descends
    [InlineData("a, a")]       // duplicate
    [InlineData("a[0], a")]    // index child vs whole
    [InlineData("a.b, a.b")]   // nested duplicate
    public void Overlapping_paths_are_rejected(string expression)
    {
        var ex = Assert.Throws<ExpressionSyntaxException>(() => Parse(expression));
        Assert.Contains("overlap", ex.Message, System.StringComparison.OrdinalIgnoreCase);
    }

    // --- Metadata ----------------------------------------------------------

    [Fact]
    public void RootNames_are_distinct_and_first_seen_order()
    {
        var p = Parse("b.x, a, b.y, c");
        Assert.Equal(new[] { "b", "a", "c" }, p.RootNames);
    }

    [Fact]
    public void HasNestedPaths_reflects_depth()
    {
        Assert.False(Parse("a, b").HasNestedPaths);
        Assert.True(Parse("a.b").HasNestedPaths);
        Assert.True(Parse("a[0]").HasNestedPaths);
    }

    // --- FromTopLevelNames (index-projection path) -------------------------

    [Fact]
    public void FromTopLevelNames_keeps_only_named_attributes()
    {
        var item = Item("{\"a\":{\"S\":\"x\"},\"b\":{\"N\":\"1\"},\"c\":{\"BOOL\":true}}");
        var result = Projection.FromTopLevelNames(new[] { "a", "c" }).Apply(item);

        Assert.Equal(2, result.Count);
        Assert.True(result.ContainsKey("a"));
        Assert.True(result.ContainsKey("c"));
    }

    [Fact]
    public void FromTopLevelNames_is_not_nested()
    {
        Assert.False(Projection.FromTopLevelNames(new[] { "a" }).HasNestedPaths);
    }

    // --- Helpers -----------------------------------------------------------

    private static Projection Parse(string expression)
        => ProjectionExpressionParser.Parse(expression, names: null);

    private static Dictionary<string, JsonElement> Item(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var map = new Dictionary<string, JsonElement>();
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            map[prop.Name] = prop.Value.Clone();
        }
        return map;
    }

    private static string Raw(JsonElement el) => el.GetRawText();

    private static void AssertJsonEqual(string expected, JsonElement actual)
    {
        using var exp = JsonDocument.Parse(expected);
        Assert.True(JsonDeepEqual(exp.RootElement, actual),
            $"Expected {expected} but got {actual.GetRawText()}");
    }

    private static bool JsonDeepEqual(JsonElement a, JsonElement b)
    {
        if (a.ValueKind != b.ValueKind) return false;
        switch (a.ValueKind)
        {
            case JsonValueKind.Object:
                var aProps = new Dictionary<string, JsonElement>();
                foreach (var p in a.EnumerateObject()) aProps[p.Name] = p.Value;
                var count = 0;
                foreach (var p in b.EnumerateObject())
                {
                    count++;
                    if (!aProps.TryGetValue(p.Name, out var av) || !JsonDeepEqual(av, p.Value))
                        return false;
                }
                return count == aProps.Count;
            case JsonValueKind.Array:
                if (a.GetArrayLength() != b.GetArrayLength()) return false;
                var ae = a.EnumerateArray();
                var be = b.EnumerateArray();
                while (ae.MoveNext() && be.MoveNext())
                {
                    if (!JsonDeepEqual(ae.Current, be.Current)) return false;
                }
                return true;
            default:
                return a.GetRawText() == b.GetRawText();
        }
    }
}
