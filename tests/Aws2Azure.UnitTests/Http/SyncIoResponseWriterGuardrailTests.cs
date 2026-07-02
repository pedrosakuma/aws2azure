using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Xml.Linq;
using Aws2Azure.Modules.DynamoDb.Operations;
using Aws2Azure.Modules.SecretsManager;
using Aws2Azure.Modules.SecretsManager.Operations;
using Aws2Azure.Modules.S3.Xml;
using Aws2Azure.Modules.Sns.Xml;
using Aws2Azure.Modules.Sqs.Xml;
using Aws2Azure.Modules.Sqs.WireProtocol;
using Aws2Azure.TestSupport.Http;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.UnitTests.Http;

/// <summary>
/// Cross-module guardrail for the repo-wide invariant (issue #449, follow-up to
/// #436/#448): a response writer must never anchor a <c>Utf8JsonWriter</c> /
/// <c>XmlWriter</c> at <c>context.Response.Body</c>. Such a writer auto-flushes
/// SYNCHRONOUSLY once its internal buffer (~16 KB) fills mid-serialization;
/// Kestrel runs with <c>AllowSynchronousIO=false</c>, so that flush throws AFTER
/// the status line is committed and the client reads a truncated body.
///
/// Each test drives a module's large (&gt;16 KB) response writer through
/// <see cref="SyncThrowingStream"/> — a body stream that rejects every
/// synchronous write/flush — and asserts the full body still landed. A future
/// regression to a stream-backed writer would fail here deterministically,
/// without needing an emulator or a perf run to surface it.
/// </summary>
public sealed class SyncIoResponseWriterGuardrailTests
{
    private const int SyncFlushThresholdBytes = 16 * 1024;

    private static (DefaultHttpContext Context, SyncThrowingStream Body) NewContext()
    {
        var context = new DefaultHttpContext();
        var body = new SyncThrowingStream();
        context.Response.Body = body;
        return (context, body);
    }

    private static async Task<byte[]> CompleteAndReadAsync(DefaultHttpContext context, SyncThrowingStream body)
    {
        await context.Response.BodyWriter.FlushAsync();
        return body.WrittenBytes;
    }

    [Fact]
    public async Task S3_ListObjectsV2_large_listing_writes_full_body_without_sync_io()
    {
        const int keyCount = 1000;
        var contents = new List<S3XmlWriter.ListedObject>(keyCount);
        for (var i = 0; i < keyCount; i++)
        {
            contents.Add(new S3XmlWriter.ListedObject(
                $"some/reasonably/deep/prefix/object-name-{i:D6}.bin",
                DateTimeOffset.UnixEpoch.AddSeconds(i),
                $"\"etag-{i:D6}\"",
                1024 + i,
                "STANDARD"));
        }

        // S3XmlWriter writes straight to the supplied stream, so the throwing
        // stream is the response body directly.
        var body = new SyncThrowingStream();
        await S3XmlWriter.WriteListObjectsV2ResultAsync(
            body,
            bucket: "guardrail-bucket",
            prefix: "some/",
            delimiter: "/",
            maxKeys: keyCount,
            keyCount: keyCount,
            isTruncated: false,
            continuationToken: null,
            nextContinuationToken: null,
            startAfter: null,
            encodeUrl: false,
            contents,
            Array.Empty<string>());

        var bytes = body.WrittenBytes;
        Assert.True(bytes.Length > SyncFlushThresholdBytes, $"payload was only {bytes.Length} bytes");
        XNamespace ns = "http://s3.amazonaws.com/doc/2006-03-01/";
        var doc = XDocument.Parse(Encoding.UTF8.GetString(bytes));
        Assert.Equal(keyCount, doc.Root!.Elements(ns + "Contents").Count());
    }

    [Fact]
    public async Task DynamoDb_Scan_large_page_writes_full_body_without_sync_io()
    {
        const int itemCount = 600;
        var items = new List<Dictionary<string, JsonElement>>(itemCount);
        for (var i = 0; i < itemCount; i++)
        {
            items.Add(new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["pk"] = Attr($"partition-key-{i:D6}"),
                ["sk"] = Attr($"sort-key-{i:D6}"),
                ["data"] = Attr($"some-reasonably-long-attribute-value-{i:D6}"),
            });
        }

        var response = new ScanResponse { Items = items, Count = itemCount, ScannedCount = itemCount };

        var (context, body) = NewContext();
        await CosmosOpsShared.WriteJsonAsync(context, 200, response, ScanJsonContext.Default.ScanResponse);
        var bytes = await CompleteAndReadAsync(context, body);

