using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Aws2Azure.Core.Buffers;
using Aws2Azure.Modules.DynamoDb.Expressions;
using Aws2Azure.Modules.DynamoDb.Internal;
using Aws2Azure.Modules.DynamoDb.Operations;
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
    private static string SerializeCondition(ConditionNode node)
    {
        using var buffer = new PooledByteBufferWriter(256);
        SprocAstSerializer.WriteCondition(buffer, node);
        return Encoding.UTF8.GetString(buffer.WrittenMemory.Span);
    }

    private static string SerializeUpdate(UpdateExpressionAst ast)
    {
        using var buffer = new PooledByteBufferWriter(256);
        SprocAstSerializer.WriteUpdate(buffer, ast);
        return Encoding.UTF8.GetString(buffer.WrittenMemory.Span);
    }

    private static JsonElement Val(string json)
        => JsonDocument.Parse(json).RootElement.Clone();

    private static JsonElement StringVal(string value)
        => JsonSerializer.SerializeToElement(
            new Dictionary<string, string> { ["S"] = value });

    private static JsonElement MapVal(string name, string value)
        => JsonSerializer.SerializeToElement(
            new Dictionary<string, Dictionary<string, Dictionary<string, string>>>
            {
                ["M"] = new()
                {
                    [name] = new() { ["S"] = value },
                },
            });

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

        var json = SerializeUpdate(ast);

        Assert.Contains("\"set\":[", json);
        Assert.Contains("\"path\":\"name\"", json);
        Assert.Contains("{\"$k\":\"lit\",\"v\":\"bob\"}", json);
    }

    [Fact]
    public void Arithmetic_increment_serializes_as_op_envelope()
    {
        var ast = Parse("SET counter = counter + :i",
            values: new Dictionary<string, JsonElement> { [":i"] = Val("{\"N\":\"1\"}") });

        var json = SerializeUpdate(ast);

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

        var json = SerializeUpdate(ast);

        Assert.Contains("\"o\":\"-\"", json);
    }

    [Fact]
    public void Bare_number_operand_uses_the_persisted_codec_canonical_form()
    {
        var ast = Parse(
            "SET counter = :value",
            values: new Dictionary<string, JsonElement>
            {
                [":value"] = Val("{\"N\":\"1e3\"}"),
            });

        var json = SerializeUpdate(ast);

        Assert.Contains("\"v\":1000", json, StringComparison.Ordinal);
        Assert.DoesNotContain("1e3", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Path_assignment_serializes_as_path_envelope()
    {
        var ast = Parse("SET a = b");

        var json = SerializeUpdate(ast);

        Assert.Contains("\"path\":\"a\"", json);
        Assert.Contains("{\"$k\":\"path\",\"p\":\"b\"}", json);
    }

    [Fact]
    public void If_not_exists_serializes_as_ifne_envelope()
    {
        var ast = Parse("SET v = if_not_exists(v, :start)",
            values: new Dictionary<string, JsonElement> { [":start"] = Val("{\"N\":\"0\"}") });

        var json = SerializeUpdate(ast);

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

        var json = SerializeUpdate(ast);

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

        var json = SerializeUpdate(ast);

        // The whole map is nested under the "lit" envelope, so resolveSetValue
        // returns it verbatim.
        Assert.Contains("{\"$k\":\"lit\",\"v\":{\"op\":\"+\",\"path\":\"x\"}}", json);
    }

    [Fact]
    public void Remove_action_serializes_path_list()
    {
        var ast = Parse("REMOVE stale");

        var json = SerializeUpdate(ast);

        Assert.Contains("\"remove\":[\"stale\"]", json);
    }

    [Fact]
    public void Update_round_trips_control_characters_in_paths_names_and_values()
    {
        var targetPath = "target\n\t\0\"\\";
        var removedPath = "removed\0\n\t\"\\";
        var mapName = "name\n\t\0\"\\";
        var value = "value\0\t\n\"\\";
        var ast = Parse(
            "SET #target = :value REMOVE #removed",
            names: new Dictionary<string, string>
            {
                ["#target"] = targetPath,
                ["#removed"] = removedPath,
            },
            values: new Dictionary<string, JsonElement>
            {
                [":value"] = MapVal(mapName, value),
            });

        var json = SerializeUpdate(ast);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var set = root.GetProperty("set")[0];
        Assert.Equal(targetPath, set.GetProperty("path").GetString());
        Assert.Equal(
            value,
            set.GetProperty("value")
                .GetProperty("v")
                .GetProperty(mapName)
                .GetString());
        Assert.Equal(removedPath, root.GetProperty("remove")[0].GetString());
    }

    [Fact]
    public void Condition_round_trips_control_characters_in_path_and_value_operands()
    {
        var path = "condition\n\t\0\"\\";
        var value = "operand\0\n\t\"\\";
        var condition = ConditionExpressionParser.Parse(
            "attribute_exists(#path) AND #path = :value",
            new Dictionary<string, string> { ["#path"] = path },
            new Dictionary<string, JsonElement> { [":value"] = StringVal(value) });

        var json = SerializeCondition(condition);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(path, root.GetProperty("left").GetProperty("attr").GetString());
        var comparison = root.GetProperty("right");
        Assert.Equal(
            path,
            comparison.GetProperty("attr").GetProperty("path").GetString());
        Assert.Equal(value, comparison.GetProperty("value").GetString());
    }

    [Fact]
    public void Condition_bytes_match_utf8_json_writer_escaping()
    {
        var path = "name<>&\u2028é\n\t\0\"\\";
        var value = "value<>&\u2029é\n\t\0\"\\";
        var condition = ConditionExpressionParser.Parse(
            "#path = :value",
            new Dictionary<string, string> { ["#path"] = path },
            new Dictionary<string, JsonElement>
            {
                [":value"] = StringVal(value),
            });
        using var actual = new PooledByteBufferWriter(256);
        SprocAstSerializer.WriteCondition(actual, condition);

        using var expected = new PooledByteBufferWriter(256);
        using (var writer = new Utf8JsonWriter(
                   expected,
                   new JsonWriterOptions
                   {
                       Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                   }))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "COMPARE");
            writer.WriteStartObject("attr");
            writer.WriteString("path", path);
            writer.WriteEndObject();
            writer.WriteString("op", "=");
            writer.WriteString("value", value);
            writer.WriteEndObject();
            writer.Flush();
        }

        Assert.Equal(
            expected.WrittenMemory.ToArray(),
            actual.WrittenMemory.ToArray());
    }

    [Fact]
    public void Unknown_condition_node_fails_closed()
    {
        var exception = Assert.Throws<NotSupportedException>(
            () => SerializeCondition(
                new UnsupportedCondition()));

        Assert.Contains("Unsupported", exception.Message);
    }

    [Fact]
    public void Repeated_large_value_references_abort_at_bound_without_ast_materialization()
    {
        const int referenceCount = 199;
        const int valueLength = 100 * 1024;
        var condition = RepeatedStringCondition(referenceCount, valueLength);

        var overflow = WriteBounded(condition, withFingerprint: true);
        Assert.Equal(
            TransactWriteItemsHandler.MaxSprocRequestBodyBytes,
            overflow.Limit);
        Assert.InRange(
            overflow.WrittenBytes,
            overflow.Limit - 2048,
            overflow.Limit);

        _ = WriteBounded(condition, withFingerprint: true);
        long minimumAllocated = long.MaxValue;
        for (var round = 0; round < 5; round++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            _ = WriteBounded(condition, withFingerprint: true);
            minimumAllocated = Math.Min(
                minimumAllocated,
                GC.GetAllocatedBytesForCurrentThread() - before);
        }

        var expandedUtf16Bytes =
            (long)referenceCount * valueLength * sizeof(char);
        Assert.True(
            minimumAllocated < expandedUtf16Bytes / 4,
            $"Bounded streaming allocated {minimumAllocated:N0} bytes; " +
            $"a quarter of the avoided expanded UTF-16 AST is " +
            $"{expandedUtf16Bytes / 4:N0} bytes.");
    }

    [Fact]
    public void Condition_streaming_honors_the_exact_two_mib_boundary()
    {
        var empty = RepeatedStringCondition(referenceCount: 1, valueLength: 0);
        using var baseline = new PooledByteBufferWriter(256);
        SprocAstSerializer.WriteCondition(baseline, empty);
        var valueLength =
            TransactWriteItemsHandler.MaxSprocRequestBodyBytes
            - baseline.WrittenMemory.Length;

        var exactCondition = RepeatedStringCondition(1, valueLength);
        using var exact = NewBoundedWriter();
        SprocAstSerializer.WriteCondition(exact, exactCondition);
        Assert.Equal(
            TransactWriteItemsHandler.MaxSprocRequestBodyBytes,
            exact.WrittenMemory.Length);

        var oversizedCondition = RepeatedStringCondition(1, valueLength + 1);
        using var oversized = NewBoundedWriter();
        var exception = Assert.Throws<BoundedBufferWriterLimitException>(
            () => SprocAstSerializer.WriteCondition(
                oversized,
                oversizedCondition));
        Assert.Equal(
            TransactWriteItemsHandler.MaxSprocRequestBodyBytes,
            exception.Limit);
    }

    private static ConditionNode RepeatedStringCondition(
        int referenceCount,
        int valueLength)
    {
        var expression = string.Join(
            " OR ",
            Enumerable.Repeat("#value = :value", referenceCount));
        return ConditionExpressionParser.Parse(
            expression,
            new Dictionary<string, string> { ["#value"] = "value" },
            new Dictionary<string, JsonElement>
            {
                [":value"] = StringVal(new string('x', valueLength)),
            });
    }

    private static BoundedBufferWriterLimitException WriteBounded(
        ConditionNode condition,
        bool withFingerprint)
    {
        using var writer = NewBoundedWriter();
        using var fingerprint = withFingerprint
            ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256)
            : null;
        return Assert.Throws<BoundedBufferWriterLimitException>(
            () => SprocAstSerializer.WriteCondition(
                writer,
                condition,
                fingerprint));
    }

    private static BoundedPooledByteBufferWriter NewBoundedWriter()
        => new(
            TransactWriteItemsHandler.MaxSprocRequestBodyBytes,
            initialCapacity: 512,
            maximumScratchSizeHint:
                TransactWriteItemsHandler.MaxSprocRequestBodyBytes);

    private sealed record UnsupportedCondition : ConditionNode;
}
