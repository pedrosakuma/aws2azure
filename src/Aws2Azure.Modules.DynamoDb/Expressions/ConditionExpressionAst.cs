using System.Collections.Generic;
using System.Text.Json;

namespace Aws2Azure.Modules.DynamoDb.Expressions;

/// <summary>
/// AST for the DynamoDB ConditionExpression / FilterExpression grammar.
/// Used by conditional writes (this slice) and re-used by Query/Scan
/// filter expressions in a later slice. All <c>#name</c> aliases are
/// resolved at parse time; every <see cref="ConditionValueOperand"/>
/// carries its bound <see cref="ValueRefOperand"/>.
/// </summary>
internal abstract record ConditionNode;

internal sealed record AndCondition(ConditionNode Left, ConditionNode Right) : ConditionNode;
internal sealed record OrCondition(ConditionNode Left, ConditionNode Right) : ConditionNode;
internal sealed record NotCondition(ConditionNode Inner) : ConditionNode;

internal enum CompareOp { Equal, NotEqual, Less, LessEqual, Greater, GreaterEqual }

internal sealed record CompareCondition(CompareOp Op, ConditionOperand Left, ConditionOperand Right) : ConditionNode;
internal sealed record BetweenCondition(ConditionOperand Value, ConditionOperand Lower, ConditionOperand Upper) : ConditionNode;
internal sealed record InCondition(ConditionOperand Value, IReadOnlyList<ConditionOperand> Set) : ConditionNode;

internal sealed record AttributeExistsCondition(DocumentPath Path) : ConditionNode;
internal sealed record AttributeNotExistsCondition(DocumentPath Path) : ConditionNode;
internal sealed record AttributeTypeCondition(DocumentPath Path, ValueRefOperand TypeTag) : ConditionNode;
internal sealed record BeginsWithCondition(ConditionOperand Path, ConditionOperand Prefix) : ConditionNode;
internal sealed record ContainsCondition(ConditionOperand Container, ConditionOperand Item) : ConditionNode;

/// <summary>
/// Operand of a condition comparison. Either a path lookup, a literal
/// value reference, or the function <c>size(path)</c> which evaluates
/// to a numeric attribute value.
/// </summary>
internal abstract record ConditionOperand;
internal sealed record ConditionPathOperand(DocumentPath Path) : ConditionOperand;
internal sealed record ConditionValueOperand(ValueRefOperand Value) : ConditionOperand;
internal sealed record SizeOperand(DocumentPath Path) : ConditionOperand;
