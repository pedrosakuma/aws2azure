using System.Collections.Generic;
using System.Text.Json;
using Aws2Azure.Modules.DynamoDb.Expressions;
using Xunit;

namespace Aws2Azure.UnitTests.DynamoDb.Expressions;

public sealed class FilterPushdownVisitorTests
{
    private static FilterPushdownResult Translate(
        string expression,
        IReadOnlyDictionary<string, string>? names = null,
        IReadOnlyDictionary<string, JsonElement>? values = null)
    {
        var ast = ConditionExpressionParser.Parse(expression, names, values);
        return FilterPushdownVisitor.Translate(ast);
    }

    private static JsonElement V(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    // ---------------- empty / trivial -------------------------------

    [Fact]
    public void Null_root_returns_no_sql_no_residual()
    {
        var r = FilterPushdownVisitor.Translate(null);
        Assert.Null(r.Sql);
        Assert.Empty(r.Parameters);
        Assert.Null(r.Residual);
    }

    // ---------------- comparisons (scalar types) --------------------

    [Fact]
    public void String_equality_pushes_direct()
    {
        var values = new Dictionary<string, JsonElement> { [":v"] = V("{\"S\":\"hello\"}") };
        var r = Translate("name = :v", values: values);
        Assert.Equal("c[\"name\"] = @fp0", r.Sql);
        Assert.Single(r.Parameters);
        Assert.Equal("@fp0", r.Parameters[0].Name);
        Assert.Equal("hello", r.Parameters[0].Value.GetString());
        Assert.Null(r.Residual);
    }

    [Fact]
    public void Bool_equality_passes_json_bool_param()
    {
        var values = new Dictionary<string, JsonElement> { [":v"] = V("{\"BOOL\":true}") };
        var r = Translate("active = :v", values: values);
        Assert.Equal("c[\"active\"] = @fp0", r.Sql);
        Assert.Equal(JsonValueKind.True, r.Parameters[0].Value.ValueKind);
    }

    [Fact]
    public void Null_equality_passes_json_null_param()
    {
        var values = new Dictionary<string, JsonElement> { [":v"] = V("{\"NULL\":true}") };
        var r = Translate("deleted = :v", values: values);
        Assert.Equal("c[\"deleted\"] = @fp0", r.Sql);
        Assert.Equal(JsonValueKind.Null, r.Parameters[0].Value.ValueKind);
    }

    [Fact]
    public void Binary_equality_targets_envelope()
    {
        var values = new Dictionary<string, JsonElement> { [":v"] = V("{\"B\":\"AQID\"}") };
        var r = Translate("blob = :v", values: values);
        Assert.Equal("c[\"blob\"][\"_a2a:B\"] = @fp0", r.Sql);
        Assert.Equal("AQID", r.Parameters[0].Value.GetString());
    }

    [Fact]
    public void Numeric_equality_emits_hybrid_with_stringtonumber()
    {
        // Equality is safe on the StringToNumber envelope branch:
        // envelope storage is only used for values that DON'T round-trip
        // as bare JSON numbers, so an envelope-stored value can never
        // exactly equal a round-trippable parameter — there are no
        // false negatives, only (residual-recoverable) false positives.
        var values = new Dictionary<string, JsonElement> { [":v"] = V("{\"N\":\"42\"}") };
        var r = Translate("age = :v", values: values);
        var expected =
            "((IS_NUMBER(c[\"age\"]) AND c[\"age\"] = @fp0)" +
            " OR (IS_DEFINED(c[\"age\"][\"_a2a:N\"]) AND StringToNumber(c[\"age\"][\"_a2a:N\"]) = @fp0))";
        Assert.Equal(expected, r.Sql);
        Assert.Single(r.Parameters);
        Assert.Equal(42, r.Parameters[0].Value.GetInt32());
        Assert.NotNull(r.Residual);
    }

    [Theory]
    [InlineData("age < :v", "<")]
    [InlineData("age <= :v", "<=")]
    [InlineData("age > :v", ">")]
    [InlineData("age >= :v", ">=")]
    public void Numeric_ordered_envelope_branch_uses_is_defined_only(string expression, string sqlOp)
    {
        // gpt-5.5 review (high): ordered numeric comparisons on the
        // envelope branch MUST NOT use StringToNumber. StringToNumber
        // rounds through an IEEE 754 double — e.g. stored
        // 9007199254740995 vs param 9007199254740996 with `<` rounds
        // both to 9007199254740996 and yields false, dropping a row
        // DDB would keep. The envelope branch is widened to
        // IS_DEFINED(envPath) so every envelope-stored row reaches the
        // residual evaluator for exact canonical-string comparison.
        var values = new Dictionary<string, JsonElement> { [":v"] = V("{\"N\":\"42\"}") };
        var r = Translate(expression, values: values);
        var expected =
            $"((IS_NUMBER(c[\"age\"]) AND c[\"age\"] {sqlOp} @fp0)" +
            $" OR IS_DEFINED(c[\"age\"][\"_a2a:N\"]))";
        Assert.Equal(expected, r.Sql);
        Assert.DoesNotContain("StringToNumber", r.Sql);
        Assert.NotNull(r.Residual);
    }

    [Fact]
    public void Numeric_not_equal_is_residual_to_cover_cross_type_rows()
    {
        // DDB `<>` is true when the attribute exists with a different
        // type than the operand. The hybrid number SQL only matches
        // numeric / envelope-numeric storage shapes, so it would drop
        // rows whose `age` is e.g. a string. Pushing it is unsafe.
        var values = new Dictionary<string, JsonElement> { [":v"] = V("{\"N\":\"42\"}") };
        var r = Translate("age <> :v", values: values);
        Assert.Null(r.Sql);
        Assert.Empty(r.Parameters);
        Assert.NotNull(r.Residual);
    }

    [Fact]
    public void Binary_not_equal_is_residual_to_cover_cross_type_rows()
    {
        // Same reasoning as numeric: envelope-targeted SQL drops rows
        // whose attribute is a different type, but DDB keeps them.
        var values = new Dictionary<string, JsonElement> { [":v"] = V("{\"B\":\"AQID\"}") };
        var r = Translate("blob <> :v", values: values);
        Assert.Null(r.Sql);
        Assert.Empty(r.Parameters);
        Assert.NotNull(r.Residual);
    }

    [Theory]
    [InlineData("blob < :v")]
    [InlineData("blob <= :v")]
    [InlineData("blob > :v")]
    [InlineData("blob >= :v")]
    public void Binary_ordered_compare_is_residual(string expression)
    {
        // base64 lexical ordering != underlying byte ordering, so
        // ordered comparisons on B must not push.
        var values = new Dictionary<string, JsonElement> { [":v"] = V("{\"B\":\"AQID\"}") };
        var r = Translate(expression, values: values);
        Assert.Null(r.Sql);
        Assert.Empty(r.Parameters);
        Assert.NotNull(r.Residual);
    }

    [Fact]
    public void Numeric_compare_with_high_precision_value_is_residual()
    {
        // 20-digit integer cannot survive double round-trip → residual.
        var values = new Dictionary<string, JsonElement>
        {
            [":v"] = V("{\"N\":\"12345678901234567890\"}"),
        };
        var r = Translate("count = :v", values: values);
        Assert.Null(r.Sql);
        Assert.Empty(r.Parameters);
        Assert.NotNull(r.Residual);
    }

    [Fact]
    public void Value_op_path_is_pushed_with_flipped_op()
    {
        // ":v < age" → "age > :v"
        var values = new Dictionary<string, JsonElement> { [":v"] = V("{\"N\":\"42\"}") };
        var r = Translate(":v < age", values: values);
        Assert.Contains("c[\"age\"] > @fp0", r.Sql);
    }

    // ---------------- BETWEEN / IN ---------------------------------

    [Fact]
    public void Between_string_emits_inclusive_range()
    {
        var values = new Dictionary<string, JsonElement>
        {
            [":lo"] = V("{\"S\":\"a\"}"),
            [":hi"] = V("{\"S\":\"z\"}"),
        };
        var r = Translate("name BETWEEN :lo AND :hi", values: values);
        Assert.Equal("(c[\"name\"] >= @fp0 AND c[\"name\"] <= @fp1)", r.Sql);
        Assert.Equal(2, r.Parameters.Count);
    }

    [Fact]
    public void Between_numeric_envelope_branch_uses_is_defined_only()
    {
        // gpt-5.5 review (high): BETWEEN is inherently ordered. The
        // envelope branch must NOT use StringToNumber for the same
        // false-negative reason as ordered compares.
        var values = new Dictionary<string, JsonElement>
        {
            [":lo"] = V("{\"N\":\"1\"}"),
            [":hi"] = V("{\"N\":\"10\"}"),
        };
        var r = Translate("age BETWEEN :lo AND :hi", values: values);
        var expected =
            "((IS_NUMBER(c[\"age\"]) AND c[\"age\"] >= @fp0 AND c[\"age\"] <= @fp1)" +
            " OR IS_DEFINED(c[\"age\"][\"_a2a:N\"]))";
        Assert.Equal(expected, r.Sql);
        Assert.DoesNotContain("StringToNumber", r.Sql);
        // Hybrid SQL is a prefilter — keep residual to re-check exact
        // canonical string for envelope-stored values.
        Assert.NotNull(r.Residual);
    }

    [Fact]
    public void Between_binary_is_residual()
    {
        // BETWEEN on B would order by base64 string, which is not
        // equivalent to ordering by the underlying byte sequence.
        var values = new Dictionary<string, JsonElement>
        {
            [":lo"] = V("{\"B\":\"AAA=\"}"),
            [":hi"] = V("{\"B\":\"AQID\"}"),
        };
        var r = Translate("blob BETWEEN :lo AND :hi", values: values);
        Assert.Null(r.Sql);
        Assert.Empty(r.Parameters);
        Assert.NotNull(r.Residual);
    }

    [Fact]
    public void Between_mismatched_types_is_residual()
    {
        var values = new Dictionary<string, JsonElement>
        {
            [":lo"] = V("{\"S\":\"a\"}"),
            [":hi"] = V("{\"N\":\"10\"}"),
        };
        var r = Translate("x BETWEEN :lo AND :hi", values: values);
        Assert.Null(r.Sql);
        Assert.Empty(r.Parameters);
        Assert.NotNull(r.Residual);
    }

    [Fact]
    public void In_string_emits_direct_in()
    {
        var values = new Dictionary<string, JsonElement>
        {
            [":a"] = V("{\"S\":\"a\"}"),
            [":b"] = V("{\"S\":\"b\"}"),
            [":c"] = V("{\"S\":\"c\"}"),
        };
        var r = Translate("name IN (:a, :b, :c)", values: values);
        Assert.Equal("c[\"name\"] IN (@fp0, @fp1, @fp2)", r.Sql);
        Assert.Equal(3, r.Parameters.Count);
    }

    [Fact]
    public void In_numeric_emits_hybrid_branches_sharing_params()
    {
        var values = new Dictionary<string, JsonElement>
        {
            [":a"] = V("{\"N\":\"1\"}"),
            [":b"] = V("{\"N\":\"2\"}"),
        };
        var r = Translate("age IN (:a, :b)", values: values);
        var expected =
            "((IS_NUMBER(c[\"age\"]) AND c[\"age\"] IN (@fp0, @fp1))" +
            " OR (IS_DEFINED(c[\"age\"][\"_a2a:N\"]) AND StringToNumber(c[\"age\"][\"_a2a:N\"]) IN (@fp0, @fp1)))";
        Assert.Equal(expected, r.Sql);
        Assert.Equal(2, r.Parameters.Count);
    }

    // ---------------- functions -------------------------------------

    [Fact]
    public void Attribute_exists_pushes()
    {
        var r = Translate("attribute_exists(name)");
        Assert.Equal("IS_DEFINED(c[\"name\"])", r.Sql);
        Assert.Empty(r.Parameters);
    }

    [Fact]
    public void Attribute_not_exists_pushes()
    {
        var r = Translate("attribute_not_exists(name)");
        Assert.Equal("NOT IS_DEFINED(c[\"name\"])", r.Sql);
    }

    [Theory]
    [InlineData("S", "IS_STRING(c[\"x\"])")]
    [InlineData("BOOL", "IS_BOOL(c[\"x\"])")]
    [InlineData("NULL", "IS_NULL(c[\"x\"])")]
    [InlineData("L", "IS_ARRAY(c[\"x\"])")]
    [InlineData("N", "(IS_NUMBER(c[\"x\"]) OR IS_DEFINED(c[\"x\"][\"_a2a:N\"]))")]
    [InlineData("B", "IS_DEFINED(c[\"x\"][\"_a2a:B\"])")]
    [InlineData("SS", "IS_DEFINED(c[\"x\"][\"_a2a:SS\"])")]
    [InlineData("NS", "IS_DEFINED(c[\"x\"][\"_a2a:NS\"])")]
    [InlineData("BS", "IS_DEFINED(c[\"x\"][\"_a2a:BS\"])")]
    public void Attribute_type_pushes_exact_mappings(string typeTag, string expectedSql)
    {
        var values = new Dictionary<string, JsonElement> { [":t"] = V($"{{\"S\":\"{typeTag}\"}}") };
        var r = Translate("attribute_type(x, :t)", values: values);
        Assert.Equal(expectedSql, r.Sql);
        Assert.Null(r.Residual);
    }

    [Fact]
    public void Attribute_type_map_is_residual()
    {
        // M would need to exclude every envelope shape — leave it
        // for client-side evaluation.
        var values = new Dictionary<string, JsonElement> { [":t"] = V("{\"S\":\"M\"}") };
        var r = Translate("attribute_type(x, :t)", values: values);
        Assert.Null(r.Sql);
        Assert.NotNull(r.Residual);
    }

    [Fact]
    public void Begins_with_string_pushes_case_sensitive()
    {
        var values = new Dictionary<string, JsonElement> { [":p"] = V("{\"S\":\"foo\"}") };
        var r = Translate("begins_with(name, :p)", values: values);
        Assert.Equal("STARTSWITH(c[\"name\"], @fp0, false)", r.Sql);
    }

    [Fact]
    public void Begins_with_binary_is_residual()
    {
        var values = new Dictionary<string, JsonElement> { [":p"] = V("{\"B\":\"AQID\"}") };
        var r = Translate("begins_with(blob, :p)", values: values);
        Assert.Null(r.Sql);
        Assert.NotNull(r.Residual);
    }

    [Fact]
    public void Contains_string_emits_string_or_set_or_list_branches()
    {
        var values = new Dictionary<string, JsonElement> { [":v"] = V("{\"S\":\"hello\"}") };
        var r = Translate("contains(field, :v)", values: values);
        var expected =
            "((IS_STRING(c[\"field\"]) AND CONTAINS(c[\"field\"], @fp0, false))" +
            " OR (IS_ARRAY(c[\"field\"][\"_a2a:SS\"]) AND ARRAY_CONTAINS(c[\"field\"][\"_a2a:SS\"], @fp0))" +
            " OR IS_ARRAY(c[\"field\"]))";
        Assert.Equal(expected, r.Sql);
        Assert.Single(r.Parameters);
        // List members may be envelope-encoded; residual evaluator
        // performs the precise typed comparison.
        Assert.NotNull(r.Residual);
    }

    [Fact]
    public void Contains_number_targets_number_set_envelope_or_list()
    {
        // NS members live as strings inside the envelope.
        var values = new Dictionary<string, JsonElement> { [":v"] = V("{\"N\":\"42\"}") };
        var r = Translate("contains(ids, :v)", values: values);
        Assert.Equal(
            "((IS_ARRAY(c[\"ids\"][\"_a2a:NS\"]) AND ARRAY_CONTAINS(c[\"ids\"][\"_a2a:NS\"], @fp0))" +
            " OR IS_ARRAY(c[\"ids\"]))",
            r.Sql);
        Assert.Equal(JsonValueKind.String, r.Parameters[0].Value.ValueKind);
        Assert.Equal("42", r.Parameters[0].Value.GetString());
        Assert.NotNull(r.Residual);
    }

    [Fact]
    public void Contains_binary_targets_binary_set_envelope_or_list()
    {
        var values = new Dictionary<string, JsonElement> { [":v"] = V("{\"B\":\"AQID\"}") };
        var r = Translate("contains(blobs, :v)", values: values);
        Assert.Equal(
            "((IS_ARRAY(c[\"blobs\"][\"_a2a:BS\"]) AND ARRAY_CONTAINS(c[\"blobs\"][\"_a2a:BS\"], @fp0))" +
            " OR IS_ARRAY(c[\"blobs\"]))",
            r.Sql);
        Assert.NotNull(r.Residual);
    }

    [Fact]
    public void Size_is_residual()
    {
        var values = new Dictionary<string, JsonElement> { [":v"] = V("{\"N\":\"3\"}") };
        var r = Translate("size(name) > :v", values: values);
        Assert.Null(r.Sql);
        Assert.Empty(r.Parameters);
        Assert.NotNull(r.Residual);
    }

    // ---------------- AND / OR / NOT composition --------------------

    [Fact]
    public void And_of_two_pushable_clauses_combines()
    {
        var values = new Dictionary<string, JsonElement>
        {
            [":n"] = V("{\"S\":\"Alice\"}"),
            [":a"] = V("{\"N\":\"30\"}"),
        };
        var r = Translate("name = :n AND age = :a", values: values);
        Assert.NotNull(r.Sql);
        Assert.StartsWith("(", r.Sql);
        Assert.Contains(" AND ", r.Sql);
        // The numeric branch is a prefilter (StringToNumber rounds
        // through a double on envelope-stored values) so its residual
        // bubbles up through the AND. The string branch contributes
        // no residual.
        Assert.NotNull(r.Residual);
        Assert.IsType<CompareCondition>(r.Residual);
        Assert.Equal(2, r.Parameters.Count);
    }

    [Fact]
    public void And_with_one_residual_keeps_pushable_and_residual()
    {
        var values = new Dictionary<string, JsonElement>
        {
            [":n"] = V("{\"S\":\"Alice\"}"),
            [":s"] = V("{\"N\":\"3\"}"),
        };
        var r = Translate("name = :n AND size(tags) > :s", values: values);
        Assert.Equal("c[\"name\"] = @fp0", r.Sql);
        Assert.Single(r.Parameters);
        Assert.NotNull(r.Residual);
        Assert.IsType<CompareCondition>(r.Residual); // size > :s
    }

    [Fact]
    public void And_with_partial_left_and_full_right_rolls_back_left_params_correctly()
    {
        // First clause emits SQL; the size() clause is the residual.
        var values = new Dictionary<string, JsonElement>
        {
            [":a"] = V("{\"N\":\"30\"}"),
            [":s"] = V("{\"N\":\"3\"}"),
        };
        var r = Translate("size(tags) > :s AND age = :a", values: values);
        Assert.NotNull(r.Sql);
        Assert.Contains("c[\"age\"]", r.Sql);
        Assert.Single(r.Parameters);
        Assert.Equal("@fp0", r.Parameters[0].Name);
        Assert.Equal(30, r.Parameters[0].Value.GetInt32());
    }

    [Fact]
    public void Or_with_one_residual_drops_to_residual_and_rolls_back_params()
    {
        // OR cannot be partially pushed; the entire subtree becomes
        // residual and any parameters speculatively bound for the
        // pushable side must be rolled back.
        var values = new Dictionary<string, JsonElement>
        {
            [":n"] = V("{\"S\":\"Alice\"}"),
            [":s"] = V("{\"N\":\"3\"}"),
        };
        var r = Translate("name = :n OR size(tags) > :s", values: values);
        Assert.Null(r.Sql);
        Assert.Empty(r.Parameters); // rolled back
        Assert.NotNull(r.Residual);
        Assert.IsType<OrCondition>(r.Residual);
    }

    [Fact]
    public void Or_of_two_pushable_combines()
    {
        var values = new Dictionary<string, JsonElement>
        {
            [":a"] = V("{\"S\":\"a\"}"),
            [":b"] = V("{\"S\":\"b\"}"),
        };
        var r = Translate("name = :a OR name = :b", values: values);
        Assert.Equal("(c[\"name\"] = @fp0 OR c[\"name\"] = @fp1)", r.Sql);
        Assert.Null(r.Residual);
    }

    [Fact]
    public void Not_of_pushable_pushes_with_wrap()
    {
        var values = new Dictionary<string, JsonElement> { [":n"] = V("{\"S\":\"Alice\"}") };
        var r = Translate("NOT name = :n", values: values);
        Assert.Equal("NOT(c[\"name\"] = @fp0)", r.Sql);
        Assert.Null(r.Residual);
    }

    [Fact]
    public void Not_of_residual_is_residual_rolling_back_params()
    {
        var values = new Dictionary<string, JsonElement> { [":s"] = V("{\"N\":\"3\"}") };
        var r = Translate("NOT size(tags) > :s", values: values);
        Assert.Null(r.Sql);
        Assert.Empty(r.Parameters);
        Assert.NotNull(r.Residual);
        Assert.IsType<NotCondition>(r.Residual);
    }

    // ---------------- expression-attribute-name resolution -----------

    [Fact]
    public void Name_alias_resolves_to_attribute_in_sql_path()
    {
        var names = new Dictionary<string, string> { ["#n"] = "name" };
        var values = new Dictionary<string, JsonElement> { [":v"] = V("{\"S\":\"Alice\"}") };
        var r = Translate("#n = :v", names: names, values: values);
        Assert.Equal("c[\"name\"] = @fp0", r.Sql);
    }

    // ---------------- nested paths ----------------------------------

    [Fact]
    public void Nested_path_translates_to_bracket_chain()
    {
        var values = new Dictionary<string, JsonElement> { [":v"] = V("{\"S\":\"SP\"}") };
        var r = Translate("profile.city = :v", values: values);
        Assert.Equal("c[\"profile\"][\"city\"] = @fp0", r.Sql);
    }

    // ---------------- envelope path-leak guard ----------------------

    [Fact]
    public void Reserved_envelope_segment_via_name_alias_is_not_pushed()
    {
        // A user-controlled #tag aliased to "_a2a:N" would, without
        // this guard, let the caller filter on storage internals via
        // pushed SQL. The visitor must refuse to translate such paths.
        var names = new Dictionary<string, string> { ["#tag"] = "_a2a:N" };
        var values = new Dictionary<string, JsonElement> { [":v"] = V("{\"S\":\"x\"}") };
        var r = Translate("#tag = :v", names: names, values: values);
        Assert.Null(r.Sql);
        Assert.Empty(r.Parameters);
        Assert.NotNull(r.Residual);
    }

    [Fact]
    public void Reserved_envelope_segment_in_nested_path_is_not_pushed()
    {
        // Even an inner segment matching the reserved prefix must
        // refuse pushdown — translator would happily render the path
        // and let the caller probe an envelope.
        var names = new Dictionary<string, string> { ["#tag"] = "_a2a:B" };
        var values = new Dictionary<string, JsonElement> { [":v"] = V("{\"S\":\"x\"}") };
        var r = Translate("profile.#tag = :v", names: names, values: values);
        Assert.Null(r.Sql);
        Assert.Empty(r.Parameters);
        Assert.NotNull(r.Residual);
    }

    [Fact]
    public void Reserved_envelope_segment_blocks_attribute_exists_pushdown()
    {
        var names = new Dictionary<string, string> { ["#tag"] = "_a2a:SS" };
        var r = Translate("attribute_exists(#tag)", names: names);
        Assert.Null(r.Sql);
        Assert.NotNull(r.Residual);
    }
}
