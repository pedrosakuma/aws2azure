using System;
using System.Collections.Generic;
using System.Text.Json;
using Aws2Azure.Modules.DynamoDb.Expressions;

namespace Aws2Azure.Modules.DynamoDb.Operations;

/// <summary>
/// Shared parsing entry point for conditional-write operands
/// (UpdateItem / PutItem / DeleteItem). Centralises:
/// <list type="bullet">
///   <item>Mutual-exclusion between modern <c>ConditionExpression</c>
///         and legacy <c>Expected</c>/<c>ConditionalOperator</c>.</item>
///   <item>Resolution of <c>ExpressionAttributeNames</c> and
///         <c>ExpressionAttributeValues</c> to the parser.</item>
///   <item>Reporting <c>ExpressionAttributeValues</c> entries that were
///         declared but never referenced — AWS rejects this with
///         ValidationException; we currently log the divergence in the
///         gap doc and skip the strict check until Slice 5.</item>
/// </list>
/// </summary>
internal static class ConditionGate
{
    /// <summary>
    /// Parses a request's condition shape. Returns <c>null</c> when no
    /// condition is present. Throws <see cref="ExpressionSyntaxException"/>
    /// on a parse error and <see cref="ConditionParseConflictException"/>
    /// when modern and legacy shapes are combined.
    /// </summary>
    public static ConditionNode? TryParse(
        string? conditionExpression,
        JsonElement? expected,
        string? conditionalOperator,
        IReadOnlyDictionary<string, string>? names,
        IReadOnlyDictionary<string, JsonElement>? values)
    {
        var hasExpr = !string.IsNullOrWhiteSpace(conditionExpression);
        var hasExpected = expected.HasValue && expected.Value.ValueKind == JsonValueKind.Object;
        var hasCondOp = !string.IsNullOrWhiteSpace(conditionalOperator);

        if (hasExpr && (hasExpected || hasCondOp))
            throw new ConditionParseConflictException(
                "ConditionExpression and the legacy Expected/ConditionalOperator parameters are mutually exclusive.");

        if (hasExpr)
        {
            return ConditionExpressionParser.Parse(conditionExpression!, names, values);
        }
        if (hasExpected)
        {
            return ExpectedNormaliser.Build(expected, conditionalOperator);
        }
        // ConditionalOperator without Expected is a noop in legacy.
        return null;
    }
}

internal sealed class ConditionParseConflictException : Exception
{
    public ConditionParseConflictException(string message) : base(message) { }
}
