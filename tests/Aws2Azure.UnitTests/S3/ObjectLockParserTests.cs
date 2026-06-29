using Aws2Azure.Modules.S3.Xml;

namespace Aws2Azure.UnitTests.S3;

public class ObjectLockParserTests
{
    [Fact]
    public void Parses_retention_with_mode_before_retain_until_date()
    {
        // AWS SDK emits Mode first then RetainUntilDate. Regression: the cursor
        // double-advanced past RetainUntilDate, yielding null -> MalformedXML.
        var xml = "<Retention xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\">" +
                  "<Mode>GOVERNANCE</Mode>" +
                  "<RetainUntilDate>2030-01-02T03:04:05.000Z</RetainUntilDate></Retention>";
        var (mode, until) = AzureBlobXmlReader.ParseRetention(xml);
        Assert.Equal("GOVERNANCE", mode);
        Assert.Equal(new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero), until);
    }

    [Fact]
    public void Parses_retention_with_reversed_order()
    {
        var xml = "<Retention><RetainUntilDate>2030-01-02T03:04:05Z</RetainUntilDate>" +
                  "<Mode>COMPLIANCE</Mode></Retention>";
        var (mode, until) = AzureBlobXmlReader.ParseRetention(xml);
        Assert.Equal("COMPLIANCE", mode);
        Assert.Equal(new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero), until);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<Retention><Mode>BOGUS</Mode><RetainUntilDate>2030-01-02T03:04:05Z</RetainUntilDate></Retention>")]
    [InlineData("<Retention><Mode>GOVERNANCE</Mode></Retention>")]
    [InlineData("<NotRetention><Mode>GOVERNANCE</Mode></NotRetention>")]
    public void Rejects_invalid_retention(string xml)
    {
        var (mode, until) = AzureBlobXmlReader.ParseRetention(xml);
        Assert.True(mode is null || until is null);
    }

    [Theory]
    [InlineData("<LegalHold><Status>ON</Status></LegalHold>", true)]
    [InlineData("<LegalHold><Status>OFF</Status></LegalHold>", false)]
    [InlineData("<LegalHold><Status>nope</Status></LegalHold>", null)]
    [InlineData("", null)]
    public void Parses_legal_hold(string xml, bool? expected)
    {
        Assert.Equal(expected, AzureBlobXmlReader.ParseLegalHold(xml));
    }
}
