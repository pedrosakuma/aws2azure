namespace Aws2Azure.Modules.DynamoDb.Expressions;

internal abstract class ConditionNodeVisitor<TResult, TContext>
{
    protected TResult Visit(ConditionNode node, TContext context) => node switch
    {
        AndCondition and => VisitAnd(and, context),
        OrCondition or => VisitOr(or, context),
        NotCondition not => VisitNot(not, context),
        AttributeExistsCondition ae => VisitAttributeExists(ae, context),
        AttributeNotExistsCondition ane => VisitAttributeNotExists(ane, context),
        AttributeTypeCondition at => VisitAttributeType(at, context),
        BeginsWithCondition bw => VisitBeginsWith(bw, context),
        ContainsCondition contains => VisitContains(contains, context),
        CompareCondition compare => VisitCompare(compare, context),
        BetweenCondition between => VisitBetween(between, context),
        InCondition inn => VisitIn(inn, context),
        _ => VisitUnsupported(node, context),
    };

    protected abstract TResult VisitAnd(AndCondition node, TContext context);
    protected abstract TResult VisitOr(OrCondition node, TContext context);
    protected abstract TResult VisitNot(NotCondition node, TContext context);
    protected abstract TResult VisitAttributeExists(AttributeExistsCondition node, TContext context);
    protected abstract TResult VisitAttributeNotExists(AttributeNotExistsCondition node, TContext context);
    protected abstract TResult VisitAttributeType(AttributeTypeCondition node, TContext context);
    protected abstract TResult VisitBeginsWith(BeginsWithCondition node, TContext context);
    protected abstract TResult VisitContains(ContainsCondition node, TContext context);
    protected abstract TResult VisitCompare(CompareCondition node, TContext context);
    protected abstract TResult VisitBetween(BetweenCondition node, TContext context);
    protected abstract TResult VisitIn(InCondition node, TContext context);
    protected abstract TResult VisitUnsupported(ConditionNode node, TContext context);
}
