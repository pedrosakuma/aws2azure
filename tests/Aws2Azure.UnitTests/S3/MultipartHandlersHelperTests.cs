using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Aws2Azure.Modules.S3.Errors;
using Aws2Azure.Modules.S3.Operations;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.UnitTests.S3;

public sealed class MultipartHandlersHelperTests
{
    [Fact]
    public void Normalize_copy_source_range_treats_missing_header_as_full_copy()
    {
        var context = new DefaultHttpContext();
        var result = Invoke<(string? Value, S3ErrorMapping.Mapping? Error)>("NormalizeCopySourceRange", context.Request);

        Assert.Null(result.Value);
        Assert.Null(result.Error);
    }

    [Fact]
    public void Normalize_copy_source_range_canonicalizes_valid_partial_range()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["x-amz-copy-source-range"] = "bytes=0001-0003";

        var result = Invoke<(string? Value, S3ErrorMapping.Mapping? Error)>("NormalizeCopySourceRange", context.Request);

        Assert.Equal("bytes=1-3", result.Value);
        Assert.Null(result.Error);
    }

    [Theory]
    [InlineData("1-3")]
    [InlineData("bytes=3-1")]
    [InlineData("bytes=-1-3")]
    [InlineData("bytes=1-")]
    public void Normalize_copy_source_range_rejects_invalid_values(string raw)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["x-amz-copy-source-range"] = raw;

        var result = Invoke<(string? Value, S3ErrorMapping.Mapping? Error)>("NormalizeCopySourceRange", context.Request);

        Assert.Null(result.Value);
        Assert.NotNull(result.Error);
        Assert.Equal("InvalidArgument", result.Error.Value.Code);
    }

    [Theory]
    [InlineData(null, 1000, null)]
    [InlineData("0", 1000, null)]
    [InlineData("7", 7, null)]
    [InlineData("5000", 1000, null)]
    [InlineData("-1", 0, "InvalidArgument")]
    [InlineData("abc", 0, "InvalidArgument")]
    public void Parse_max_parts_applies_defaults_limits_and_errors(string? raw, int expectedValue, string? expectedErrorCode)
    {
        var query = raw is null
            ? new QueryCollection()
            : new QueryCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
            {
                ["max-parts"] = raw
            });

        var result = Invoke<(int Value, S3ErrorMapping.Mapping? Error)>("ParseMaxParts", query);

        Assert.Equal(expectedValue, result.Value);
        Assert.Equal(expectedErrorCode, result.Error?.Code);
    }

    [Fact]
    public void Synthetic_part_etag_is_md5_of_block_id()
    {
        const string blockId = "YjAwMDAwMDAwMDAwMDAwMHBwMDAwMDM=";

        var etag = Invoke<string>("SyntheticPartEtag", blockId);

        Assert.Equal(Convert.ToHexString(MD5.HashData(Encoding.ASCII.GetBytes(blockId))).ToLowerInvariant(), etag);
    }

    [Theory]
    [InlineData("\"0xABCD\"", 1, "\"abcd0000000000000000000000000000-1\"")]
    [InlineData("\"1234567890abcdef1234567890abcdefEXTRA\"", 2, "\"1234567890abcdef1234567890abcdef-2\"")]
    public void Synthesize_multipart_etag_pads_or_truncates_to_s3_shape(string azureEtag, int partCount, string expected)
    {
        var etag = Invoke<string>("SynthesizeMultipartEtag", azureEtag, partCount);

        Assert.Equal(expected, etag);
    }

    [Fact]
    public async Task Hashing_stream_computes_md5_while_stream_is_read()
    {
        var inner = new MemoryStream(Encoding.UTF8.GetBytes("hash-me"));
        await using var hashing = CreateHashingStream(inner, HashAlgorithmName.MD5);

        var buffer = new byte[3];
        while (await hashing.ReadAsync(buffer) > 0)
        {
        }

        var hash = GetFinalHash(hashing);
        Assert.Equal(
            Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes("hash-me"))).ToLowerInvariant(),
            Convert.ToHexString(hash).ToLowerInvariant());
        Assert.Equal(hash, GetFinalHash(hashing));
    }

    private static T Invoke<T>(string methodName, params object?[] args) =>
        (T)typeof(MultipartHandlers)
            .GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, args)!;

    private static Stream CreateHashingStream(Stream inner, HashAlgorithmName algorithm) =>
        (Stream)typeof(MultipartHandlers)
            .GetNestedType("HashingStream", BindingFlags.NonPublic)!
            .GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, binder: null, [typeof(Stream), typeof(HashAlgorithmName)], modifiers: null)!
            .Invoke([inner, algorithm]);

    private static byte[] GetFinalHash(Stream hashing) =>
        (byte[])hashing.GetType()
            .GetMethod("GetFinalHash", BindingFlags.Instance | BindingFlags.Public)!
            .Invoke(hashing, null)!;
}
