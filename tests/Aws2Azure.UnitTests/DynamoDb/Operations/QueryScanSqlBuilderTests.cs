using System.Collections.Generic;
using System.Text.Json;
using Aws2Azure.Modules.DynamoDb.Expressions;
using Aws2Azure.Modules.DynamoDb.Operations;
using Xunit;

namespace Aws2Azure.UnitTests.DynamoDb.Operations;

public sealed class QueryHandlerSqlBuilderTests
{
    private static FilterPushdownResult Pushdown(
        string expression,
        IReadOnlyDictionary<string, JsonElement> values)
    {
        var ast = ConditionExpressionParser.Parse(expression, null, values);
        return FilterPushdownVisitor.Translate(ast);
    }

    private static JsonElement V(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Fact]
    public void Hash_only_query_without_filter_emits_base_sql()
    {
        var key = new KeyConditionAnalyser.AnalysedKeyCondition("alice", null);
        var (sql, p) = QueryHandler.BuildSql(
            key, forward: true, composite: false,
            new FilterPushdownResult(null, System.Array.Empty<CosmosSqlParameter>(), null));
        Assert.Equal("SELECT * FROM c WHERE c._a2a = 'item'", sql);
        Assert.Empty(p);
    }

    [Fact]
    public void Composite_with_sk_compare_appends_order_by()
    {
        var key = new KeyConditionAnalyser.AnalysedKeyCondition(
            "alice", new KeyConditionAnalyser.SkCompare(">", "2025"));
        var (sql, p) = QueryHandler.BuildSql(
            key, forward: false, composite: true,
            new FilterPushdownResult(null, System.Array.Empty<CosmosSqlParameter>(), null));
        Assert.Equal(
            "SELECT * FROM c WHERE c._a2a = 'item' AND c.id > @sk0 ORDER BY c.id DESC",
            sql);
        Assert.Single(p);
        Assert.Equal("@sk0", p[0].Name);
        Assert.Equal(JsonValueKind.String, p[0].Value.ValueKind);
        Assert.Equal("2025", p[0].Value.GetString());
    }

    [Fact]
    public void Pushdown_filter_is_appended_before_order_by_and_params_merged()
    {
        var key = new KeyConditionAnalyser.AnalysedKeyCondition(
            "alice", new KeyConditionAnalyser.SkBeginsWith("2025-"));
        var values = new Dictionary<string, JsonElement> { [":v"] = V("{\"S\":\"x\"}") };
        var pd = Pushdown("name = :v", values);

        var (sql, p) = QueryHandler.BuildSql(key, true, true, pd);

        Assert.Equal(
            "SELECT * FROM c WHERE c._a2a = 'item' AND STARTSWITH(c.id, @sk0) AND c[\"name\"] = @fp0 ORDER BY c.id ASC",
            sql);
        Assert.Equal(2, p.Count);
        Assert.Equal("@sk0", p[0].Name);
        Assert.Equal("@fp0", p[1].Name);
    }

    [Fact]
    public void Pushdown_only_filter_without_sk_appends_after_item_clause()
    {
        var key = new KeyConditionAnalyser.AnalysedKeyCondition("alice", null);
        var values = new Dictionary<string, JsonElement> { [":v"] = V("{\"S\":\"x\"}") };
        var pd = Pushdown("name = :v", values);

        var (sql, _) = QueryHandler.BuildSql(key, true, false, pd);

        Assert.Equal(
            "SELECT * FROM c WHERE c._a2a = 'item' AND c[\"name\"] = @fp0",
            sql);
    }

    [Fact]
    public void Body_writer_emits_typed_parameter_values()
    {
        var pd = Pushdown(
            "age > :v",
            new Dictionary<string, JsonElement> { [":v"] = V("{\"N\":\"30\"}") });
        using var body = CosmosQueryBody.Build("SELECT * FROM c WHERE " + pd.Sql, pd.Parameters);

        using var doc = JsonDocument.Parse(body.WrittenMemory);
        var paramsArr = doc.RootElement.GetProperty("parameters");
        Assert.Equal(1, paramsArr.GetArrayLength());
        var first = paramsArr[0];
        Assert.Equal("@fp0", first.GetProperty("name").GetString());
        // Numeric, not stringified — Cosmos receives 30 as a JSON number.
        Assert.Equal(JsonValueKind.Number, first.GetProperty("value").ValueKind);
        Assert.Equal(30, first.GetProperty("value").GetInt32());
    }

