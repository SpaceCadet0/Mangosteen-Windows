using System.Text;
using System.Text.RegularExpressions;

namespace Mangosteen.Core.Documents;

/// <summary>A practical subset of Markdown: headings, paragraphs, lists, code, bold, italic, code spans and links.</summary>
public static partial class MarkdownFormat
{
    public static RichDocument Parse(string markdown)
    {
        var document = new RichDocument();
        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var paragraph = new List<string>();

        void Flush()
        {
            if (paragraph.Count == 0)
            {
                return;
            }
            var block = new Block(BlockType.Paragraph);
            var text = new StringBuilder();
            for (var i = 0; i < paragraph.Count; i++)
            {
                var line = paragraph[i];
                var hardBreak = line.EndsWith("  ", StringComparison.Ordinal) || line.EndsWith('\\');
                text.Append(line.TrimEnd(' ', '\\'));
                if (i < paragraph.Count - 1)
                {
                    text.Append(hardBreak ? '\n' : ' ');
                }
            }
            AddInline(block, text.ToString().Trim());
            document.Blocks.Add(block);
            paragraph.Clear();
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();
            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                Flush();
                var fence = trimmed[..3];
                var code = new List<string>();
                for (i++; i < lines.Length && !lines[i].Trim().StartsWith(fence, StringComparison.Ordinal); i++)
                {
                    code.Add(lines[i]);
                }
                var block = new Block(BlockType.Code);
                block.Add(new Run(string.Join("\n", code), Code: true));
                document.Blocks.Add(block);
                continue;
            }
            if (trimmed.Length == 0)
            {
                Flush();
                continue;
            }
            if (HeadingPattern().Match(line) is { Success: true } heading)
            {
                Flush();
                var block = new Block(BlockType.Heading, heading.Groups[1].Length);
                AddInline(block, heading.Groups[2].Value.TrimEnd('#', ' '));
                document.Blocks.Add(block);
                continue;
            }
            if (RulePattern().IsMatch(trimmed))
            {
                Flush();
                continue;
            }
            if (ListPattern().Match(line) is { Success: true } item)
            {
                Flush();
                var indent = item.Groups[1].Value.Replace("\t", "    ").Length;
                var block = new Block(BlockType.ListItem, Math.Min(indent / 2, 8), ordered: char.IsDigit(item.Groups[2].Value[0]));
                AddInline(block, item.Groups[3].Value.Trim());
                document.Blocks.Add(block);
                continue;
            }
            paragraph.Add(trimmed.StartsWith('>') ? trimmed.TrimStart('>', ' ') : line.TrimStart());
        }
        Flush();
        return document;
    }

    /// <summary>Parses inline markup into runs and appends them to <paramref name="block"/>.</summary>
    public static void AddInline(Block block, string text, bool bold = false, bool italic = false, string? link = null)
    {
        var plain = new StringBuilder();
        void Emit()
        {
            block.Add(new Run(plain.ToString(), bold, italic, Link: link));
            plain.Clear();
        }
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\\' && i + 1 < text.Length && "\\`*_[]()#+-.!<>".Contains(text[i + 1]))
            {
                plain.Append(text[++i]);
                continue;
            }
            if (c == '`' && text.IndexOf('`', i + 1) is var end and > 0)
            {
                Emit();
                block.Add(new Run(text[(i + 1)..end], bold, italic, Link: link, Code: true));
                i = end;
                continue;
            }
            if ((c is '*' or '_') && i + 1 < text.Length && text[i + 1] == c
                && text.IndexOf(new string(c, 2), i + 2, StringComparison.Ordinal) is var strongEnd and > 0)
            {
                Emit();
                AddInline(block, text[(i + 2)..strongEnd], true, italic, link);
                i = strongEnd + 1;
                continue;
            }
            if ((c is '*' or '_') && i + 1 < text.Length && !char.IsWhiteSpace(text[i + 1])
                && (c == '*' || i == 0 || !char.IsLetterOrDigit(text[i - 1]))
                && ClosingEmphasis(text, i + 1, c) is var emEnd and > 0)
            {
                Emit();
                AddInline(block, text[(i + 1)..emEnd], bold, true, link);
                i = emEnd;
                continue;
            }
            if (c == '[' && link is null && LinkPattern().Match(text, i) is { Success: true } match && match.Index == i)
            {
                Emit();
                AddInline(block, match.Groups[1].Value, bold, italic, match.Groups[2].Value);
                i += match.Length - 1;
                continue;
            }
            plain.Append(c);
        }
        Emit();
    }

    private static int ClosingEmphasis(string text, int from, char marker)
    {
        for (var i = from; i < text.Length; i++)
        {
            if (text[i] == marker && !char.IsWhiteSpace(text[i - 1]) && (i + 1 >= text.Length || text[i + 1] != marker)
                && (marker == '*' || i + 1 >= text.Length || !char.IsLetterOrDigit(text[i + 1])))
            {
                return i;
            }
        }
        return -1;
    }

    public static string Write(RichDocument document)
    {
        var builder = new StringBuilder();
        var numbers = new ListNumbering();
        Block? previous = null;
        foreach (var block in document.Blocks)
        {
            if (previous is not null)
            {
                builder.Append(previous.Type == BlockType.ListItem && block.Type == BlockType.ListItem ? "\n" : "\n\n");
            }
            switch (block.Type)
            {
                case BlockType.Heading:
                    numbers.Reset();
                    builder.Append(new string('#', Math.Clamp(block.Level, 1, 6))).Append(' ').Append(Inline(block.Runs).Replace("\n", " "));
                    break;
                case BlockType.ListItem:
                    builder.Append(new string(' ', block.Level * 2));
                    builder.Append(block.Ordered ? $"{numbers.Next(block)}. " : "- ");
                    builder.Append(Inline(block.Runs).Replace("\n", "  \n" + new string(' ', block.Level * 2 + 2)));
                    break;
                case BlockType.Code:
                    numbers.Reset();
                    builder.Append("```\n").Append(block.Text).Append("\n```");
                    break;
                default:
                    numbers.Reset();
                    builder.Append(Inline(block.Runs).Replace("\n", "  \n"));
                    break;
            }
            previous = block;
        }
        return builder.Append('\n').ToString();
    }

    private static string Inline(IEnumerable<Run> runs)
    {
        var builder = new StringBuilder();
        foreach (var run in runs)
        {
            var text = run.Code ? "`" + run.Text + "`" : Escape(run.Text);
            if (run.Text.Trim().Length == 0)
            {
                builder.Append(run.Text);
                continue;
            }
            // Markers must hug the text: "** bold**" is not bold.
            var lead = text[..(text.Length - text.TrimStart().Length)];
            var trail = text[text.TrimEnd().Length..];
            text = text.Trim();
            if (run.Italic)
            {
                text = "*" + text + "*";
            }
            if (run.Bold)
            {
                text = "**" + text + "**";
            }
            if (run.Link is { } link)
            {
                text = "[" + text + "](" + link.Replace(" ", "%20").Replace(")", "%29") + ")";
            }
            builder.Append(lead).Append(text).Append(trail);
        }
        return builder.ToString();
    }

    private static string Escape(string text) => EscapePattern().Replace(text, @"\$1");

    [GeneratedRegex(@"^(#{1,6})\s+(.*)$")]
    private static partial Regex HeadingPattern();

    [GeneratedRegex(@"^(-{3,}|\*{3,}|_{3,})$")]
    private static partial Regex RulePattern();

    [GeneratedRegex(@"^(\s*)([-*+]|\d{1,9}[.)])\s+(.*)$")]
    private static partial Regex ListPattern();

    [GeneratedRegex(@"\[([^\]]*)\]\(([^)\s]*)(?:\s+""[^""]*"")?\)")]
    private static partial Regex LinkPattern();

    [GeneratedRegex(@"([\\`*_\[\]])")]
    private static partial Regex EscapePattern();
}
