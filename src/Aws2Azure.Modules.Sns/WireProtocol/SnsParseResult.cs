using System.Collections.Generic;

namespace Aws2Azure.Modules.Sns.WireProtocol;

public sealed record SnsParseResult(
    SnsOperation Operation,
    IReadOnlyDictionary<string, string> Parameters,
    SnsParseError? Error);

public sealed record SnsParseError(
    SnsParseErrorType Type,
    string Code,
    string Message);

public enum SnsParseErrorType
{
    InvalidMethod,
    InvalidContentType,
    UnsupportedJsonProtocol,
    MissingAction,
    UnknownOperation,
    InvalidRequest,
}
