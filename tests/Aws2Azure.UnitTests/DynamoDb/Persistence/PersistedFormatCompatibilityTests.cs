using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aws2Azure.Modules.DynamoDb.Internal;
using Aws2Azure.Modules.DynamoDb.Operations;
using Aws2Azure.Modules.DynamoDb.Persistence;

namespace Aws2Azure.UnitTests.DynamoDb.Persistence;

public sealed class PersistedFormatCompatibilityTests
{
    [Fact]
    public void Current_item_writer_matches_frozen_fixture()
    {
        using var item = JsonDocument.Parse(
            """
            {
              "pk":{"S":"partition-1"},
              "sk":{"S":"sort-1"},
              "name":{"S":"Alice"},
              "big":{"N":"99999999999999999999999999999999999999"},
              "blob":{"B":"aGVsbG8="},
              "ttl":{"N":"1700003600"}
            }
            """);

        var actual = InferredAttributeStorage.BuildCosmosDocument(
            "sort-1", "partition-1", item.RootElement);

        AssertJsonEqual(Fixture("current/item-document.json"), actual);
    }

    [Fact]
    public void Current_writer_matches_frozen_ttl_and_index_derived_fields_fixture()
    {
        using var item = JsonDocument.Parse(
            """
            {
              "pk":{"S":"partition-1"},
              "sk":{"S":"sort-1"},
              "gsk":{"N":"42"},
              "ttl":{"N":"1700003600"}
            }
            """);
        var metadata = new TableMetadata
        {
            AttributeDefinitions =
            [
                new() { Name = "pk", Type = "S" },
                new() { Name = "sk", Type = "S" },
                new() { Name = "gsk", Type = "N" },
            ],
            KeySchema =
            [
                new() { Name = "pk", KeyType = "HASH" },
                new() { Name = "sk", KeyType = "RANGE" },
            ],
            GlobalSecondaryIndexes =
            [
                new()
                {
                    IndexName = "byAmount",
                    KeySchema =
                    [
                        new() { Name = "pk", KeyType = "HASH" },
                        new() { Name = "gsk", KeyType = "RANGE" },
                    ],
                },
            ],
        };
        var orderKeys = SecondaryIndexOrderKeys.Compute(metadata, item.RootElement);
        var buffer = new ArrayBufferWriter<byte>();

        InferredAttributeStorage.WriteCosmosDocument(
            buffer,
            "sort-1",
            "partition-1",
            item.RootElement,
            ttlSeconds: 3600,
            orderKeys);

        AssertJsonEqual(
            Fixture("current/item-derived-fields.json"),
            Encoding.UTF8.GetString(buffer.WrittenSpan));
    }

    [Fact]
    public void Current_dom_and_streaming_readers_read_frozen_v1_item_envelope()
    {
        var legacy = Fixture("v1/item-envelope.json");
        using var document = JsonDocument.Parse(legacy);

        var materialized = InferredAttributeStorage.ExtractItem(document.RootElement);
        Assert.NotNull(materialized);
        Assert.Equal("partition-1", materialized!["pk"].GetProperty("S").GetString());
        Assert.Equal(
            "99999999999999999999999999999999999999",
            materialized["big"].GetProperty("N").GetString());
        Assert.Equal("aGVsbG8=", materialized["blob"].GetProperty("B").GetString());

        var output = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(output))
        {
            InferredAttributeStorage.WriteGetItemEnvelope(
                writer,
                Encoding.UTF8.GetBytes(legacy));
        }

        using var response = JsonDocument.Parse(output.WrittenMemory);
        Assert.True(response.RootElement.TryGetProperty("Item", out var streamed));
        Assert.True(JsonElement.DeepEquals(
            document.RootElement.GetProperty("item"),
            streamed));

        var binary = Aws2Azure.UnitTests.DynamoDb.CosmosBinaryTestEncoder.Encode(legacy);
        var binaryReader = new CosmosBinaryReader(binary);
        try
        {
            output = new ArrayBufferWriter<byte>();
            using var writer = new Utf8JsonWriter(output);
            InferredAttributeStorage.WriteGetItemEnvelope(writer, ref binaryReader);
            writer.Flush();
        }
        finally
        {
            binaryReader.Dispose();
        }

