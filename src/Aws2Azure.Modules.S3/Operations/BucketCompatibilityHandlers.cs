using System.Text;
using System.Xml;
using Aws2Azure.Modules.S3.Errors;
using Aws2Azure.Modules.S3.Internal;
using Aws2Azure.Modules.S3.Xml;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.S3.Operations;

internal static partial class SubresourceHandlers
{
    private const long MaxConfigurationBodyBytes = 64 * 1024;
    private const string BucketOwnershipMetadataKey = "aws2azureownership";
    private const string PublicAccessBlockMetadataKey = "aws2azurepublicaccessblock";
    private const string BucketEncryptionMetadataKey = "aws2azureencryption";

    private static async Task GetBucketOwnershipControlsAsync(
        HttpContext context, BlobClient blob, string bucket, CancellationToken ct)
    {
        var (ok, value) = await ReadContainerMetadataValueAsync(
            context, blob, bucket, S3Operation.GetBucketOwnershipControls, BucketOwnershipMetadataKey, ct)
            .ConfigureAwait(false);
        if (!ok) return;
        if (value is not ("BucketOwnerEnforced" or "BucketOwnerPreferred" or "ObjectWriter"))
        {
            await S3ErrorMapping.WriteAsync(context, S3ErrorMapping.NoSuchConfiguration(
                "OwnershipControlsNotFoundError", "ownership controls configuration")).ConfigureAwait(false);
            return;
        }

        await WriteXmlAsync(context, S3XmlWriter.OwnershipControls(value)).ConfigureAwait(false);
    }

    private static async Task PutBucketOwnershipControlsAsync(
        HttpContext context, BlobClient blob, string bucket, CancellationToken ct)
    {
        var xml = await ReadConfigurationXmlAsync(context, ct).ConfigureAwait(false);
        if (xml is null || !TryParseOwnershipControls(xml, out var objectOwnership))
        {
            await S3ErrorMapping.WriteAsync(context, MalformedXml()).ConfigureAwait(false);
            return;
        }

        if (!await MutateContainerMetadataAsync(
                context, blob, bucket, S3Operation.PutBucketOwnershipControls,
                BucketOwnershipMetadataKey, objectOwnership, ct).ConfigureAwait(false)) return;
        context.Response.StatusCode = StatusCodes.Status200OK;
    }

    private static async Task DeleteBucketOwnershipControlsAsync(
        HttpContext context, BlobClient blob, string bucket, CancellationToken ct)
    {
        if (!await MutateContainerMetadataAsync(
                context, blob, bucket, S3Operation.DeleteBucketOwnershipControls,
                BucketOwnershipMetadataKey, null, ct).ConfigureAwait(false)) return;
        context.Response.StatusCode = StatusCodes.Status204NoContent;
    }

    private static async Task GetPublicAccessBlockAsync(
        HttpContext context, BlobClient blob, string bucket, CancellationToken ct)
    {
        var (ok, value) = await ReadContainerMetadataValueAsync(
            context, blob, bucket, S3Operation.GetPublicAccessBlock, PublicAccessBlockMetadataKey, ct)
            .ConfigureAwait(false);
        if (!ok) return;
        if (!TryDecodePublicAccessBlock(value, out var intent))
        {
            await S3ErrorMapping.WriteAsync(context, S3ErrorMapping.NoSuchConfiguration(
                "NoSuchPublicAccessBlockConfiguration", "public access block configuration")).ConfigureAwait(false);
            return;
        }

        await WriteXmlAsync(context, S3XmlWriter.PublicAccessBlockConfiguration(
            intent.BlockPublicAcls,
            intent.IgnorePublicAcls,
            intent.BlockPublicPolicy,
            intent.RestrictPublicBuckets)).ConfigureAwait(false);
    }

    private static async Task PutPublicAccessBlockAsync(
        HttpContext context, BlobClient blob, string bucket, CancellationToken ct)
    {
        var xml = await ReadConfigurationXmlAsync(context, ct).ConfigureAwait(false);
        if (xml is null || !TryParsePublicAccessBlock(xml, out var intent))
        {
            await S3ErrorMapping.WriteAsync(context, MalformedXml()).ConfigureAwait(false);
            return;
        }

        var encoded = EncodePublicAccessBlock(intent);
        if (!await MutateContainerMetadataAsync(
                context, blob, bucket, S3Operation.PutPublicAccessBlock,
                PublicAccessBlockMetadataKey, encoded, ct).ConfigureAwait(false)) return;
        context.Response.StatusCode = StatusCodes.Status200OK;
    }

