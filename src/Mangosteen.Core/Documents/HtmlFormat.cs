using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace Mangosteen.Core.Documents;

/// <summary>HTML into the document model (structure and inline styles), and a clean UTF-8 page out of it.</summary>
public static partial class HtmlFormat
{
    public static RichDocument Parse(string html)
    {
        var page = new HtmlParser().ParseDocument(html);
        var reader = new Reader();
        if (page.Body is { } body)
        {
            reader.Walk(body, new Style());
        }
        reader.Finish();
        return reader.Document;
    }

    private readonly record struct Style(bool Bold = false, bool Italic = false, bool Underline = false, string? Link = null,
        bool Code = false, int ListDepth = 0, bool Ordered = false);

    private sealed class Reader
    {
        private Block? _current;
        public RichDocument Document { get; } = new();

        public void Finish() => Close();

        private void Close()
        {
            if (_current is null)
            {
                return;
            }
            _current.Trim();
            if (_current.Runs.Count > 0 || _current.Type == BlockType.Code)
            {
                Document.Blocks.Add(_current);
            }
            _current = null;
        }

        private Block Current => _current ??= new Block(BlockType.Paragraph);

        public void Walk(INode node, Style style)
        {
            foreach (var child in node.ChildNodes)
            {
                if (child is IText text)
                {
                    var value = WhitespacePattern().Replace(text.Data, " ");
                    if (_current is null && value.Trim().Length == 0)
                    {
                        continue;
                    }
                    Current.Add(new Run(value, style.Bold, style.Italic, style.Underline, style.Link, style.Code));
                }
                else if (child is IElement element)
                {
                    Element(element, style);
                }
            }
        }

        private void Element(IElement element, Style style)
        {
            var tag = element.LocalName;
            switch (tag)
            {
                case "script" or "style" or "head" or "title" or "noscript" or "template" or "svg" or "iframe":
                    return;
                case "br":
                    Current.Add(new Run("\n", style.Bold, style.Italic, style.Underline, style.Link, style.Code));
                    return;
                case "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
                    Close();
                    _current = new Block(BlockType.Heading, tag[1] - '0');
                    Walk(element, style);
                    Close();
                    return;
                case "ul" or "ol" or "menu":
                    Close();
                    Walk(element, style with { ListDepth = style.ListDepth + 1, Ordered = tag == "ol" });
                    Close();
                    return;
                case "li":
                    Close();
                    _current = new Block(BlockType.ListItem, Math.Max(0, style.ListDepth - 1), style.Ordered);
                    Walk(element, style);
                    Close();
                    return;
                case "pre":
                    Close();
                    var code = new Block(BlockType.Code);
                    code.Add(new Run(element.TextContent.TrimEnd('\n'), Code: true));
                    Document.Blocks.Add(code);
                    return;
                case "td" or "th":
                    Walk(element, style);
                    Current.Add(new Run("\t"));
                    return;
                case "b" or "strong":
                    Walk(element, style with { Bold = true });
                    return;
                case "i" or "em" or "cite" or "var" or "dfn":
                    Walk(element, style with { Italic = true });
                    return;
                case "u" or "ins":
                    Walk(element, style with { Underline = true });
                    return;
                case "code" or "kbd" or "samp" or "tt":
                    Walk(element, style with { Code = true });
                    return;
                case "a":
                    var href = element.GetAttribute("href");
                    Walk(element, string.IsNullOrEmpty(href) || href.StartsWith('#') ? style : style with { Link = href });
                    return;
                case "img":
                    return;
            }
            if (IsBlock(tag))
            {
                Close();
                Walk(element, style);
                Close();
            }
            else
            {
                Walk(element, ApplyInlineCss(element, style));
            }
        }

