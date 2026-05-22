using System.Threading.Tasks;
using Aws2Azure.Modules.Kinesis.Errors;
using Aws2Azure.Modules.Kinesis.WireProtocol;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.Kinesis.Operations;

/// <summary>
/// Slice-1 placeholders: every recognised operation returns
/// <c>InternalFailure</c> with HTTP 501 so SDK callers surface a
/// clean, distinct exception rather than the generic
/// <c>UnknownOperationException</c> reserved for parse-level failures.
/// Per-slice handlers (Slices 2-7) replace these one at a time.
/// </summary>
internal static class StubHandlers
{
    public static Task HandleNotImplementedAsync(HttpContext context, KinesisOperation op)
    {
        return KinesisErrorResponse.WriteAsync(
            context,
            StatusCodes.Status501NotImplemented,
            "InternalFailure",
            $"Kinesis operation '{KinesisOperationNames.ToShortName(op)}' is not yet implemented by aws2azure.");
    }
}
