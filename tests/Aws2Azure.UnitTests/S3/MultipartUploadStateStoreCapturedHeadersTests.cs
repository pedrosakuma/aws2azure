using System.Reflection;
using Aws2Azure.Modules.S3.Internal;
using Aws2Azure.Modules.S3.Xml;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.UnitTests.S3;

/// <summary>
/// Covers the durable multipart-state record's binary (de)serialization,
/// exercised via reflection since <c>SerializeCapturedHeaders</c> /
/// <c>TryDeserializeCapturedHeaders</c> are private implementation details
/// of <see cref="MultipartUploadStateStore"/> (issue #799 added the
/// <c>x-amz-tagging</c> tags section to this record).
/// </summary>
public sealed class MultipartUploadStateStoreCapturedHeadersTests
{
    [Fact]
    public void Round_trips_tags_captured_from_x_amz_tagging()
    {
        var context = new DefaultHttpContext();
        var tags = new List<S3XmlWriter.Tag>
        {
            new("Project", "Blue"),
            new("Team", "Widget"),
        };

        var serialized = Serialize(context.Request, tags);
        var headers = Deserialize(serialized);

        Assert.Equal(2, headers.Tags.Count);
        Assert.Contains(headers.Tags, t => t.Key == "Project" && t.Value == "Blue");
        Assert.Contains(headers.Tags, t => t.Key == "Team" && t.Value == "Widget");
    }

    [Fact]
    public void Round_trips_empty_tags_when_no_x_amz_tagging_header_present()
    {
        var context = new DefaultHttpContext();

        var serialized = Serialize(context.Request, Array.Empty<S3XmlWriter.Tag>());
        var headers = Deserialize(serialized);

        Assert.Empty(headers.Tags);
    }

    [Fact]
    public void Deserializes_pre_issue_799_records_without_a_trailing_tags_section_as_no_tags()
    {
        // Simulates a durable-state record written before the #799 tags
        // section existed: metadata section is present, but nothing follows
        // it. TryDeserializeCapturedHeaders must treat this as "no tags"
        // rather than fail, so in-flight uploads survive a rolling deploy.
        var context = new DefaultHttpContext();
        context.Request.Headers["Content-Type"] = "text/plain";

        var withTags = Serialize(context.Request, Array.Empty<S3XmlWriter.Tag>());
        var legacyRecord = StripTrailingTagsSection(withTags);

        var headers = Deserialize(legacyRecord);

        Assert.Equal("text/plain", headers.ContentType);
        Assert.Empty(headers.Tags);
    }

    [Fact]
    public void Round_trips_content_headers_and_metadata_alongside_tags()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["Content-Type"] = "application/json";
        context.Request.Headers["Content-Encoding"] = "gzip";
        context.Request.Headers["x-amz-meta-foo"] = "bar";
        var tags = new List<S3XmlWriter.Tag> { new("k", "v") };

        var serialized = Serialize(context.Request, tags);
        var headers = Deserialize(serialized);

        Assert.Equal("application/json", headers.ContentType);
        Assert.Equal("gzip", headers.ContentEncoding);
        Assert.Equal("bar", headers.Metadata["foo"]);
        var tag = Assert.Single(headers.Tags);
        Assert.Equal("k", tag.Key);
        Assert.Equal("v", tag.Value);
    }

    private static byte[] Serialize(HttpRequest request, IReadOnlyList<S3XmlWriter.Tag> tags) =>
        (byte[])typeof(MultipartUploadStateStore)
            .GetMethod("SerializeCapturedHeaders", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [request, tags])!;

    private static MultipartUploadStateStore.CapturedHeaders Deserialize(byte[] data)
    {
        var method = typeof(MultipartUploadStateStore)
            .GetMethod("TryDeserializeCapturedHeaders", BindingFlags.NonPublic | BindingFlags.Static)!;
        var args = new object?[] { data, null };
        var ok = (bool)method.Invoke(null, args)!;
        Assert.True(ok);
        return (MultipartUploadStateStore.CapturedHeaders)args[1]!;
    }

    /// <summary>
    /// Truncates a serialized record to just past the metadata section
    /// (Magic + flags + optional content headers + metadata count/entries),
    /// dropping the trailing tags section entirely — reproducing the exact
    /// byte shape of a record written before #799.
    /// </summary>
    private static byte[] StripTrailingTagsSection(byte[] serialized)
    {
        var method = typeof(MultipartUploadStateStore)
            .GetMethod("TryDeserializeCapturedHeaders", BindingFlags.NonPublic | BindingFlags.Static)!;
        // Progressively shrink the buffer from the end until the smallest
        // length that still deserializes with zero tags — that's exactly
        // the offset immediately after the metadata section (the trailing
        // 2-byte zero tag-count that this record still lacked).
        for (var len = serialized.Length - 1; len >= 0; len--)
        {
            var candidate = serialized[..len];
            var args = new object?[] { candidate, null };
            var ok = (bool)method.Invoke(null, args)!;
            if (ok)
            {
                var headers = (MultipartUploadStateStore.CapturedHeaders)args[1]!;
                if (headers.Tags.Count == 0)
                {
                    return candidate;
                }
            }
        }

        throw new InvalidOperationException("Could not locate the metadata-section boundary in the serialized record.");
    }
}
