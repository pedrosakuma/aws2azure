namespace Aws2Azure.Modules.S3;

/// <summary>
/// Parsed S3 request: which operation, plus extracted bucket/key when the
/// shape carries them. Built by <see cref="S3Router"/> from the incoming
/// <see cref="Microsoft.AspNetCore.Http.HttpContext"/>.
/// </summary>
public readonly record struct S3RouteResult(
    S3Operation Operation,
    string? Bucket,
    string? Key,
    bool VirtualHosted);
