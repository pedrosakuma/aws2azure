using System.Collections.Generic;
using System.Text.Json;
using Aws2Azure.Modules.DynamoDb.Expressions;
using Aws2Azure.Modules.DynamoDb.Internal;
using Aws2Azure.Modules.DynamoDb.Operations;
using Xunit;

namespace Aws2Azure.UnitTests.DynamoDb;

/// <summary>
/// Grammar enforcement for <see cref="KeyConditionAnalyser"/>. Pins
/// the strict DynamoDB shape: <c>HASH = :v [AND &lt;sk predicate&gt;]</c>
/// where the sort-key predicate is one of =, &lt;, &lt;=, &gt;, &gt;=,
/// BETWEEN, or begins_with.
/// </summary>
public class KeyConditionAnalyserTests
{
    private static string Hex(string s) =>
        System.Convert.ToHexStringLower(System.Text.Encoding.UTF8.GetBytes(s));

    private static TableMetadata HashOnlyMeta() => new()
    {
        TableName = "t",
        AttributeDefinitions = new List<TableAttributeDefinition>
        {
            new() { Name = "pk", Type = "S" },
        },
        KeySchema = new List<TableKeySchemaElement>
        {
            new() { Name = "pk", KeyType = "HASH" },
        },
    };

    private static TableMetadata CompositeMeta() => new()
    {
        TableName = "t",
        AttributeDefinitions = new List<TableAttributeDefinition>
        {
            new() { Name = "pk", Type = "S" },
            new() { Name = "sk", Type = "S" },
        },
        KeySchema = new List<TableKeySchemaElement>
        {
            new() { Name = "pk", KeyType = "HASH" },
            new() { Name = "sk", KeyType = "RANGE" },
        },
    };

    private static Dictionary<string, JsonElement> Values(params (string Name, string Json)[] pairs)
    {
        var d = new Dictionary<string, JsonElement>();
        foreach (var (n, j) in pairs)
        {
            using var doc = JsonDocument.Parse(j);
            d[n] = doc.RootElement.Clone();
        }
        return d;
    }

    private static KeyConditionAnalyser.AnalysedKeyCondition Analyse(
        string expression, TableMetadata meta,
        Dictionary<string, JsonElement>? values = null,
        Dictionary<string, string>? names = null)
    {
        var ast = ConditionExpressionParser.Parse(expression, names, values);
        return KeyConditionAnalyser.Analyse(ast, meta);
    }

    [Fact]
    public void Hash_only_equality_yields_hash_value()
    {
        var r = Analyse("pk = :v", HashOnlyMeta(), Values((":v", "{\"S\":\"a\"}")));
        Assert.Equal(Hex("a"), r.HashValue);
        Assert.Null(r.Sk);
    }

    [Fact]
    public void Hash_with_sort_equality()
    {
        var r = Analyse("pk = :p AND sk = :s", CompositeMeta(),
            Values((":p", "{\"S\":\"a\"}"), (":s", "{\"S\":\"b\"}")));
        Assert.Equal(Hex("a"), r.HashValue);
        var c = Assert.IsType<KeyConditionAnalyser.SkCompare>(r.Sk);
        Assert.Equal("=", c.Op);
        Assert.Equal(Hex("b"), c.Value);
    }

    [Theory]
    [InlineData("<", "<")]
    [InlineData("<=", "<=")]
    [InlineData(">", ">")]
    [InlineData(">=", ">=")]
    public void Hash_with_sort_relational(string op, string expected)
    {
        var r = Analyse($"pk = :p AND sk {op} :s", CompositeMeta(),
            Values((":p", "{\"S\":\"a\"}"), (":s", "{\"S\":\"b\"}")));
        var c = Assert.IsType<KeyConditionAnalyser.SkCompare>(r.Sk);
        Assert.Equal(expected, c.Op);
    }

    [Fact]
    public void Between_yields_lo_hi()
    {
        var r = Analyse("pk = :p AND sk BETWEEN :lo AND :hi", CompositeMeta(),
            Values((":p", "{\"S\":\"a\"}"), (":lo", "{\"S\":\"b\"}"), (":hi", "{\"S\":\"c\"}")));
        var b = Assert.IsType<KeyConditionAnalyser.SkBetween>(r.Sk);
        Assert.Equal(Hex("b"), b.Lo);
        Assert.Equal(Hex("c"), b.Hi);
    }

