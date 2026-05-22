using System.Threading.Tasks;
using Aws2Azure.Modules.Sns.Errors;
using Aws2Azure.Modules.Sns.WireProtocol;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.Sns.Operations;

internal static class StubHandlers
{
    public static Task HandleNotImplementedAsync(HttpContext context, SnsOperation operation)
    {
        return SnsErrorResponse.WriteErrorAsync(
            context,
            StatusCodes.Status501NotImplemented,
            errorType: "Receiver",
            errorCode: "InternalFailure",
            message: $"SNS operation '{SnsOperationNames.ToShortName(operation)}' is not yet implemented by aws2azure.");
    }
}
