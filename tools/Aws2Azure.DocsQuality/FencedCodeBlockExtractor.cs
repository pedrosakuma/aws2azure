using System.Text.RegularExpressions;

namespace Aws2Azure.DocsQuality;

internal sealed record FencedCodeBlock(string Language, string Content, int StartLine);

/// <summary>
/// Extracts fenced code blocks from Markdown text. Handles blocks indented
/// under MkDocs tabbed-content markers (<c>=== "Bash"</c>) by matching the
/// fence markers after trimming leading whitespace, which is how MkDocs's own
/// Markdown extension recognizes indented fences too.
/// </summary>
internal static partial class FencedCodeBlockExtractor
{
    [GeneratedRegex(@"^(?<indent>\s*)```(?<lang>[\w-]*)\s*$")]
    private static partial Regex FenceLine();

    public static IReadOnlyList<FencedCodeBlock> Extract(string text)
    {
        var blocks = new List<FencedCodeBlock>();
        var lines = text.Replace("\r\n", "\n").Split('\n');

        string? currentLanguage = null;
        string? closingFence = null;
        var contentLines = new List<string>();
        var startLine = 0;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (closingFence is null)
            {
                var match = FenceLine().Match(line);
                if (match.Success)
                {
                    currentLanguage = match.Groups["lang"].Value;
                    closingFence = match.Groups["indent"].Value + "```";
                    contentLines.Clear();
                    startLine = i + 2;
                }
                continue;
            }

            if (line.TrimEnd() == closingFence.TrimEnd() || line.Trim() == "```")
            {
                blocks.Add(new FencedCodeBlock(currentLanguage ?? string.Empty, string.Join('\n', contentLines), startLine));
                closingFence = null;
                currentLanguage = null;
                continue;
            }

            contentLines.Add(line);
        }

        return blocks;
    }
}
