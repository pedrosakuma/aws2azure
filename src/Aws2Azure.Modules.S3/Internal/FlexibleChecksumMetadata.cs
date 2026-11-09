using Aws2Azure.Modules.S3.Streaming;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.S3.Internal;

internal static class FlexibleChecksumMetadata
{
    internal const string AlgorithmMetadataName = "aws2azurechecksumalgorithm";
    internal const string DigestMetadataName = "aws2azurechecksumdigest";

    internal readonly record struct Value(FlexibleChecksumAlgorithm Algorithm, string Digest);

    internal enum ReadKind
    {
        None,
        Success,
        Corrupt,
    }

    internal static void AddToRequest(HttpRequestMessage request, FlexibleChecksumValidation? validation)
    {
        if (validation is null || !validation.TryGetPersistableDigest(out var digest))
        {
            return;
        }

        request.Headers.TryAddWithoutValidation(
            "x-ms-meta-" + AlgorithmMetadataName,
            FlexibleChecksumValidation.GetToken(validation.Algorithm));
        request.Headers.TryAddWithoutValidation("x-ms-meta-" + DigestMetadataName, digest);
    }

    internal static ReadKind Read(HttpResponseMessage response, out Value value)
    {
        value = default;
        var hasAlgorithm = TryReadSingleMetadata(response, AlgorithmMetadataName, out var algorithmToken, out var duplicateAlgorithm);
        var hasDigest = TryReadSingleMetadata(response, DigestMetadataName, out var digest, out var duplicateDigest);

        if (duplicateAlgorithm || duplicateDigest || hasAlgorithm != hasDigest)
        {
            return ReadKind.Corrupt;
        }

        if (!hasAlgorithm)
        {
            return ReadKind.None;
        }

        if (!FlexibleChecksumValidation.TryParseAlgorithm(algorithmToken, out var algorithm) ||
            !IsCanonicalDigest(digest, FlexibleChecksumValidation.GetDigestLength(algorithm)))
        {
            return ReadKind.Corrupt;
        }

        value = new Value(algorithm, digest);
        return ReadKind.Success;
    }

    internal static void ApplyToResponse(HttpResponse response, Value value)
    {
        response.Headers[FlexibleChecksumValidation.GetHeaderName(value.Algorithm)] = value.Digest;
        response.Headers["x-amz-checksum-type"] = "FULL_OBJECT";
    }

    private static bool TryReadSingleMetadata(
        HttpResponseMessage response,
        string metadataName,
        out string value,
        out bool duplicate)
    {
        value = string.Empty;
        duplicate = false;
        if (!response.Headers.TryGetValues("x-ms-meta-" + metadataName, out var values))
        {
            return false;
        }

        var found = false;
        foreach (var candidate in values)
        {
            if (found)
            {
                duplicate = true;
                return false;
            }

            value = candidate;
            found = true;
        }

        return found;
    }

    private static bool IsCanonicalDigest(string value, int expectedLength)
    {
        Span<byte> decoded = stackalloc byte[32];
        return Convert.TryFromBase64String(value, decoded, out var bytesWritten)
            && bytesWritten == expectedLength
            && Convert.ToBase64String(decoded[..bytesWritten]) == value;
    }
}