    private static async Task DeletePublicAccessBlockAsync(
        HttpContext context, BlobClient blob, string bucket, CancellationToken ct)
    {
        if (!await MutateContainerMetadataAsync(
                context, blob, bucket, S3Operation.DeletePublicAccessBlock,
                PublicAccessBlockMetadataKey, null, ct).ConfigureAwait(false)) return;
        context.Response.StatusCode = StatusCodes.Status204NoContent;
    }

    private static async Task GetBucketEncryptionAsync(
        HttpContext context, BlobClient blob, string bucket, CancellationToken ct)
    {
        var (ok, value) = await ReadContainerMetadataValueAsync(
            context, blob, bucket, S3Operation.GetBucketEncryption, BucketEncryptionMetadataKey, ct)
            .ConfigureAwait(false);
        if (!ok) return;
        if (!string.IsNullOrEmpty(value)
            && !string.Equals(value, "AES256", StringComparison.Ordinal))
        {
            await S3ErrorMapping.WriteAsync(context, S3ErrorMapping.NoSuchConfiguration(
                "ServerSideEncryptionConfigurationNotFoundError",
                "server side encryption configuration")).ConfigureAwait(false);
            return;
        }

        await WriteXmlAsync(context, S3XmlWriter.BucketEncryptionAes256()).ConfigureAwait(false);
    }

    private static async Task PutBucketEncryptionAsync(
        HttpContext context, BlobClient blob, string bucket, CancellationToken ct)
    {
        var xml = await ReadConfigurationXmlAsync(context, ct).ConfigureAwait(false);
        if (xml is null || !TryParseBucketEncryption(
                xml, out var algorithm, out var hasUnsupportedConfiguration))
        {
            await S3ErrorMapping.WriteAsync(context, MalformedXml()).ConfigureAwait(false);
            return;
        }
        if (!string.Equals(algorithm, "AES256", StringComparison.Ordinal)
            || hasUnsupportedConfiguration)
        {
            await S3ErrorMapping.WriteAsync(context, S3ErrorMapping.NotImplemented(
                S3Operation.PutBucketEncryption)).ConfigureAwait(false);
            return;
        }

        if (!await MutateContainerMetadataAsync(
                context, blob, bucket, S3Operation.PutBucketEncryption,
                BucketEncryptionMetadataKey, "AES256", ct).ConfigureAwait(false)) return;
        context.Response.StatusCode = StatusCodes.Status200OK;
    }

    private static async Task DeleteBucketEncryptionAsync(
        HttpContext context, BlobClient blob, string bucket, CancellationToken ct)
    {
        if (!await MutateContainerMetadataAsync(
                context, blob, bucket, S3Operation.DeleteBucketEncryption,
                BucketEncryptionMetadataKey, null, ct).ConfigureAwait(false)) return;
        context.Response.StatusCode = StatusCodes.Status204NoContent;
    }

