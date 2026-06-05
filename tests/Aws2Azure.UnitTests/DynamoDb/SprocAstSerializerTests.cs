using System.Collections.Generic;
using System.Text.Json;
using Aws2Azure.Modules.DynamoDb.Expressions;
using Aws2Azure.Modules.DynamoDb.Internal;
using Xunit;

namespace Aws2Azure.UnitTests.DynamoDb;

/// <summary>
/// Pins the JSON contract that <see cref="SprocAstSerializer"/> emits for the
/// single-item <c>atomicWrite_v2</c> sproc. SET-value operands are tagged with a
/// <c>$k</c> discriminator so the server-side <c>resolveSetValue</c> can interpret
/// arithmetic / path / if_not_exists / list_append unambiguously (#202). The JS
/// side can only be exercised against real Cosmos, so these tests lock the wire
/// shape that the JS resolver depends on.
/// </summary>
public class SprocAstSerializerTests
{
    private static JsonElement Val(string json)
        => JsonDocument.Parse(json).RootElement.Clone();

    private static UpdateExpressionAst Parse(string expr,
        IReadOnlyDictionary<string, string>? names = null,
        IReadOnlyDictionary<string, JsonElement>? values = null)
        => UpdateExpressionParser.Parse(expr, names, values);

    [Fact]
    public void Literal_set_value_is_wrapped_in_lit_envelope()
    {
        var ast = Parse("SET #n = :v",
            names: new Dictionary<string, string> { ["#n"] = "name" },
            values: new Dictionary<string, JsonElement> { [":v"] = Val("{\"S\":\"bob\"}") });

        var json = SprocAstSerializer.SerializeUpdate(ast)!;

        Assert.Contains("\"set\":[", json);
        Assert.Contains("\"path\":\"name\"", json);
        Assert.Contains("{\"$k\":\"lit\",\"v\":\"bob\"}", json);
    }

    [Fact]
    public void Arithmetic_increment_serializes_as_op_envelope()
    {
        var ast = Parse("SET counter = counter + :i",
            values: new Dictionary<string, JsonElement> { [":i"] = Val("{\"N\":\"1\"}") });

        var json = SprocAstSerializer.SerializeUpdate(ast)!;

        // {"$k":"op","o":"+","l":{"$k":"path","p":"counter"},"r":{"$k":"lit","v":1}}
        Assert.Contains("\"$k\":\"op\"", json);
        Assert.Contains("\"o\":\"+\"", json);
        Assert.Contains("\"l\":{\"$k\":\"path\",\"p\":\"counter\"}", json);
        Assert.Contains("\"r\":{\"$k\":\"lit\",\"v\":1}", json);
    }

    [Fact]
    public void Arithmetic_decrement_uses_minus_operator()
    {
        var ast = Parse("SET counter = counter - :i",
            values: new Dictionary<string, JsonElement> { [":i"] = Val("{\"N\":\"3\"}") });

        var json = SprocAstSerializer.SerializeUpdate(ast)!;

        Assert.Contains("\"o\":\"-\"", json);
    }

    [Fact]
    public void Path_assignment_serializes_as_path_envelope()
    {
        var ast = Parse("SET a = b");

        var json = SprocAstSerializer.SerializeUpdate(ast)!;

        Assert.Contains("\"path\":\"a\"", json);
        Assert.Contains("{\"$k\":\"path\",\"p\":\"b\"}", json);
    }

    [Fact]
    public void If_not_exists_serializes_as_ifne_envelope()
    {
        var ast = Parse("SET v = if_not_exists(v, :start)",
            values: new Dictionary<string, JsonElement> { [":start"] = Val("{\"N\":\"0\"}") });

        var json = SprocAstSerializer.SerializeUpdate(ast)!;

        // {"$k":"ifne","p":"v","f":{"$k":"lit","v":0}}
        Assert.Contains("\"$k\":\"ifne\"", json);
        Assert.Contains("\"p\":\"v\"", json);
        Assert.Contains("\"f\":{\"$k\":\"lit\",\"v\":0}", json);
    }

    [Fact]
    public void List_append_serializes_as_lap_envelope()
    {
        var ast = Parse("SET items = list_append(items, :more)",
            values: new Dictionary<string, JsonElement> { [":more"] = Val("{\"L\":[{\"S\":\"x\"}]}") });

        var json = SprocAstSerializer.SerializeUpdate(ast)!;

        // {"$k":"lap","l":{"$k":"path","p":"items"},"r":{"$k":"lit","v":["x"]}}
        Assert.Contains("\"$k\":\"lap\"", json);
        Assert.Contains("\"l\":{\"$k\":\"path\",\"p\":\"items\"}", json);
        Assert.Contains("\"r\":{\"$k\":\"lit\",\"v\":[\"x\"]}", json);
    }

    [Fact]
    public void Map_literal_set_value_cannot_be_confused_with_an_operand()
    {
        // A user map value whose keys happen to look like an operand ("op",
        // "path") must round-trip as a literal, never be reinterpreted.
        var ast = Parse("SET m = :v",
            values: new Dictionary<string, JsonElement>
            {
                [":v"] = Val("{\"M\":{\"op\":{\"S\":\"+\"},\"path\":{\"S\":\"x\"}}}")
            });

        var json = SprocAstSerializer.SerializeUpdate(ast)!;

        // The whole map is nested under the "lit" envelope, so resolveSetValue
        // returns it verbatim.
        Assert.Contains("{\"$k\":\"lit\",\"v\":{\"op\":\"+\",\"path\":\"x\"}}", json);
    }

    [Fact]
    public void Remove_action_serializes_path_list()
    {
        var ast = Parse("REMOVE stale");

        var json = SprocAstSerializer.SerializeUpdate(ast)!;

        Assert.Contains("\"remove\":[\"stale\"]", json);
    }
}
