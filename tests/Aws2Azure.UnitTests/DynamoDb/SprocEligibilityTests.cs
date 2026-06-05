using System.Collections.Generic;
using System.Text.Json;
using Aws2Azure.Modules.DynamoDb.Expressions;
using Aws2Azure.Modules.DynamoDb.Internal;
using Xunit;

namespace Aws2Azure.UnitTests.DynamoDb;

/// <summary>
/// Pins the conservative dispatch gate (#202): the single-item
/// <c>atomicWrite_v2</c> sproc must only be used for the slice of the
/// DynamoDB expression surface its server-side JS executes faithfully.
/// Everything else must be reported ineligible so the caller falls back to
/// the in-process GET → modify → PUT path (Preferred) or fails loud (Required),
/// never running divergent server-side JS.
/// </summary>
public class SprocEligibilityTests
{
    private static JsonElement Val(string json)
        => JsonDocument.Parse(json).RootElement.Clone();

    private static ConditionNode Cond(string expr,
        IReadOnlyDictionary<string, string>? names = null,
        IReadOnlyDictionary<string, JsonElement>? values = null)
        => ConditionExpressionParser.Parse(expr, names, values);

    private static UpdateExpressionAst Upd(string expr,
        IReadOnlyDictionary<string, string>? names = null,
        IReadOnlyDictionary<string, JsonElement>? values = null)
        => UpdateExpressionParser.Parse(expr, names, values);

    // ---- eligible cases -------------------------------------------------

    [Fact]
    public void Null_condition_and_update_is_eligible()
        => Assert.True(SprocEligibility.IsEligible(null, null));

    [Fact]
    public void Scalar_equality_condition_is_eligible()
    {
        var c = Cond("version = :v",
            values: new Dictionary<string, JsonElement> { [":v"] = Val("{\"N\":\"1\"}") });
        Assert.True(SprocEligibility.IsEligible(c, null));
    }

    [Fact]
    public void Attribute_not_exists_condition_is_eligible()
        => Assert.True(SprocEligibility.IsEligible(Cond("attribute_not_exists(pk)"), null));

    [Fact]
    public void Set_arithmetic_and_remove_update_is_eligible()
    {
        var u = Upd("SET counter = counter + :i REMOVE stale",
            values: new Dictionary<string, JsonElement> { [":i"] = Val("{\"N\":\"1\"}") });
        Assert.True(SprocEligibility.IsEligible(null, u));
    }

    [Fact]
    public void If_not_exists_and_list_append_with_native_values_is_eligible()
    {
        var u = Upd("SET v = if_not_exists(v, :z), xs = list_append(xs, :more)",
            values: new Dictionary<string, JsonElement>
            {
                [":z"] = Val("{\"N\":\"0\"}"),
                [":more"] = Val("{\"L\":[{\"S\":\"a\"}]}"),
            });
        Assert.True(SprocEligibility.IsEligible(null, u));
    }

    // ---- ineligible: update clauses ------------------------------------

    [Fact]
    public void Add_clause_is_ineligible()
    {
        var u = Upd("ADD counter :i",
            values: new Dictionary<string, JsonElement> { [":i"] = Val("{\"N\":\"1\"}") });
        Assert.False(SprocEligibility.IsEligible(null, u));
    }

    [Fact]
    public void Delete_clause_is_ineligible()
    {
        var u = Upd("DELETE tags :t",
            values: new Dictionary<string, JsonElement> { [":t"] = Val("{\"SS\":[\"x\"]}") });
        Assert.False(SprocEligibility.IsEligible(null, u));
    }

    [Fact]
    public void List_index_target_path_is_ineligible()
    {
        var u = Upd("SET xs[0] = :v",
            values: new Dictionary<string, JsonElement> { [":v"] = Val("{\"S\":\"a\"}") });
        Assert.False(SprocEligibility.IsEligible(null, u));
    }

    [Fact]
    public void Set_of_string_set_literal_is_ineligible()
    {
        var u = Upd("SET tags = :t",
            values: new Dictionary<string, JsonElement> { [":t"] = Val("{\"SS\":[\"x\",\"y\"]}") });
        Assert.False(SprocEligibility.IsEligible(null, u));
    }

    [Fact]
    public void Set_of_binary_literal_is_ineligible()
    {
        var u = Upd("SET blob = :b",
            values: new Dictionary<string, JsonElement> { [":b"] = Val("{\"B\":\"AQID\"}") });
        Assert.False(SprocEligibility.IsEligible(null, u));
    }

    [Fact]
    public void Set_of_high_precision_number_is_ineligible()
    {
        // 25 significant digits — does not round-trip through an IEEE-754 double.
        var u = Upd("SET big = :n",
            values: new Dictionary<string, JsonElement> { [":n"] = Val("{\"N\":\"1234567890123456789012345\"}") });
        Assert.False(SprocEligibility.IsEligible(null, u));
    }

    [Fact]
    public void Set_of_native_map_with_nested_set_is_ineligible()
    {
        var u = Upd("SET m = :m",
            values: new Dictionary<string, JsonElement>
            {
                [":m"] = Val("{\"M\":{\"ok\":{\"S\":\"x\"},\"bad\":{\"NS\":[\"1\"]}}}"),
            });
        Assert.False(SprocEligibility.IsEligible(null, u));
    }

    // ---- ineligible: conditions ----------------------------------------

    [Fact]
    public void Size_condition_is_ineligible()
    {
        var c = Cond("size(tags) > :n",
            values: new Dictionary<string, JsonElement> { [":n"] = Val("{\"N\":\"0\"}") });
        Assert.False(SprocEligibility.IsEligible(c, null));
    }

    [Fact]
    public void Contains_condition_is_ineligible()
    {
        var c = Cond("contains(tags, :v)",
            values: new Dictionary<string, JsonElement> { [":v"] = Val("{\"S\":\"x\"}") });
        Assert.False(SprocEligibility.IsEligible(c, null));
    }

    [Fact]
    public void Attribute_type_set_tag_is_ineligible()
    {
        var c = Cond("attribute_type(tags, :t)",
            values: new Dictionary<string, JsonElement> { [":t"] = Val("{\"S\":\"SS\"}") });
        Assert.False(SprocEligibility.IsEligible(c, null));
    }

    [Fact]
    public void Attribute_type_string_tag_is_eligible()
    {
        var c = Cond("attribute_type(name, :t)",
            values: new Dictionary<string, JsonElement> { [":t"] = Val("{\"S\":\"S\"}") });
        Assert.True(SprocEligibility.IsEligible(c, null));
    }

    [Fact]
    public void Condition_comparing_against_binary_literal_is_ineligible()
    {
        var c = Cond("blob = :b",
            values: new Dictionary<string, JsonElement> { [":b"] = Val("{\"B\":\"AQID\"}") });
        Assert.False(SprocEligibility.IsEligible(c, null));
    }

    [Fact]
    public void List_index_condition_path_is_ineligible()
    {
        var c = Cond("xs[0] = :v",
            values: new Dictionary<string, JsonElement> { [":v"] = Val("{\"S\":\"a\"}") });
        Assert.False(SprocEligibility.IsEligible(c, null));
    }

    [Fact]
    public void Eligible_condition_but_ineligible_update_is_ineligible()
    {
        var c = Cond("version = :v",
            values: new Dictionary<string, JsonElement> { [":v"] = Val("{\"N\":\"1\"}") });
        var u = Upd("ADD counter :i",
            values: new Dictionary<string, JsonElement> { [":i"] = Val("{\"N\":\"1\"}") });
        Assert.False(SprocEligibility.IsEligible(c, u));
    }
}