    private static async Task PutBucketRequestPaymentAsync(HttpContext context, CancellationToken ct)
    {
        var xml = await ReadConfigurationXmlAsync(context, ct).ConfigureAwait(false);
        if (xml is null || !TryParseSingleValueConfiguration(
                xml, "RequestPaymentConfiguration", "Payer", out var payer))
        {
            await S3ErrorMapping.WriteAsync(context, MalformedXml()).ConfigureAwait(false);
            return;
        }
        if (!string.Equals(payer, "BucketOwner", StringComparison.Ordinal))
        {
            await S3ErrorMapping.WriteAsync(context, S3ErrorMapping.NotImplemented(
                S3Operation.PutBucketRequestPayment)).ConfigureAwait(false);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
    }

    private static async Task PutBucketAccelerateConfigurationAsync(HttpContext context, CancellationToken ct)
    {
        var xml = await ReadConfigurationXmlAsync(context, ct).ConfigureAwait(false);
        if (xml is null || !TryParseSingleValueConfiguration(
                xml, "AccelerateConfiguration", "Status", out var status))
        {
            await S3ErrorMapping.WriteAsync(context, MalformedXml()).ConfigureAwait(false);
            return;
        }
        if (!string.Equals(status, "Suspended", StringComparison.Ordinal))
        {
            await S3ErrorMapping.WriteAsync(context, S3ErrorMapping.NotImplemented(
                S3Operation.PutBucketAccelerateConfiguration)).ConfigureAwait(false);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
    }

    private static async Task<(bool Ok, string? Value)> ReadContainerMetadataValueAsync(
        HttpContext context,
        BlobClient blob,
        string bucket,
        S3Operation operation,
        string metadataKey,
        CancellationToken ct)
    {
        using var response = await blob.GetContainerMetadataAsync(bucket, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            await S3ErrorMapping.WriteAsync(context, S3ErrorMapping.FromAzure(response, operation)).ConfigureAwait(false);
            return (false, null);
        }

        var metadata = BlobClient.ReadContainerMetadata(response);
        return (true, metadata.TryGetValue(metadataKey, out var value) ? value : null);
    }

    private static async Task<string?> ReadConfigurationXmlAsync(HttpContext context, CancellationToken ct)
    {
        if ((context.Request.ContentLength ?? 0) > MaxConfigurationBodyBytes)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        var bytes = new byte[8 * 1024];
        long total = 0;
        int read;
        while ((read = await context.Request.Body.ReadAsync(bytes.AsMemory(), ct).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > MaxConfigurationBodyBytes)
            {
                return null;
            }
            buffer.Write(bytes, 0, read);
        }
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
    }

    private static bool TryParseOwnershipControls(string xml, out string objectOwnership)
    {
        objectOwnership = string.Empty;
        try
        {
            using var reader = CreateConfigurationReader(xml);
            if (!MoveToElement(reader, "OwnershipControls")) return false;
            reader.ReadStartElement();
            if (!MoveToElement(reader, "Rule")) return false;
            reader.ReadStartElement();
            if (!MoveToElement(reader, "ObjectOwnership")) return false;
            objectOwnership = reader.ReadElementContentAsString();
            if (!ReadRequiredEnd(reader, "Rule") || !ReadRequiredEnd(reader, "OwnershipControls")
                || !ReadToDocumentEnd(reader)) return false;
            return objectOwnership is "BucketOwnerEnforced" or "BucketOwnerPreferred" or "ObjectWriter";
        }
        catch (XmlException)
        {
            return false;
        }
    }

    private static bool TryParsePublicAccessBlock(string xml, out PublicAccessBlockIntent intent)
    {
        intent = default;
        try
        {
            using var reader = CreateConfigurationReader(xml);
            if (!MoveToElement(reader, "PublicAccessBlockConfiguration")) return false;
            reader.ReadStartElement();
            var blockPublicAcls = false;
            var ignorePublicAcls = false;
            var blockPublicPolicy = false;
            var restrictPublicBuckets = false;
            var sawBlockPublicAcls = false;
            var sawIgnorePublicAcls = false;
            var sawBlockPublicPolicy = false;
            var sawRestrictPublicBuckets = false;
            while (reader.MoveToContent() == XmlNodeType.Element)
            {
                var name = reader.LocalName;
                var value = reader.ReadElementContentAsString();
                if (!TryParseBoolean(value, out var parsed)) return false;
                switch (name)
                {
                    case "BlockPublicAcls" when !sawBlockPublicAcls:
                        blockPublicAcls = parsed;
                        sawBlockPublicAcls = true;
                        break;
                    case "IgnorePublicAcls" when !sawIgnorePublicAcls:
                        ignorePublicAcls = parsed;
                        sawIgnorePublicAcls = true;
                        break;
                    case "BlockPublicPolicy" when !sawBlockPublicPolicy:
                        blockPublicPolicy = parsed;
                        sawBlockPublicPolicy = true;
                        break;
                    case "RestrictPublicBuckets" when !sawRestrictPublicBuckets:
                        restrictPublicBuckets = parsed;
                        sawRestrictPublicBuckets = true;
                        break;
                    default:
                        return false;
                }
            }
            if (!ReadRequiredEnd(reader, "PublicAccessBlockConfiguration") || !ReadToDocumentEnd(reader)) return false;
            intent = new PublicAccessBlockIntent(
                blockPublicAcls,
                ignorePublicAcls,
                blockPublicPolicy,
                restrictPublicBuckets);
            return true;
        }
        catch (XmlException)
        {
            return false;
        }
    }

    private static bool TryParseBucketEncryption(
        string xml, out string algorithm, out bool hasUnsupportedConfiguration)
    {
        algorithm = string.Empty;
        hasUnsupportedConfiguration = false;
        try
        {
            using var reader = CreateConfigurationReader(xml);
            if (!MoveToElement(reader, "ServerSideEncryptionConfiguration")) return false;
            reader.ReadStartElement();
            if (!MoveToElement(reader, "Rule")) return false;
            reader.ReadStartElement();
            if (!MoveToElement(reader, "ApplyServerSideEncryptionByDefault")) return false;
            reader.ReadStartElement();
            var sawAlgorithm = false;
            var sawKmsMasterKey = false;
            while (reader.MoveToContent() == XmlNodeType.Element)
            {
                switch (reader.LocalName)
                {
                    case "SSEAlgorithm" when !sawAlgorithm:
                        algorithm = reader.ReadElementContentAsString();
                        sawAlgorithm = true;
                        break;
                    case "KMSMasterKeyID" when !sawKmsMasterKey:
                        _ = reader.ReadElementContentAsString();
                        sawKmsMasterKey = true;
                        hasUnsupportedConfiguration = true;
                        break;
                    default:
                        return false;
                }
            }
            if (!ReadRequiredEnd(reader, "ApplyServerSideEncryptionByDefault")) return false;
            while (reader.MoveToContent() == XmlNodeType.Element)
            {
                switch (reader.LocalName)
                {
                    case "BucketKeyEnabled":
                        if (!TryParseBoolean(reader.ReadElementContentAsString(), out var bucketKeyEnabled)) return false;
                        hasUnsupportedConfiguration |= bucketKeyEnabled;
                        break;
                    case "BlockedEncryptionTypes":
                        reader.Skip();
                        hasUnsupportedConfiguration = true;
                        break;
                    default:
                        return false;
                }
            }
            if (!ReadRequiredEnd(reader, "Rule")) return false;
            while (reader.MoveToContent() == XmlNodeType.Element)
            {
                if (!string.Equals(reader.LocalName, "Rule", StringComparison.Ordinal)) return false;
                reader.Skip();
                hasUnsupportedConfiguration = true;
            }
            return ReadRequiredEnd(reader, "ServerSideEncryptionConfiguration")
                && ReadToDocumentEnd(reader)
                && sawAlgorithm;
        }
        catch (XmlException)
        {
            return false;
        }
    }

    private static bool TryParseSingleValueConfiguration(
        string xml, string rootName, string valueName, out string value)
    {
        value = string.Empty;
        try
        {
            using var reader = CreateConfigurationReader(xml);
            if (!MoveToElement(reader, rootName)) return false;
            reader.ReadStartElement();
            if (!MoveToElement(reader, valueName)) return false;
            value = reader.ReadElementContentAsString();
            return ReadRequiredEnd(reader, rootName) && ReadToDocumentEnd(reader);
        }
        catch (XmlException)
        {
            return false;
        }
    }

    private static XmlReader CreateConfigurationReader(string xml) =>
        XmlReader.Create(new StringReader(xml), new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreWhitespace = true,
            IgnoreComments = true,
        });

    private static bool MoveToElement(XmlReader reader, string localName) =>
        reader.MoveToContent() == XmlNodeType.Element
        && string.Equals(reader.LocalName, localName, StringComparison.Ordinal);

    private static bool ReadRequiredEnd(XmlReader reader, string localName)
    {
        if (reader.MoveToContent() != XmlNodeType.EndElement
            || !string.Equals(reader.LocalName, localName, StringComparison.Ordinal))
        {
            return false;
        }
        reader.ReadEndElement();
        return true;
    }

    private static bool ReadToDocumentEnd(XmlReader reader) =>
        reader.MoveToContent() == XmlNodeType.None;

    private static bool TryParseBoolean(string value, out bool parsed)
    {
        if (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) || value == "1")
        {
            parsed = true;
            return true;
        }
        if (string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) || value == "0")
        {
            parsed = false;
            return true;
        }
        parsed = false;
        return false;
    }

    private static string EncodePublicAccessBlock(PublicAccessBlockIntent intent) =>
        string.Create(4, intent, static (span, value) =>
        {
            span[0] = value.BlockPublicAcls ? '1' : '0';
            span[1] = value.IgnorePublicAcls ? '1' : '0';
            span[2] = value.BlockPublicPolicy ? '1' : '0';
            span[3] = value.RestrictPublicBuckets ? '1' : '0';
        });

    private static bool TryDecodePublicAccessBlock(string? value, out PublicAccessBlockIntent intent)
    {
        intent = default;
        if (value is not { Length: 4 }) return false;
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] is not ('0' or '1')) return false;
        }
        intent = new PublicAccessBlockIntent(
            value[0] == '1',
            value[1] == '1',
            value[2] == '1',
            value[3] == '1');
        return true;
    }

    private readonly record struct PublicAccessBlockIntent(
        bool BlockPublicAcls,
        bool IgnorePublicAcls,
        bool BlockPublicPolicy,
        bool RestrictPublicBuckets);
}
