using Aws2Azure.Modules.S3.Errors;
using Aws2Azure.Modules.S3.Streaming;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.S3.Internal;

internal static class MultipartChecksumPolicy
{
    public static bool TryReadInitiation(
        HttpRequest request,
        out FlexibleChecksumAlgorithm? algorithm,
        out S3ErrorMapping.Mapping? error)
    {
        algorithm = null;
        error = null;

        var algorithmValue = ReadSingleHeader(request, "x-amz-checksum-algorithm", out var duplicateAlgorithm);
        if (duplicateAlgorithm)
        {
            error = InvalidArgument("Header x-amz-checksum-algorithm must contain exactly one value.");
            return false;
        }

        var checksumType = ReadSingleHeader(request, "x-amz-checksum-type", out var duplicateType);
        if (duplicateType)
        {
            error = InvalidArgument("Header x-amz-checksum-type must contain exactly one value.");
            return false;
        }

        if (algorithmValue is null)
        {
            if (checksumType is not null)
            {
                error = InvalidArgument("x-amz-checksum-type requires x-amz-checksum-algorithm.");
                return false;
            }

            return true;
        }

        if (!FlexibleChecksumValidation.TryParseAlgorithm(algorithmValue, out var parsed))
        {
            error = InvalidArgument($"Checksum algorithm '{algorithmValue}' is not supported.");
            return false;
        }

        if (parsed == FlexibleChecksumAlgorithm.Crc64Nvme)
        {
            error = NotImplemented(
                "aws2azure: CRC64NVME multipart checksums are not supported because the proxy cannot reproduce AWS's full-object result.");
            return false;
        }

        if (checksumType is not null &&
            !checksumType.Equals("COMPOSITE", StringComparison.OrdinalIgnoreCase))
        {
            error = NotImplemented(
                $"aws2azure: Multipart checksum type '{checksumType}' is not supported; only COMPOSITE request validation is available.");
            return false;
        }

        algorithm = parsed;
        return true;
    }

    private static string? ReadSingleHeader(HttpRequest request, string name, out bool duplicate)
    {
        duplicate = false;
        if (!request.Headers.TryGetValue(name, out var values))
        {
            return null;
        }

        string? found = null;
        foreach (var rawValue in values)
        {
            if (string.IsNullOrWhiteSpace(rawValue))
            {
                continue;
            }

            if (found is not null)
            {
                duplicate = true;
                return null;
            }

            found = rawValue.Trim();
        }

        return found;
    }

    private static S3ErrorMapping.Mapping InvalidArgument(string message) =>
        S3ErrorMapping.InvalidArgument(message);

    private static S3ErrorMapping.Mapping NotImplemented(string message) =>
        new(StatusCodes.Status501NotImplemented, "NotImplemented", message);
}