        using var binaryResponse = JsonDocument.Parse(output.WrittenMemory);
        Assert.True(JsonElement.DeepEquals(
            document.RootElement.GetProperty("item"),
            binaryResponse.RootElement.GetProperty("Item")));
    }

    [Fact]
    public void Frozen_v1_export_transforms_into_current_v2_document()
    {
        using var legacy = JsonDocument.Parse(Fixture("v1/item-envelope.json"));
        var item = InferredAttributeStorage.ExtractItem(legacy.RootElement);
        Assert.NotNull(item);

        var itemBuffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(itemBuffer))
        {
            writer.WriteStartObject();
            foreach (var attribute in item!)
            {
                writer.WritePropertyName(attribute.Key);
                attribute.Value.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        using var itemDocument = JsonDocument.Parse(itemBuffer.WrittenMemory);
        var transformed = InferredAttributeStorage.BuildCosmosDocument(
            legacy.RootElement.GetProperty("id").GetString()!,
            legacy.RootElement.GetProperty("pk").GetString()!,
            itemDocument.RootElement);

        AssertJsonEqual(Fixture("current/item-document.json"), transformed);
    }

    [Fact]
    public void Current_streaming_reader_tolerates_export_import_property_reordering()
    {
        const string reordered =
            """
            {
              "pk":"partition-1",
              "name":"Alice",
              "id":"sort-1",
              "_a2a":"item",
              "_a2a_pk":"partition-1"
            }
            """;
        var output = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(output))
        {
            InferredAttributeStorage.WriteGetItemEnvelope(
                writer,
                Encoding.UTF8.GetBytes(reordered));
        }

        using var response = JsonDocument.Parse(output.WrittenMemory);
        var item = response.RootElement.GetProperty("Item");
        Assert.Equal("partition-1", item.GetProperty("pk").GetProperty("S").GetString());
        Assert.Equal("Alice", item.GetProperty("name").GetProperty("S").GetString());
    }

    [Fact]
    public void Current_streaming_reader_disambiguates_reordered_item_property()
    {
        const string reorderedLegacy =
            """
            {
              "item":{"pk":{"S":"partition-1"},"item":{"S":"legacy-user-value"}},
              "_a2a":"item",
              "pk":"partition-1",
              "id":"sort-1"
            }
            """;
        const string reorderedCurrent =
            """
            {
              "item":"current-user-value",
              "pk":"partition-1",
              "_a2a":"item",
              "id":"sort-1",
              "_a2a_pk":"partition-1"
            }
            """;

        Assert.Equal(
            "legacy-user-value",
            StreamItem(reorderedLegacy).GetProperty("item").GetProperty("S").GetString());
        Assert.True(JsonElement.DeepEquals(
            StreamItem(reorderedLegacy),
            StreamBinaryItem(reorderedLegacy)));
        Assert.Equal(
            "current-user-value",
            StreamItem(reorderedCurrent).GetProperty("item").GetProperty("S").GetString());
        Assert.True(JsonElement.DeepEquals(
            StreamItem(reorderedCurrent),
            StreamBinaryItem(reorderedCurrent)));
    }

    [Fact]
    public void V1_table_metadata_rewrite_preserves_unknown_fields_and_emits_current_version()
    {
        var legacy = Fixture("v1/table-metadata.json");
        var metadata = JsonSerializer.Deserialize(
            legacy,
            TableMetadataJsonContext.Default.TableMetadata);
        Assert.NotNull(metadata);
        metadata!.RemoveCosmosSystemExtensionData();
        metadata.BillingMode = "PROVISIONED";

        var rewritten = JsonSerializer.Serialize(
            metadata,
            TableMetadataJsonContext.Default.TableMetadata);
        using var document = JsonDocument.Parse(rewritten);
        var root = document.RootElement;

        Assert.Equal(DynamoDbPersistedFormatContract.TableMetadataVersion,
            root.GetProperty("formatVersion").GetInt32());
        Assert.Equal("PROVISIONED", root.GetProperty("billingMode").GetString());
        Assert.Equal("adjacent-runtime",
            root.GetProperty("futurePolicy").GetProperty("writer").GetString());
        Assert.Equal("lossless",
            root.GetProperty("attributeDefinitions")[2]
                .GetProperty("futureScalarRule").GetString());
        Assert.Equal(7,
            root.GetProperty("globalSecondaryIndexes")[0]
                .GetProperty("futureProjection").GetProperty("revision").GetInt32());
        Assert.Equal("absolute-epoch",
            root.GetProperty("timeToLive").GetProperty("futureTtlMode").GetString());
        Assert.False(root.TryGetProperty("_rid", out _));
        Assert.False(root.TryGetProperty("_etag", out _));
        Assert.False(root.TryGetProperty("_ts", out _));
    }

    [Fact]
    public void Current_table_metadata_fixture_round_trips_without_contract_drift()
    {
        var frozen = Fixture("current/table-metadata.json");
        var metadata = JsonSerializer.Deserialize(
            frozen,
            TableMetadataJsonContext.Default.TableMetadata);
        Assert.NotNull(metadata);

        var actual = JsonSerializer.Serialize(
            metadata,
            TableMetadataJsonContext.Default.TableMetadata);

        AssertJsonEqual(frozen, actual);
    }

    [Fact]
    public void Current_reader_accepts_frozen_v1_continuation_formats()
    {
        using var basic = JsonDocument.Parse(Fixture("v1/continuation-basic.json"));
        Assert.Equal(
            "cosmos-ct-v1",
            DynamoDbContinuationTokenCodec.Extract(basic.RootElement));
        AssertJsonEqual(
            basic.RootElement.GetRawText(),
            JsonSerializer.Serialize(
                DynamoDbContinuationTokenCodec.BuildKey("cosmos-ct-v1")));

        using var ordered = JsonDocument.Parse(Fixture("v1/continuation-ordered.json"));
        var token = CrossPartitionOrderByQuery.DecodeToken(ordered.RootElement);
        Assert.NotNull(token);
        Assert.True(token!.Forward);
        Assert.Equal(3, token.Skip);
        Assert.Equal("42", token.BoundaryValue.GetProperty("N").GetString());
        AssertJsonEqual(
            ordered.RootElement.GetRawText(),
            JsonSerializer.Serialize(CrossPartitionOrderByQuery.BuildTokenKey(token)));
    }

    [Fact]
    public void Continuation_codec_preserves_non_object_sentinel_as_no_continuation()
    {
        using var key = JsonDocument.Parse(
            "{\"__a2a_continuation\":\"not-a-typed-string\"}");
        Assert.Null(DynamoDbContinuationTokenCodec.Extract(key.RootElement));
    }

    [Fact]
    public void Stored_procedure_ids_and_body_hashes_match_frozen_identity_set()
    {
        var atomicHash = Sha256(SprocManager.SprocBody);
        var transactHash = Sha256(SprocManager.TransactSprocBody);

        Assert.Equal(
            DynamoDbPersistedFormatContract.AtomicWriteBodySha256,
            atomicHash);
        Assert.Equal(
            DynamoDbPersistedFormatContract.AtomicTransactWriteBodySha256,
            transactHash);

        using var frozen = JsonDocument.Parse(Fixture("current/stored-procedures.json"));
        var identities = frozen.RootElement.GetProperty("storedProcedures");
        Assert.Equal(
            SprocManager.SprocId,
            identities[0].GetProperty("id").GetString());
        Assert.Equal(
            atomicHash,
            identities[0].GetProperty("bodySha256").GetString());
        Assert.Equal(
            SprocManager.TransactSprocId,
            identities[1].GetProperty("id").GetString());
        Assert.Equal(
            transactHash,
            identities[1].GetProperty("bodySha256").GetString());
    }

    [Fact]
    public void Published_inventory_matches_runtime_versions_and_frozen_fixtures()
    {
        var root = RepositoryRoot();
        var inventoryPath = Path.Combine(
            root,
            "docs/compatibility/dynamodb-persisted-formats-v1.json");
        using var inventory = JsonDocument.Parse(File.ReadAllText(inventoryPath));

        Assert.Equal(
            DynamoDbPersistedFormatContract.InventoryVersion,
            inventory.RootElement.GetProperty("inventory_version").GetInt32());
        foreach (var format in inventory.RootElement.GetProperty("formats").EnumerateArray())
        {
            AssertInventoryVersions(format);
            foreach (var propertyName in new[] { "v1_fixture", "current_fixture" })
            {
                if (format.TryGetProperty(propertyName, out var fixture))
                {
                    var fixturePath = Path.Combine(root, fixture.GetString()!);
                    Assert.True(
                        File.Exists(fixturePath),
                        $"missing frozen fixture {fixture.GetString()}");
                    var expectedHash = format.GetProperty(propertyName + "_sha256").GetString();
                    var actualHash = Convert.ToHexString(
                            SHA256.HashData(File.ReadAllBytes(fixturePath)))
                        .ToLowerInvariant();
                    Assert.Equal(expectedHash, actualHash);
                }
            }
        }

        var storedProcedures = inventory.RootElement.GetProperty("stored_procedures");
        Assert.Equal(SprocManager.SprocId,
            storedProcedures[0].GetProperty("id").GetString());
        Assert.Equal(Sha256(SprocManager.SprocBody),
            storedProcedures[0].GetProperty("body_sha256").GetString());
        Assert.Equal(SprocManager.TransactSprocId,
            storedProcedures[1].GetProperty("id").GetString());
        Assert.Equal(Sha256(SprocManager.TransactSprocBody),
            storedProcedures[1].GetProperty("body_sha256").GetString());

        using var identityFixture = JsonDocument.Parse(
            Fixture("current/stored-procedures.json"));
        Assert.Equal(
            DynamoDbPersistedFormatContract.StoredProcedureIdentityVersion,
            identityFixture.RootElement.GetProperty("identityVersion").GetInt32());
    }

    private static void AssertInventoryVersions(JsonElement format)
    {
        var id = format.GetProperty("id").GetString();
        var current = format.GetProperty("current_write_version").GetInt32();
        var readers = format.GetProperty("read_versions")
            .EnumerateArray()
            .Select(value => value.GetInt32())
            .ToArray();

        switch (id)
        {
            case "item-envelope":
                Assert.Equal(
                    DynamoDbPersistedFormatContract.CurrentItemDocumentVersion,
                    current);
                Assert.Equal(
                    [
                        DynamoDbPersistedFormatContract.LegacyItemEnvelopeVersion,
                        DynamoDbPersistedFormatContract.CurrentItemDocumentVersion,
                    ],
                    readers);
                break;
            case "table-metadata":
                Assert.Equal(DynamoDbPersistedFormatContract.TableMetadataVersion, current);
                Assert.Equal([DynamoDbPersistedFormatContract.TableMetadataVersion], readers);
                break;
            case "query-scan-continuation":
                Assert.Equal(DynamoDbPersistedFormatContract.ContinuationVersion, current);
                Assert.Equal([DynamoDbPersistedFormatContract.ContinuationVersion], readers);
                break;
            case "ordered-query-continuation":
                Assert.Equal(
                    DynamoDbPersistedFormatContract.OrderedContinuationVersion,
                    current);
                Assert.Equal(
                    [DynamoDbPersistedFormatContract.OrderedContinuationVersion],
                    readers);
                break;
            case "stored-procedure-identities":
                Assert.Equal(
                    DynamoDbPersistedFormatContract.StoredProcedureIdentityVersion,
                    current);
                Assert.Equal(
                    [DynamoDbPersistedFormatContract.StoredProcedureIdentityVersion],
                    readers);
                break;
            case "ttl-index-derived-fields":
                Assert.Equal(DynamoDbPersistedFormatContract.DerivedFieldVersion, current);
                Assert.Equal([DynamoDbPersistedFormatContract.DerivedFieldVersion], readers);
                break;
            default:
                Assert.Fail($"Unknown persisted-format inventory row '{id}'.");
                break;
        }
    }

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private static string Fixture(string relativePath) =>
        File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "tests/Aws2Azure.UnitTests/DynamoDb/Persistence/Fixtures",
            relativePath));

    private static JsonElement StreamItem(string document)
    {
        var output = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(output))
        {
            InferredAttributeStorage.WriteGetItemEnvelope(
                writer,
                Encoding.UTF8.GetBytes(document));
        }
        using var response = JsonDocument.Parse(output.WrittenMemory);
        return response.RootElement.GetProperty("Item").Clone();
    }

    private static JsonElement StreamBinaryItem(string document)
    {
        var binary = Aws2Azure.UnitTests.DynamoDb.CosmosBinaryTestEncoder.Encode(document);
        var reader = new CosmosBinaryReader(binary);
        var output = new ArrayBufferWriter<byte>();
        try
        {
            using var writer = new Utf8JsonWriter(output);
            InferredAttributeStorage.WriteGetItemEnvelope(writer, ref reader);
            writer.Flush();
        }
        finally
        {
            reader.Dispose();
        }
        using var response = JsonDocument.Parse(output.WrittenMemory);
        return response.RootElement.GetProperty("Item").Clone();
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "aws2azure.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate repository root.");
    }

    private static void AssertJsonEqual(string expected, string actual)
    {
        using var expectedDocument = JsonDocument.Parse(expected);
        using var actualDocument = JsonDocument.Parse(actual);
        Assert.True(
            JsonElement.DeepEquals(
                expectedDocument.RootElement,
                actualDocument.RootElement),
            $"JSON differs.\nExpected: {expected}\nActual: {actual}");
    }
}
