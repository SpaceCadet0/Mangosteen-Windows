using System.Text;

namespace Mangosteen.Core.Documents;

public enum BlockType
{
    Paragraph,
    Heading,
    ListItem,
    Code,
}

/// <summary>A stretch of text with one style. Line breaks inside a paragraph are "\n", tabs "\t".</summary>
public sealed record Run(string Text, bool Bold = false, bool Italic = false, bool Underline = false, string? Link = null, bool Code = false)
{
    public bool SameStyle(Run other) =>
        Bold == other.Bold && Italic == other.Italic && Underline == other.Underline && Link == other.Link && Code == other.Code;
}

/// <summary>A paragraph, heading (Level 1-6), list item (Level 0 = top, Ordered) or code block.</summary>
public sealed class Block(BlockType type, int level = 0, bool ordered = false)
{
    public BlockType Type { get; set; } = type;
    public int Level { get; set; } = level;
    public bool Ordered { get; set; } = ordered;
    public List<Run> Runs { get; } = [];

    public string Text => string.Concat(Runs.Select(r => r.Text));

    /// <summary>Appends text, merging it with the last run when the style matches.</summary>
    public void Add(Run run)
    {
        if (run.Text.Length == 0)
        {
            return;
        }
        if (Runs.Count > 0 && Runs[^1].SameStyle(run))
        {
            Runs[^1] = Runs[^1] with { Text = Runs[^1].Text + run.Text };
        }
        else
        {
            Runs.Add(run);
        }
    }

    /// <summary>Drops leading and trailing whitespace of the paragraph (but not of code blocks).</summary>
    public void Trim()
    {
        if (Type == BlockType.Code || Runs.Count == 0)
        {
            return;
        }
        Runs[0] = Runs[0] with { Text = Runs[0].Text.TrimStart(' ', '\n') };
        Runs[^1] = Runs[^1] with { Text = Runs[^1].Text.TrimEnd(' ', '\n') };
        Runs.RemoveAll(r => r.Text.Length == 0);
    }
}

/// <summary>
/// The common model every document format is read into and written from: paragraphs, headings, lists, code
/// blocks, and bold, italic, underlined, linked or code text. Images, tables and page layout are not kept.
/// </summary>
public sealed class RichDocument
{
    public List<Block> Blocks { get; } = [];

    public static RichDocument FromPlainText(string text)
    {
        var document = new RichDocument();
        foreach (var line in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var block = new Block(BlockType.Paragraph);
            block.Add(new Run(line));
            document.Blocks.Add(block);
        }
        while (document.Blocks.Count > 0 && document.Blocks[^1].Runs.Count == 0)
        {
            document.Blocks.RemoveAt(document.Blocks.Count - 1);
        }
        return document;
    }

    /// <summary>Plain text: one line per paragraph; list items get a bullet or their number.</summary>
    public string ToPlainText()
    {
        var builder = new StringBuilder();
        var numbers = new ListNumbering();
        foreach (var block in Blocks)
        {
            if (block.Type == BlockType.ListItem)
            {
                builder.Append(new string(' ', block.Level * 4));
                builder.Append(block.Ordered ? $"{numbers.Next(block)}. " : "• ");
            }
            else
            {
                numbers.Reset();
            }
            builder.Append(block.Text);
            builder.Append('\n');
        }
        return builder.ToString();
    }
}

/// <summary>Counts ordered list items per nesting level; any non-list block restarts the count.</summary>
public sealed class ListNumbering
{
    private readonly List<int> _counters = [];

    public int Next(Block item)
    {
        while (_counters.Count <= item.Level)
        {
            _counters.Add(0);
        }
        _counters.RemoveRange(item.Level + 1, _counters.Count - item.Level - 1);
        return ++_counters[item.Level];
    }

    public void Reset() => _counters.Clear();
}
