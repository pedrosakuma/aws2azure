using System.Text;
using System.Xml;

namespace Aws2Azure.Modules.S3.Internal;

/// <summary>
/// Parses Azure's <c>Get Block List</c> response:
/// <code>
/// &lt;BlockList&gt;
///   &lt;UncommittedBlocks&gt;
///     &lt;Block&gt;&lt;Name&gt;base64&lt;/Name&gt;&lt;Size&gt;1024&lt;/Size&gt;&lt;/Block&gt;
///     …
///   &lt;/UncommittedBlocks&gt;
///   &lt;CommittedBlocks&gt;…&lt;/CommittedBlocks&gt;
/// &lt;/BlockList&gt;
/// </code>
/// Each block also exposes the optional aws2azure-managed
/// "<c>b{nonce16hex}p{partNumber5d}</c>" id layout — see <see cref="TryParseBlockName"/>.
/// </summary>
internal static class BlockListParser
{
    public readonly record struct Block(string Name, long Size);

    public readonly record struct BlockList(
        IReadOnlyList<Block> Committed,
        IReadOnlyList<Block> Uncommitted);

    public static BlockList Parse(Stream xml)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            IgnoreWhitespace = true,
            IgnoreComments = true,
            CloseInput = false,
        };
        var committed = new List<Block>();
        var uncommitted = new List<Block>();
        List<Block>? sink = null;
        using var reader = XmlReader.Create(xml, settings);
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }
            switch (reader.LocalName)
            {
                case "CommittedBlocks": sink = committed; break;
                case "UncommittedBlocks": sink = uncommitted; break;
                case "Block" when sink is not null:
                    if (TryReadBlock(reader, out var b))
                    {
                        sink.Add(b);
                    }
                    break;
            }
        }
        return new BlockList(committed, uncommitted);
    }

    private static bool TryReadBlock(XmlReader reader, out Block block)
    {
        block = default;
        if (reader.IsEmptyElement)
        {
            return false;
        }
        string? name = null;
        long size = 0;
        // Step into <Block> body — ReadElementContentAsString below advances
        // past leaf elements, so we must not double-Read after consuming.
        if (!reader.Read()) return false;
        while (true)
        {
            if (reader.NodeType == XmlNodeType.EndElement &&
                string.Equals(reader.LocalName, "Block", StringComparison.Ordinal))
            {
                break;
            }
            if (reader.NodeType == XmlNodeType.Element)
            {
                var elem = reader.LocalName;
                var value = reader.ReadElementContentAsString();
                if (elem == "Name") { name = value; continue; }
                if (elem == "Size" && long.TryParse(value,
                        System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out var s))
                {
                    size = s; continue;
                }
                continue;
            }
            if (!reader.Read()) return false;
        }
        if (name is null) return false;
        block = new Block(name, size);
        return true;
    }

    /// <summary>
    /// Decodes a base64 Azure block name and parses the aws2azure layout
    /// <c>b{nonce16hex}p{partNumber5d}</c>. Returns <c>false</c> for any
    /// block that wasn't issued by this proxy (so we never report foreign
    /// uncommitted blocks as belonging to one of our uploads).
    /// </summary>
    public static bool TryParseBlockName(string base64Name, out string nonceHex, out int partNumber)
    {
        nonceHex = string.Empty;
        partNumber = 0;
        try
        {
            var raw = Convert.FromBase64String(base64Name);
            var ascii = Encoding.ASCII.GetString(raw);
            // Format: b{16}p{5} = 23 chars
            if (ascii.Length != 23 || ascii[0] != 'b' || ascii[17] != 'p')
            {
                return false;
            }
            nonceHex = ascii.Substring(1, 16);
            if (!int.TryParse(ascii.AsSpan(18, 5),
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out partNumber))
            {
                return false;
            }
            // Validate hex digits in nonce.
            foreach (var c in nonceHex)
            {
                if (!Uri.IsHexDigit(c))
                {
                    return false;
                }
            }
            return partNumber is >= 1 and <= 10000;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
