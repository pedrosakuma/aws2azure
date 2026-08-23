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
    public void Attribute_type_map_tag_is_ineligible()
    {
        // "M" collides with _a2a:* envelope objects (sets/binary/high-precision
        // N), which the sproc's checkAttrType would also report as "M".
        var c = Cond("attribute_type(payload, :t)",
            values: new Dictionary<string, JsonElement> { [":t"] = Val("{\"S\":\"M\"}") });
        Assert.False(SprocEligibility.IsEligible(c, null));
    }

    [Fact]
    public void Attribute_type_number_tag_is_ineligible()
    {
        // "N": high-precision numbers are stored as {"_a2a:N":...} envelope
        // objects, which checkAttrType would not report as "N".
        var c = Cond("attribute_type(count, :t)",
            values: new Dictionary<string, JsonElement> { [":t"] = Val("{\"S\":\"N\"}") });
        Assert.False(SprocEligibility.IsEligible(c, null));
    }

    [Fact]
    public void Condition_on_root_id_attribute_is_ineligible()
    {
        // A user attribute named "id" is shadow-encoded as "_a2a$id" in storage;
        // the sproc would operate on the raw Cosmos routing field instead.
        var c = Cond("#i = :v",
            names: new Dictionary<string, string> { ["#i"] = "id" },
            values: new Dictionary<string, JsonElement> { [":v"] = Val("{\"S\":\"x\"}") });
        Assert.False(SprocEligibility.IsEligible(c, null));
    }

    [Fact]
    public void Update_on_root_id_attribute_is_ineligible()
    {
        var u = Upd("SET #i = :v",
            names: new Dictionary<string, string> { ["#i"] = "id" },
            values: new Dictionary<string, JsonElement> { [":v"] = Val("{\"S\":\"x\"}") });
        Assert.False(SprocEligibility.IsEligible(null, u));
    }

    [Fact]
    public void Condition_on_reserved_namespace_attribute_is_ineligible()
    {
        var c = Cond("#r = :v",
            names: new Dictionary<string, string> { ["#r"] = "_a2a" },
            values: new Dictionary<string, JsonElement> { [":v"] = Val("{\"S\":\"x\"}") });
        Assert.False(SprocEligibility.IsEligible(c, null));
    }

    [Fact]
    public void Dotted_attribute_name_path_is_ineligible()
    {
        // A literal dot in the attribute name (via ExpressionAttributeNames)
        // would be mis-parsed as a nested path by the dot-splitting sproc.
        var c = Cond("#d = :v",
            names: new Dictionary<string, string> { ["#d"] = "with.dot" },
            values: new Dictionary<string, JsonElement> { [":v"] = Val("{\"S\":\"x\"}") });
        Assert.False(SprocEligibility.IsEligible(c, null));
    }

    [Fact]
    public void Update_on_dotted_attribute_name_is_ineligible()
    {
        var u = Upd("SET #d = :v",
            names: new Dictionary<string, string> { ["#d"] = "with.dot" },
            values: new Dictionary<string, JsonElement> { [":v"] = Val("{\"S\":\"x\"}") });
        Assert.False(SprocEligibility.IsEligible(null, u));
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

    [Fact]
    public void Transaction_condition_subset_accepts_scalar_top_level_composition()
    {
        var condition = Cond(
            "attribute_exists(version) AND version BETWEEN :lo AND :hi AND begins_with(state, :prefix)",
            values: new Dictionary<string, JsonElement>
            {
                [":lo"] = Val("{\"S\":\"1\"}"),
                [":hi"] = Val("{\"S\":\"3\"}"),
                [":prefix"] = Val("{\"S\":\"rea\"}"),
            });

        Assert.True(
            SprocEligibility.TryValidateTransactionCondition(
                condition,
                out var error),
            error);
    }

    [Fact]
    public void Transaction_certification_condition_composition_is_eligible()
    {
        var condition = Cond(
            "(#text = :wrong OR #text = :mango) "
            + "AND NOT (#text = :wrong) "
            + "AND #text BETWEEN :low AND :high "
            + "AND #text IN (:pear, :mango) "
            + "AND attribute_exists(#text) "
            + "AND attribute_not_exists(#missing) "
            + "AND begins_with(#prefix, :prefix) "
            + "AND attribute_type(#text, :typeS) "
            + "AND attribute_type(#flag, :typeBool) "
            + "AND attribute_type(#nil, :typeNull) "
            + "AND #flag = :true "
            + "AND #nil = :null "
            + "AND #count = :seven "
            + "AND #count <> :eight "
            + "AND #count IN (:six, :seven) "
            + "AND :low < #text "
            + "AND :seven = #count",
            names: new Dictionary<string, string>
            {
                ["#text"] = "text",
                ["#missing"] = "missing",
                ["#prefix"] = "prefix",
                ["#flag"] = "flag",
                ["#nil"] = "nil",
                ["#count"] = "count",
            },
            values: new Dictionary<string, JsonElement>
            {
                [":wrong"] = Val("{\"S\":\"wrong\"}"),
                [":mango"] = Val("{\"S\":\"mango\"}"),
                [":low"] = Val("{\"S\":\"apple\"}"),
                [":high"] = Val("{\"S\":\"zebra\"}"),
                [":pear"] = Val("{\"S\":\"pear\"}"),
                [":prefix"] = Val("{\"S\":\"prefix-\"}"),
                [":typeS"] = Val("{\"S\":\"S\"}"),
                [":typeBool"] = Val("{\"S\":\"BOOL\"}"),
                [":typeNull"] = Val("{\"S\":\"NULL\"}"),
                [":true"] = Val("{\"BOOL\":true}"),
                [":null"] = Val("{\"NULL\":true}"),
                [":six"] = Val("{\"N\":\"6\"}"),
                [":seven"] = Val("{\"N\":\"7\"}"),
                [":eight"] = Val("{\"N\":\"8\"}"),
            });

        Assert.True(
            SprocEligibility.TryValidateTransactionCondition(
                condition,
                out var error),
            error);
    }

    [Fact]
    public void Transaction_condition_subset_rejects_ordered_numeric_comparison()
    {
        var condition = Cond(
            "version > :v",
            values: new Dictionary<string, JsonElement>
            {
                [":v"] = Val("{\"N\":\"1\"}"),
            });

        Assert.False(
            SprocEligibility.TryValidateTransactionCondition(
                condition,
                out var error));
        Assert.Contains("strings only", error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("n = :v", "100000000000000000000")]
    [InlineData("n <> :v", "100000000000000000000")]
    [InlineData("n > :v", "100000000000000000000")]
    [InlineData("n = :v", "1e-7")]
    [InlineData("n <> :v", "1e-7")]
    [InlineData("n > :v", "1e-7")]
    [InlineData("n = :v", "0.12345678901234567890123456789012345678")]
    [InlineData("n <> :v", "0.12345678901234567890123456789012345678")]
    [InlineData("n > :v", "0.12345678901234567890123456789012345678")]
    public void Transaction_condition_subset_rejects_numbers_persisted_as_envelopes(
        string expression,
        string number)
    {
        var condition = Cond(
            expression,
            values: new Dictionary<string, JsonElement>
            {
                [":v"] = Val($"{{\"N\":\"{number}\"}}"),
            });

        Assert.False(
            SprocEligibility.TryValidateTransactionCondition(
                condition,
                out _));
    }

    [Fact]
    public void Transaction_condition_subset_accepts_number_when_codec_persists_it_bare()
    {
        var condition = Cond(
            "n = :v",
            values: new Dictionary<string, JsonElement>
            {
                [":v"] = Val("{\"N\":\"1e3\"}"),
            });

        Assert.True(
            SprocEligibility.TryValidateTransactionCondition(
                condition,
                out var error),
            error);
    }

    [Fact]
    public void Transaction_condition_subset_rejects_numeric_between()
    {
        var condition = Cond(
            "version BETWEEN :lo AND :hi",
            values: new Dictionary<string, JsonElement>
            {
                [":lo"] = Val("{\"N\":\"1\"}"),
                [":hi"] = Val("{\"N\":\"3\"}"),
            });

        Assert.False(
            SprocEligibility.TryValidateTransactionCondition(
                condition,
                out var error));
        Assert.Contains("string bounds", error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("m = :v", "{\"M\":{\"x\":{\"S\":\"y\"}}}")]
    [InlineData("xs = :v", "{\"L\":[{\"S\":\"y\"}]}")]
    [InlineData("blob = :v", "{\"B\":\"AQID\"}")]
    [InlineData("tags = :v", "{\"SS\":[\"x\"]}")]
    [InlineData("n = :v", "{\"N\":\"9007199254740993\"}")]
    public void Transaction_condition_subset_rejects_non_scalar_or_unsafe_values(
        string expression,
        string value)
    {
        var condition = Cond(
            expression,
            values: new Dictionary<string, JsonElement>
            {
                [":v"] = Val(value),
            });

        Assert.False(
            SprocEligibility.TryValidateTransactionCondition(
                condition,
                out var error));
        Assert.Contains("scalar", error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("m.x = :v")]
    [InlineData("xs[0] = :v")]
    public void Transaction_condition_subset_rejects_nested_paths(string expression)
    {
        var condition = Cond(
            expression,
            values: new Dictionary<string, JsonElement>
            {
                [":v"] = Val("{\"S\":\"y\"}"),
            });

        Assert.False(
            SprocEligibility.TryValidateTransactionCondition(
                condition,
                out var error));
        Assert.Contains("top-level", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Transaction_condition_subset_rejects_path_to_path_comparison()
    {
        Assert.False(
            SprocEligibility.TryValidateTransactionCondition(
                Cond("left = right"),
                out var error));
        Assert.Contains("scalar", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Transaction_condition_subset_accepts_reversed_scalar_comparison()
    {
        var condition = Cond(
            ":minimum < price",
            values: new Dictionary<string, JsonElement>
            {
                [":minimum"] = Val("{\"S\":\"100\"}"),
            });

        Assert.True(
            SprocEligibility.TryValidateTransactionCondition(
                condition,
                out var error),
            error);
    }

    [Fact]
    public void Transaction_condition_subset_rejects_cosmos_system_field()
    {
        var condition = Cond(
            "attribute_exists(#field)",
            names: new Dictionary<string, string>
            {
                ["#field"] = "_etag",
            });

        Assert.False(
            SprocEligibility.TryValidateTransactionCondition(
                condition,
                out var error));
        Assert.Contains("Cosmos system", error, StringComparison.OrdinalIgnoreCase);
    }

    // ---- transaction Update subset (#798) --------------------------------

    [Fact]
    public void Transaction_update_null_is_rejected()
    {
        Assert.False(
            SprocEligibility.TryValidateTransactionUpdate(null, out var error));
        Assert.Contains("UpdateExpression", error);
    }

    [Fact]
    public void Transaction_update_set_and_remove_is_eligible()
    {
        var update = Upd(
            "SET v = :x REMOVE stale",
            values: new Dictionary<string, JsonElement> { [":x"] = Val("{\"N\":\"1\"}") });

        Assert.True(
            SprocEligibility.TryValidateTransactionUpdate(update, out var error),
            error);
    }

    [Fact]
    public void Transaction_update_set_arithmetic_and_if_not_exists_is_eligible()
    {
        var update = Upd(
            "SET total = total + :delta, note = if_not_exists(note, :fallback)",
            values: new Dictionary<string, JsonElement>
            {
                [":delta"] = Val("{\"N\":\"1\"}"),
                [":fallback"] = Val("{\"S\":\"none\"}"),
            });

        Assert.True(
            SprocEligibility.TryValidateTransactionUpdate(update, out var error),
            error);
    }

    [Fact]
    public void Transaction_update_list_append_is_eligible()
    {
        var update = Upd(
            "SET items = list_append(items, :new)",
            values: new Dictionary<string, JsonElement>
            {
                [":new"] = Val("{\"L\":[{\"S\":\"z\"}]}"),
            });

        Assert.True(
            SprocEligibility.TryValidateTransactionUpdate(update, out var error),
            error);
    }

    [Fact]
    public void Transaction_update_rejects_add_clause()
    {
        var update = Upd(
            "ADD counter :one",
            values: new Dictionary<string, JsonElement> { [":one"] = Val("{\"N\":\"1\"}") });

        Assert.False(
            SprocEligibility.TryValidateTransactionUpdate(update, out var error));
        Assert.Contains("ADD and DELETE", error);
    }

    [Fact]
    public void Transaction_update_rejects_delete_clause()
    {
        var update = Upd(
            "DELETE tags :one",
            values: new Dictionary<string, JsonElement> { [":one"] = Val("{\"SS\":[\"x\"]}") });

        Assert.False(
            SprocEligibility.TryValidateTransactionUpdate(update, out var error));
        Assert.Contains("ADD and DELETE", error);
    }

    [Fact]
    public void Transaction_update_rejects_nested_path()
    {
        var update = Upd(
            "SET a.b = :x",
            values: new Dictionary<string, JsonElement> { [":x"] = Val("{\"N\":\"1\"}") });

        Assert.False(
            SprocEligibility.TryValidateTransactionUpdate(update, out var error));
        Assert.Contains("Update", error);
    }

    [Fact]
    public void Transaction_update_rejects_binary_literal()
    {
        var update = Upd(
            "SET blob = :b",
            values: new Dictionary<string, JsonElement> { [":b"] = Val("{\"B\":\"AQID\"}") });

        Assert.False(
            SprocEligibility.TryValidateTransactionUpdate(update, out var error));
        Assert.Contains("sets, binary", error);
    }

    [Fact]
    public void Transaction_update_rejects_reserved_cosmos_field()
    {
        var update = Upd(
            "SET #field = :x",
            names: new Dictionary<string, string> { ["#field"] = "_etag" },
            values: new Dictionary<string, JsonElement> { [":x"] = Val("{\"S\":\"x\"}") });

        Assert.False(
            SprocEligibility.TryValidateTransactionUpdate(update, out var error));
        Assert.Contains("Cosmos system", error, StringComparison.OrdinalIgnoreCase);
    }
}