    [Fact]
    public void Body_writer_preserves_bool_and_null_parameters()
    {
        var pd = Pushdown(
            "active = :a AND deleted = :d",
            new Dictionary<string, JsonElement>
            {
                [":a"] = V("{\"BOOL\":true}"),
                [":d"] = V("{\"NULL\":true}"),
            });
        using var body = CosmosQueryBody.Build("SELECT *", pd.Parameters);

        using var doc = JsonDocument.Parse(body.WrittenMemory);
        var p = doc.RootElement.GetProperty("parameters");
        Assert.Equal(JsonValueKind.True, p[0].GetProperty("value").ValueKind);
        Assert.Equal(JsonValueKind.Null, p[1].GetProperty("value").ValueKind);
    }

    private static FilterPushdownResult Empty() =>
        new(null, System.Array.Empty<CosmosSqlParameter>(), null);

    [Fact]
    public void Lsi_sql_without_override_orders_by_raw_sort_path()
    {
        // #504 flag OFF: raw ORDER BY on the LSI sort attribute, no encoded-field
        // IS_DEFINED guard (legacy items remain visible and ordered as before).
        var (sql, _) = QueryHandler.BuildLsiSql(
            "lsk", forward: true, Empty(), Empty(), orderByPathOverride: null);
        Assert.Equal(
            "SELECT * FROM c WHERE c._a2a = 'item' AND IS_DEFINED(c[\"lsk\"]) ORDER BY c[\"lsk\"] ASC",
            sql);
    }

    [Fact]
    public void Lsi_sql_with_override_orders_by_encoded_field_and_guards_it()
    {
        // #504 flag ON: ORDER BY the encoded order field, plus an IS_DEFINED guard
        // so pre-encoded (legacy) items are excluded rather than mis-ordered.
        var encoded = "c[\"_a2a$ord$lsk\"]";
        var (sql, _) = QueryHandler.BuildLsiSql(
            "lsk", forward: false, Empty(), Empty(), orderByPathOverride: encoded);
        Assert.Equal(
            "SELECT * FROM c WHERE c._a2a = 'item' AND IS_DEFINED(c[\"lsk\"]) "
            + "AND IS_DEFINED(c[\"_a2a$ord$lsk\"]) ORDER BY c[\"_a2a$ord$lsk\"] DESC",
            sql);
    }
}

public sealed class ScanHandlerSqlBuilderTests
{
    private static JsonElement V(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Fact]
    public void Empty_pushdown_emits_base_scan_sql()
    {
        using var body = ScanHandler.BuildScanQueryBody(
            new FilterPushdownResult(null, System.Array.Empty<CosmosSqlParameter>(), null));
        using var doc = JsonDocument.Parse(body.WrittenMemory);
        Assert.Equal(
            "SELECT * FROM c WHERE c._a2a = 'item'",
            doc.RootElement.GetProperty("query").GetString());
        Assert.Equal(0, doc.RootElement.GetProperty("parameters").GetArrayLength());
    }

    [Fact]
    public void Pushdown_filter_appended_with_and()
    {
        var values = new Dictionary<string, JsonElement> { [":v"] = V("{\"S\":\"alice\"}") };
        var ast = ConditionExpressionParser.Parse("name = :v", null, values);
        var pd = FilterPushdownVisitor.Translate(ast);

        using var body = ScanHandler.BuildScanQueryBody(pd);
        using var doc = JsonDocument.Parse(body.WrittenMemory);
        Assert.Equal(
            "SELECT * FROM c WHERE c._a2a = 'item' AND c[\"name\"] = @fp0",
            doc.RootElement.GetProperty("query").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("parameters").GetArrayLength());
    }
}