        private static Style ApplyInlineCss(IElement element, Style style)
        {
            var css = element.GetAttribute("style") ?? "";
            if (css.Length == 0)
            {
                return style;
            }
            return style with
            {
                Bold = style.Bold || BoldPattern().IsMatch(css),
                Italic = style.Italic || css.Contains("italic", StringComparison.OrdinalIgnoreCase),
                Underline = style.Underline || css.Contains("underline", StringComparison.OrdinalIgnoreCase),
            };
        }

        private static bool IsBlock(string tag) => tag is "p" or "div" or "section" or "article" or "blockquote" or "header"
            or "footer" or "main" or "nav" or "aside" or "address" or "dl" or "dt" or "dd" or "figure" or "figcaption"
            or "table" or "thead" or "tbody" or "tfoot" or "tr" or "caption" or "form" or "fieldset" or "hr" or "body"
            or "center" or "details" or "summary";
    }

    public static string Write(RichDocument document, string title)
    {
        var builder = new StringBuilder();
        builder.Append("<!DOCTYPE html>\n<html>\n<head>\n<meta charset=\"utf-8\">\n<title>")
            .Append(WebUtility.HtmlEncode(title))
            .Append("</title>\n<style>body { font-family: -apple-system, \"Segoe UI\", Arial, sans-serif; line-height: 1.5; max-width: 46em; margin: 2em auto; padding: 0 1em; } pre { background: #f4f4f4; padding: 0.8em; overflow-x: auto; }</style>\n</head>\n<body>\n");
        var openLists = new Stack<bool>();
        void CloseListsTo(int depth)
        {
            while (openLists.Count > depth)
            {
                builder.Append(openLists.Pop() ? "</ol>\n" : "</ul>\n");
            }
        }
        foreach (var block in document.Blocks)
        {
            if (block.Type != BlockType.ListItem)
            {
                CloseListsTo(0);
            }
            switch (block.Type)
            {
                case BlockType.Heading:
                    var level = Math.Clamp(block.Level, 1, 6);
                    builder.Append($"<h{level}>").Append(Inline(block.Runs)).Append($"</h{level}>\n");
                    break;
                case BlockType.ListItem:
                    CloseListsTo(block.Level + 1);
                    if (openLists.Count == block.Level + 1 && openLists.Peek() != block.Ordered)
                    {
                        CloseListsTo(block.Level);
                    }
                    while (openLists.Count < block.Level + 1)
                    {
                        builder.Append(block.Ordered ? "<ol>\n" : "<ul>\n");
                        openLists.Push(block.Ordered);
                    }
                    builder.Append("<li>").Append(Inline(block.Runs)).Append("</li>\n");
                    break;
                case BlockType.Code:
                    builder.Append("<pre><code>").Append(WebUtility.HtmlEncode(block.Text)).Append("</code></pre>\n");
                    break;
                default:
                    builder.Append("<p>").Append(Inline(block.Runs)).Append("</p>\n");
                    break;
            }
        }
        CloseListsTo(0);
        return builder.Append("</body>\n</html>\n").ToString();
    }

    private static string Inline(IEnumerable<Run> runs)
    {
        var builder = new StringBuilder();
        foreach (var run in runs)
        {
            var text = WebUtility.HtmlEncode(run.Text).Replace("\n", "<br>\n").Replace("\t", "&emsp;");
            if (run.Code)
            {
                text = "<code>" + text + "</code>";
            }
            if (run.Underline)
            {
                text = "<u>" + text + "</u>";
            }
            if (run.Italic)
            {
                text = "<em>" + text + "</em>";
            }
            if (run.Bold)
            {
                text = "<strong>" + text + "</strong>";
            }
            if (run.Link is { } link)
            {
                text = "<a href=\"" + WebUtility.HtmlEncode(link) + "\">" + text + "</a>";
            }
            builder.Append(text);
        }
        return builder.ToString();
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespacePattern();

    [GeneratedRegex(@"font-weight\s*:\s*(bold|[6-9]00)", RegexOptions.IgnoreCase)]
    private static partial Regex BoldPattern();
}
