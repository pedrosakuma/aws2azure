using System.Threading.Tasks;
using Aws2Azure.Modules.DynamoDb.Internal;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.DynamoDb.Operations;

/// <summary>
/// DynamoDB <c>TransactWriteItems</c> stub. Cosmos DB does not offer
/// cross-container ACID writes (transactional batches are restricted to
/// a single logical partition within a single container), so a faithful
/// AWS-shape implementation would have to fake atomicity. To avoid that
/// footgun we surface a clear <c>TransactionCanceledException</c> with a
/// validation reason and document the gap in
/// <c>docs/gaps/dynamodb/TransactWriteItems.yaml</c>. Callers can fall
/// back to per-item <c>PutItem</c>/<c>UpdateItem</c>/<c>DeleteItem</c> or
/// the <c>BatchWriteItem</c> endpoint which honestly advertise non-atomic
/// semantics.
/// </summary>
internal static class TransactWriteItemsHandler
{
    public static Task HandleTransactWriteItemsAsync(
        HttpContext ctx, byte[] body, CosmosClient cosmos, System.Threading.CancellationToken ct)
    {
        return CosmosOpsShared.WriteErrorAsync(ctx, 400, "TransactionCanceledException",
            "TransactWriteItems is not supported by aws2azure: Azure Cosmos DB cannot provide ACID writes across containers/partitions. Use BatchWriteItem or per-item operations instead.");
    }
}
