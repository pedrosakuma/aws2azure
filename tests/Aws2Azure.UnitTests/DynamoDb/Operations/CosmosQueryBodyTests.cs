using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Aws2Azure.Modules.DynamoDb.Expressions;
using Aws2Azure.Modules.DynamoDb.Operations;
using Xunit;

namespace Aws2Azure.UnitTests.DynamoDb.Operations;

/// <summary>
/// Byte-identity gate for the single-pass <see cref="CosmosQueryBody.Build"/>
/// (#344): the pooled-UTF-8 output must equal the reference
/// <c>Utf8JsonWriter → GetString → UTF-8</c> encoding it replaced, including
/// the string-parameter path that dropped the <c>JsonElement</c> round-trip.
/// </summary>
public sealed class CosmosQueryBodyTests
{
    private static JsonElement V(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// Reference encoder mirroring the pre-#344 implementation: writes via
    /// <see cref="Utf8JsonWriter"/> into a <see cref="MemoryStream"/> and
    /// returns the UTF-8 bytes (the old path returned a string the callers
    /// re-encoded; the bytes are what hit the wire).
    /// </summary>
    private static byte[] ReferenceBuild(string sql, IReadOnlyList<CosmosSqlParameter> parameters)
    {
        using var ms = new MemoryStream();
        var options = new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        using (var writer = new Utf8JsonWriter(ms, options))
        {
            writer.WriteStartObject();
            writer.WriteString("query", sql);
            writer.WritePropertyName("parameters");
            writer.WriteStartArray();
            foreach (var p in parameters)
            {
                writer.WriteStartObject();
                writer.WriteString("name", p.Name);
                writer.WritePropertyName("value");
                p.Value.WriteTo(writer);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return ms.ToArray();
    }

    public static IEnumerable<object[]> Corpus()
    {
        // Each param spec is [name, kind ("raw"|"elem"), value]; "raw" uses the
        // string ctor, "elem" parses the value as JSON for the JsonElement ctor.
        yield return new object[] { "SELECT * FROM c WHERE c._a2a = 'item'", System.Array.Empty<string[]>() };
        // Typed element-backed params (number / bool / null / string).
        yield return new object[]
        {
            "SELECT * FROM c WHERE c.age > @n AND c.flag = @b AND c.del = @z AND c.name = @s",
            new[]
            {
                new[] { "@n", "elem", "30" },
                new[] { "@b", "elem", "true" },
                new[] { "@z", "elem", "null" },
                new[] { "@s", "elem", "\"alice\"" },
            },
        };
        // String-backed params (KeyCondition path): must match the old
        // StringValue(JsonElement) encoding byte-for-byte.
        yield return new object[]
        {
            "SELECT * FROM c WHERE c.id >= @lo AND c.id <= @hi",
            new[]
            {
                new[] { "@lo", "raw", "2025-01" },
                new[] { "@hi", "raw", "2025-12" },
            },
        };
        // Escaped content in both SQL and string params.
        yield return new object[]
        {
            "SELECT * FROM c WHERE c[\"na\\\"me\"] = @s",
            new[]
            {
                new[] { "@s", "raw", "a\"b\\c\u00e9\nx" },
            },
        };
        // Mixed element string + raw string must be identical.
        yield return new object[]
        {
            "SELECT *",
            new[]
            {
                new[] { "@e", "elem", "\"a\\\"b\\\\c\u00e9\"" },
                new[] { "@r", "raw", "a\"b\\c\u00e9" },
            },
        };
    }

    private static CosmosSqlParameter[] BuildParams(string[][] specs)
    {
        var result = new CosmosSqlParameter[specs.Length];
        for (int i = 0; i < specs.Length; i++)
        {
            var spec = specs[i];
            result[i] = spec[1] == "raw"
                ? new CosmosSqlParameter(spec[0], spec[2])
                : new CosmosSqlParameter(spec[0], V(spec[2]));
        }

        return result;
    }

    [Theory]
    [MemberData(nameof(Corpus))]
    public void Build_is_byte_identical_to_reference(string sql, string[][] specs)
    {
        CosmosSqlParameter[] parameters = BuildParams(specs);
        byte[] expected = ReferenceBuild(sql, parameters);

        using var actual = CosmosQueryBody.Build(sql, parameters);

        Assert.Equal(expected, actual.WrittenMemory.ToArray());
    }

    [Fact]
    public void String_param_matches_element_string_param_byte_for_byte()
    {
        // The dropped StringValue(JsonElement) round-trip must not change a
        // single output byte vs. emitting the raw string directly.
        const string value = "a\"b\\c\u00e9\tz/</script>";

        using var viaRaw = CosmosQueryBody.Build("Q", new[] { new CosmosSqlParameter("@p", value) });
        using var viaElement = CosmosQueryBody.Build("Q", new[] { new CosmosSqlParameter("@p", V(JsonSerializer.Serialize(value))) });

        Assert.Equal(viaElement.WrittenMemory.ToArray(), viaRaw.WrittenMemory.ToArray());
    }

    [Fact]
    public void Build_output_parses_back_to_expected_shape()
    {
        using var body = CosmosQueryBody.Build(
            "SELECT * FROM c WHERE c.id = @id",
            new[] { new CosmosSqlParameter("@id", "user-42") });

        using var doc = JsonDocument.Parse(body.WrittenMemory);
        Assert.Equal("SELECT * FROM c WHERE c.id = @id", doc.RootElement.GetProperty("query").GetString());
        var arr = doc.RootElement.GetProperty("parameters");
        Assert.Equal(1, arr.GetArrayLength());
        Assert.Equal("@id", arr[0].GetProperty("name").GetString());
        Assert.Equal("user-42", arr[0].GetProperty("value").GetString());
    }
}
