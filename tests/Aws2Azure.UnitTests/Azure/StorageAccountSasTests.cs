using System;
using System.Web;
using Aws2Azure.Core.Azure;
using Xunit;

namespace Aws2Azure.UnitTests.Azure;

public class StorageAccountSasTests
{
    [Fact]
    public void Generate_BuildsExpectedQueryStringFields()
    {
        const string key = "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";
        var expiry = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

        var sas = StorageAccountSas.Generate(
            "devstoreaccount1",
            key,
            StorageSasPermissions.Read | StorageSasPermissions.Write | StorageSasPermissions.List,
            StorageSasServices.Blob | StorageSasServices.Queue,
            StorageSasResourceTypes.Service | StorageSasResourceTypes.Container | StorageSasResourceTypes.Object,
            expiry);

        var parsed = HttpUtility.ParseQueryString(sas);
        Assert.Equal("2020-12-06", parsed["sv"]);
        Assert.Equal("bq", parsed["ss"]);
        Assert.Equal("sco", parsed["srt"]);
        Assert.Equal("rwl", parsed["sp"]);
        Assert.Equal("2026-06-01T12:00:00Z", parsed["se"]);
        Assert.Equal("https", parsed["spr"]);
        Assert.False(string.IsNullOrEmpty(parsed["sig"]));
    }

    [Fact]
    public void Generate_PermissionOrderIsCanonical()
    {
        const string key = "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";
        var sas = StorageAccountSas.Generate(
            "acct",
            key,
            StorageSasPermissions.Process | StorageSasPermissions.Read | StorageSasPermissions.Delete,
            StorageSasServices.Blob,
            StorageSasResourceTypes.Object,
            DateTimeOffset.UtcNow.AddHours(1));
        var parsed = HttpUtility.ParseQueryString(sas);
        Assert.Equal("rdp", parsed["sp"]);
    }
}
