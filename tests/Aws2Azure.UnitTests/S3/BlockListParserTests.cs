using System.Text;
using Aws2Azure.Modules.S3.Internal;

namespace Aws2Azure.UnitTests.S3;

public class BlockListParserTests
{
    [Fact]
    public void Parses_uncommitted_blocks()
    {
        var xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <BlockList>
              <UncommittedBlocks>
                <Block><Name>YjAwMTEyMjMzNDQ1NTY2NzdwMDAwMDE=</Name><Size>1024</Size></Block>
                <Block><Name>YjAwMTEyMjMzNDQ1NTY2NzdwMDAwMDI=</Name><Size>2048</Size></Block>
              </UncommittedBlocks>
            </BlockList>
            """;
        var r = BlockListParser.Parse(new MemoryStream(Encoding.UTF8.GetBytes(xml)));
        Assert.Equal(2, r.Uncommitted.Count);
        Assert.Equal(1024, r.Uncommitted[0].Size);
        Assert.Equal(2048, r.Uncommitted[1].Size);
        Assert.Empty(r.Committed);
    }

    [Fact]
    public void Parses_committed_and_uncommitted_sections()
    {
        var xml = """
            <BlockList>
              <CommittedBlocks>
                <Block><Name>YQ==</Name><Size>10</Size></Block>
              </CommittedBlocks>
              <UncommittedBlocks>
                <Block><Name>Yg==</Name><Size>20</Size></Block>
              </UncommittedBlocks>
            </BlockList>
            """;
        var r = BlockListParser.Parse(new MemoryStream(Encoding.UTF8.GetBytes(xml)));
        Assert.Single(r.Committed);
        Assert.Single(r.Uncommitted);
    }

    [Fact]
    public void TryParseBlockName_round_trips_aws2azure_layout()
    {
        var nonce = "0123456789abcdef";
        var id = UploadIdCodec.BlockId(nonce, 42);
        Assert.True(BlockListParser.TryParseBlockName(id, out var parsedNonce, out var partNumber));
        Assert.Equal(nonce, parsedNonce);
        Assert.Equal(42, partNumber);
    }

    [Theory]
    [InlineData("YQ==")]                       // single-byte payload
    [InlineData("Zm9yZWlnbi1ibG9jaw==")]       // wrong length
    [InlineData("not-base64!")]                // invalid base64
    public void TryParseBlockName_rejects_foreign_block_ids(string base64)
    {
        Assert.False(BlockListParser.TryParseBlockName(base64, out _, out _));
    }
}