        Assert.True(bytes.Length > SyncFlushThresholdBytes, $"payload was only {bytes.Length} bytes");
        using var json = JsonDocument.Parse(bytes);
        Assert.Equal(itemCount, json.RootElement.GetProperty("Items").GetArrayLength());
    }

    [Fact]
    public async Task DynamoDb_GetItem_large_projected_item_writes_full_body_without_sync_io()
    {
        // A projected GetItem travels the materialized path and is committed via
        // CosmosOpsShared.WriteJsonBufferedAsync — guard that a >16 KB item lands
        // in full without any synchronous body write/flush.
        var item = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        for (var i = 0; i < 600; i++)
        {
            item[$"attribute-name-{i:D6}"] = Attr($"some-reasonably-long-attribute-value-{i:D6}");
        }
        var response = new GetItemResponse { Item = item };

        var (context, body) = NewContext();
        await CosmosOpsShared.WriteJsonBufferedAsync(
            context, 200, response, ItemJsonContext.Default.GetItemResponse, default);
        var bytes = await CompleteAndReadAsync(context, body);

        Assert.True(bytes.Length > SyncFlushThresholdBytes, $"payload was only {bytes.Length} bytes");
        using var json = JsonDocument.Parse(bytes);
        Assert.Equal(item.Count, json.RootElement.GetProperty("Item").EnumerateObject().Count());
    }

    [Theory]
    [InlineData(SqsWireProtocol.Query)]
    [InlineData(SqsWireProtocol.AwsJson)]
    public async Task Sqs_ReceiveMessage_large_batch_writes_full_body_without_sync_io(SqsWireProtocol protocol)
    {
        const int messageCount = 400;
        var payload = new string('y', 256);
        var messages = new List<ReceivedSqsMessage>(messageCount);
        for (var i = 0; i < messageCount; i++)
        {
            messages.Add(new ReceivedSqsMessage(
                MessageId: $"00000000-0000-0000-0000-{i:D12}",
                ReceiptHandle: $"receipt-handle-{i:D6}-{payload}",
                MD5OfBody: "0123456789abcdef0123456789abcdef",
                Body: payload,
                MD5OfMessageAttributes: null,
                Attributes: null,
                MessageAttributes: null));
        }

        var (context, body) = NewContext();
        await SqsResponseWriter.WriteReceiveMessageAsync(context, protocol, messages);
        var bytes = await CompleteAndReadAsync(context, body);

        Assert.True(bytes.Length > SyncFlushThresholdBytes, $"payload was only {bytes.Length} bytes");
        if (protocol == SqsWireProtocol.AwsJson)
        {
            using var json = JsonDocument.Parse(bytes);
            Assert.Equal(messageCount, json.RootElement.GetProperty("Messages").GetArrayLength());
        }
        else
        {
            var doc = XDocument.Parse(Encoding.UTF8.GetString(bytes));
            Assert.Equal(messageCount, doc.Descendants().Count(e => e.Name.LocalName == "Message"));
        }
    }

    [Fact]
    public async Task Sns_ListTopics_large_listing_writes_full_body_without_sync_io()
    {
        const int topicCount = 1500;
        var arns = new List<string>(topicCount);
        for (var i = 0; i < topicCount; i++)
        {
            arns.Add($"arn:aws:sns:us-east-1:123456789012:topic-with-a-fairly-long-name-{i:D6}");
        }

        var (context, body) = NewContext();
        await SnsResponseWriter.WriteListTopicsResponseAsync(context, arns, nextToken: null);
        var bytes = await CompleteAndReadAsync(context, body);

        Assert.True(bytes.Length > SyncFlushThresholdBytes, $"payload was only {bytes.Length} bytes");
        XNamespace ns = SnsResponseWriter.XmlNamespace;
        var doc = XDocument.Parse(Encoding.UTF8.GetString(bytes));
        Assert.Equal(topicCount, doc.Descendants(ns + "member").Count());
    }

    [Fact]
    public async Task SecretsManager_ListSecrets_large_listing_writes_full_body_without_sync_io()
    {
        const int secretCount = 800;
        var secrets = new List<ListSecretsItem>(secretCount);
        for (var i = 0; i < secretCount; i++)
        {
            secrets.Add(new ListSecretsItem(
                Arn: $"arn:aws:secretsmanager:azure:keyvault:secret:secret-name-{i:D6}",
                Name: $"secret-name-{i:D6}",
                Description: $"secret description {i:D6}",
                CreatedDate: DateTimeOffset.UnixEpoch.AddSeconds(i),
                LastChangedDate: DateTimeOffset.UnixEpoch.AddSeconds(i + 1),
                Tags: [new SecretsManagerTag("env", "test")],
                VersionIdsToStages: null));
        }

        var (context, body) = NewContext();
        await SecretsManagerOperationSupport.WriteJsonAsync(context, new ListSecretsResponse(secrets, null), SecretsManagerJsonContext.Default.ListSecretsResponse, CancellationToken.None);
        var bytes = await CompleteAndReadAsync(context, body);

        Assert.True(bytes.Length > SyncFlushThresholdBytes, $"payload was only {bytes.Length} bytes");
        using var json = JsonDocument.Parse(bytes);
        Assert.Equal(secretCount, json.RootElement.GetProperty("SecretList").GetArrayLength());
    }

    private static JsonElement Attr(string value)
        => JsonDocument.Parse($"{{\"S\":\"{value}\"}}").RootElement.Clone();
}
