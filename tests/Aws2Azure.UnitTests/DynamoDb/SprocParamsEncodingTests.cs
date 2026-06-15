using System;
using System.Text;
using Aws2Azure.Modules.DynamoDb.Internal;
using Aws2Azure.Modules.DynamoDb.Operations;
using Xunit;

namespace Aws2Azure.UnitTests.DynamoDb;

/// <summary>
/// Byte-identity guard for #345: the single-pass / zero-copy sproc parameter
/// encoders (<see cref="SprocManager.WriteSingleWriteParams"/> and
/// <see cref="TransactWriteItemsHandler.BuildTransactParamsBody"/>) must produce
/// wire bytes identical to the legacy string encoders they replaced, so removing
/// the <c>byte[] → string → byte[]</c> round-trips is provably behaviour-neutral.
/// </summary>
public class SprocParamsEncodingTests
{
    private static byte[] WriteSingleWrite(
        SprocOperation op, string docId, string? payload, string? conditionAst, string? updateAst)
    {
        using var buf = new PooledByteBufferWriter(64);
        ReadOnlyMemory<byte>? payloadBytes =
            payload is null ? (ReadOnlyMemory<byte>?)null : Encoding.UTF8.GetBytes(payload);
        SprocManager.WriteSingleWriteParams(buf, op, docId, payloadBytes, conditionAst, updateAst);
        return buf.WrittenMemory.ToArray();
    }

    [Theory]
    // PUT with a document payload + a condition AST.
    [InlineData("Put", "user#1", "{\"id\":\"user#1\",\"_a2a_pk\":\"p\",\"name\":\"bob\"}",
        "{\"type\":\"ATTR_NOT_EXISTS\",\"attr\":\"id\"}", null)]
    // UPDATE with key attrs + condition + update AST.
    [InlineData("Update", "k#2", "{\"id\":\"k#2\",\"_a2a_pk\":\"p\",\"_a2a\":\"item\"}",
        "{\"type\":\"COMPARE\",\"attr\":{\"path\":\"v\"},\"op\":\"=\",\"value\":1}",
        "{\"set\":[{\"path\":\"v\",\"value\":{\"$k\":\"lit\",\"v\":2}}]}")]
    // DELETE: null payload + condition, null update.
    [InlineData("Delete", "k#3", null, "{\"type\":\"ATTR_EXISTS\",\"attr\":\"id\"}", null)]
    // No condition / no update: bare PUT.
    [InlineData("Put", "k#4", "{\"id\":\"k#4\"}", null, null)]
    // docId carrying the two characters the minimal escaper handles.
    [InlineData("Put", "weird\"id\\path", "{\"id\":\"x\"}", null, null)]
    public void SingleWrite_params_are_byte_identical_to_legacy(
        string opName, string docId, string? payload, string? conditionAst, string? updateAst)
    {
        var op = Enum.Parse<SprocOperation>(opName);

        var actual = WriteSingleWrite(op, docId, payload, conditionAst, updateAst);
        var expected = Encoding.UTF8.GetBytes(
            SprocManager.BuildParamsJson(op, docId, payload, conditionAst, updateAst));

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void SingleWrite_null_payload_emits_null_literal()
    {
        var bytes = WriteSingleWrite(SprocOperation.Delete, "k", payload: null, conditionAst: null, updateAst: null);
        Assert.Equal("[\"DELETE\",\"k\",null,null,null]", Encoding.UTF8.GetString(bytes));
    }

    private static byte[] BuildTransact(params TransactWriteItemsHandler.PreparedOp[] ops)
    {
        using var buf = TransactWriteItemsHandler.BuildTransactParamsBody(ops);
        return buf.WrittenMemory.ToArray();
    }

    [Fact]
    public void Transact_params_wrap_operations_array_byte_identical_to_legacy()
    {
        var ops = new[]
        {
            new TransactWriteItemsHandler.PreparedOp(
                TransactWriteItemsHandler.OpKind.Put, "1",
                Encoding.UTF8.GetBytes("{\"id\":\"1\",\"_a2a_pk\":\"p\"}"), null),
            new TransactWriteItemsHandler.PreparedOp(
                TransactWriteItemsHandler.OpKind.Delete, "2", null,
                "{\"type\":\"ATTR_EXISTS\",\"attr\":\"id\"}"),
            new TransactWriteItemsHandler.PreparedOp(
                TransactWriteItemsHandler.OpKind.Check, "3", null, null),
        };

        var actual = BuildTransact(ops);
        var expected = Encoding.UTF8.GetBytes("[" + TransactWriteItemsHandler.BuildOperationsJson(ops) + "]");

        Assert.Equal(expected, actual);
        // The parameter list nests the operations array one level deep.
        Assert.StartsWith("[[", Encoding.UTF8.GetString(actual));
        Assert.EndsWith("]]", Encoding.UTF8.GetString(actual));
    }

    [Fact]
    public void Transact_empty_ops_is_double_empty_array()
    {
        var actual = BuildTransact(Array.Empty<TransactWriteItemsHandler.PreparedOp>());
        Assert.Equal("[[]]", Encoding.UTF8.GetString(actual));
    }
}