    [Fact]
    public void Begins_with_yields_prefix()
    {
        var r = Analyse("pk = :p AND begins_with(sk, :pre)", CompositeMeta(),
            Values((":p", "{\"S\":\"a\"}"), (":pre", "{\"S\":\"ord#\"}")));
        var bw = Assert.IsType<KeyConditionAnalyser.SkBeginsWith>(r.Sk);
        Assert.Equal(Hex("ord#"), bw.Prefix);
    }

    [Fact]
    public void Begins_with_on_number_sort_key_is_rejected()
    {
        var meta = new TableMetadata
        {
            TableName = "t",
            AttributeDefinitions = new List<TableAttributeDefinition>
            {
                new() { Name = "pk", Type = "S" },
                new() { Name = "sk", Type = "N" },
            },
            KeySchema = new List<TableKeySchemaElement>
            {
                new() { Name = "pk", KeyType = "HASH" },
                new() { Name = "sk", KeyType = "RANGE" },
            },
        };

        Assert.Throws<KeyConditionException>(() => Analyse(
            "pk = :p AND begins_with(sk, :pre)", meta,
            Values((":p", "{\"S\":\"a\"}"), (":pre", "{\"N\":\"42\"}"))));
    }

    [Fact]
    public void Or_is_rejected()
    {
        Assert.Throws<KeyConditionException>(() => Analyse("pk = :a OR pk = :b",
            HashOnlyMeta(), Values((":a", "{\"S\":\"a\"}"), (":b", "{\"S\":\"b\"}"))));
    }

    [Fact]
    public void Hash_inequality_is_rejected()
    {
        Assert.Throws<KeyConditionException>(() => Analyse("pk > :v",
            HashOnlyMeta(), Values((":v", "{\"S\":\"a\"}"))));
    }

    [Fact]
    public void Sort_predicate_on_hash_only_table_is_rejected()
    {
        Assert.Throws<KeyConditionException>(() => Analyse("pk = :p AND sk = :s",
            HashOnlyMeta(), Values((":p", "{\"S\":\"a\"}"), (":s", "{\"S\":\"b\"}"))));
    }

    [Fact]
    public void Contains_function_is_rejected_as_sort_predicate()
    {
        Assert.Throws<KeyConditionException>(() => Analyse("pk = :p AND contains(sk, :s)",
            CompositeMeta(), Values((":p", "{\"S\":\"a\"}"), (":s", "{\"S\":\"b\"}"))));
    }

    [Fact]
    public void Non_scalar_key_value_is_rejected()
    {
        Assert.Throws<KeyConditionException>(() => Analyse("pk = :v",
            HashOnlyMeta(), Values((":v", "{\"L\":[]}"))));
    }

    [Fact]
    public void Empty_key_value_is_rejected()
    {
        Assert.Throws<KeyConditionException>(() => Analyse("pk = :v",
            HashOnlyMeta(), Values((":v", "{\"S\":\"\"}"))));
    }

    [Fact]
    public void Numeric_hash_value_is_order_preserving_encoded()
    {
        var meta = new TableMetadata
        {
            TableName = "t",
            AttributeDefinitions = new List<TableAttributeDefinition>
            {
                new() { Name = "id", Type = "N" },
            },
            KeySchema = new List<TableKeySchemaElement>
            {
                new() { Name = "id", KeyType = "HASH" },
            },
        };
        var r = Analyse("id = :v", meta, Values((":v", "{\"N\":\"42\"}")));
        // numeric keys are encoded order-preservingly, not passed through raw.
        Assert.NotEqual("42", r.HashValue);
        Assert.Equal(42, r.HashValue!.Length);
        // numerically-equal forms collapse to the same encoded id.
        var r2 = Analyse("id = :v", meta, Values((":v", "{\"N\":\"42.0\"}")));
        Assert.Equal(r.HashValue, r2.HashValue);
    }

    [Fact]
    public void Key_value_with_mismatched_type_is_rejected()
    {
        // table declares pk as S; query passes {"N":"42"} → reject.
        Assert.Throws<KeyConditionException>(() => Analyse("pk = :v",
            HashOnlyMeta(), Values((":v", "{\"N\":\"42\"}"))));
    }
}
