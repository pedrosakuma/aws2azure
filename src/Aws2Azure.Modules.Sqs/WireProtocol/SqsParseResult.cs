using System.Collections.Generic;

namespace Aws2Azure.Modules.Sqs.WireProtocol;

/// <summary>
/// Outcome of parsing an incoming SQS request: which protocol was used,
/// which operation was requested, and the flattened parameter dictionary
/// the operation handler will consult.
/// </summary>
/// <param name="Protocol">Whether the request arrived as query-protocol or AWS JSON.</param>
/// <param name="Operation">Operation resolved from <c>Action=</c> or <c>X-Amz-Target</c>.</param>
/// <param name="Parameters">
/// Flattened parameter map. For query-protocol callers this is the form/query
/// pairs as-is (including AWS list-protocol keys like <c>Attribute.1.Name</c>).
/// For AWS JSON callers the JSON object's top-level scalar properties are
/// projected here under their PascalCase JSON property names; complex/nested
/// payloads (e.g. <c>Entries</c>, <c>MessageAttributes</c>) are left to the
/// per-op handlers which consume <see cref="JsonBody"/> directly.
/// </param>
/// <param name="JsonBody">
/// Raw JSON body when <paramref name="Protocol"/> is
/// <see cref="SqsWireProtocol.AwsJson"/>; <c>null</c> for query-protocol
/// requests.
/// </param>
/// <param name="Error">
/// Non-null if the request could not be classified (e.g. unknown action,
/// malformed body). When set, the rest of the fields hold partial info only.
/// </param>
public sealed record SqsParseResult(
    SqsWireProtocol Protocol,
    SqsOperation Operation,
    IReadOnlyDictionary<string, string> Parameters,
    string? JsonBody,
    SqsParseError? Error);

/// <summary>
/// Parse-time error surfaced as an SQS-shaped response in the negotiated
/// protocol's envelope.
/// </summary>
public sealed record SqsParseError(string Code, string Message);
