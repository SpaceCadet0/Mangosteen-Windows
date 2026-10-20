using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace Mangosteen.Core.Documents;

/// <summary>OpenDocument text (.odt): a ZIP with content.xml and styles.xml.</summary>
public static class OdtFormat
{
    private static readonly XNamespace Office = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
    private static readonly XNamespace TextNs = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
    private static readonly XNamespace StyleNs = "urn:oasis:names:tc:opendocument:xmlns:style:1.0";
    private static readonly XNamespace Fo = "urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0";
    private static readonly XNamespace XLink = "http://www.w3.org/1999/xlink";
    private static readonly XNamespace Manifest = "urn:oasis:names:tc:opendocument:xmlns:manifest:1.0";
    private const string MimeType = "application/vnd.oasis.opendocument.text";

    private sealed record TextStyle(bool Bold, bool Italic, bool Underline, bool Code);

    // MARK: Reading

    public static RichDocument Read(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var content = Load(zip, "content.xml") ?? throw new InvalidDataException("The document has no content.");
        var styles = new Dictionary<string, TextStyle>();
        var orderedLists = new HashSet<string>();
        foreach (var source in new[] { Load(zip, "styles.xml"), content })
        {
            if (source is null)
            {
                continue;
            }
            foreach (var style in source.Descendants(StyleNs + "style"))
            {
                var name = (string?)style.Attribute(StyleNs + "name");
                var props = style.Element(StyleNs + "text-properties");
                if (name is null)
                {
                    continue;
                }
                var font = (string?)props?.Attribute(StyleNs + "font-name") ?? (string?)props?.Attribute(Fo + "font-family") ?? "";
                var weight = (string?)props?.Attribute(Fo + "font-weight") ?? "";
                styles[name] = new TextStyle(
                    weight is "bold" or "600" or "700" or "800" or "900" || name.Contains("Strong", StringComparison.Ordinal),
                    (string?)props?.Attribute(Fo + "font-style") == "italic" || name.Contains("Emphasis", StringComparison.Ordinal),
                    props?.Attribute(StyleNs + "text-underline-style") is { } u && (string)u != "none",
                    font.Contains("Mono", StringComparison.OrdinalIgnoreCase) || font.Contains("Courier", StringComparison.OrdinalIgnoreCase)
                        || font.Contains("Consolas", StringComparison.OrdinalIgnoreCase) || name.Contains("Source_20_Text", StringComparison.Ordinal));
            }
            foreach (var list in source.Descendants(TextNs + "list-style"))
            {
                if (list.Element(TextNs + "list-level-style-number") is not null && (string?)list.Attribute(StyleNs + "name") is { } name)
                {
                    orderedLists.Add(name);
                }
            }
        }

        var document = new RichDocument();
        var body = content.Descendants(Office + "text").FirstOrDefault() ?? throw new InvalidDataException("The document has no text.");
        ReadBlocks(body, document, styles, orderedLists, listDepth: 0, ordered: false);
        return document;
    }

    private static XDocument? Load(ZipArchive zip, string name)
    {
        var entry = zip.GetEntry(name);
        if (entry is null)
        {
            return null;
        }
        using var stream = entry.Open();
        return XDocument.Load(stream, LoadOptions.PreserveWhitespace);
    }

    private static void ReadBlocks(XElement parent, RichDocument document, Dictionary<string, TextStyle> styles,
        HashSet<string> orderedLists, int listDepth, bool ordered)
    {
        foreach (var element in parent.Elements())
        {
            if (element.Name == TextNs + "h")
            {
                var level = int.TryParse((string?)element.Attribute(TextNs + "outline-level"), out var l) ? Math.Clamp(l, 1, 6) : 1;
                var block = new Block(BlockType.Heading, level);
                ReadInline(element, block, styles, new TextStyle(false, false, false, false), null);
                document.Blocks.Add(block);
            }
            else if (element.Name == TextNs + "p")
            {
                var styleName = (string?)element.Attribute(TextNs + "style-name") ?? "";
                var paragraphStyle = styles.GetValueOrDefault(styleName) ?? new TextStyle(false, false, false, false);
                var block = listDepth > 0
                    ? new Block(BlockType.ListItem, listDepth - 1, ordered)
                    : new Block(paragraphStyle.Code ? BlockType.Code : BlockType.Paragraph);
                ReadInline(element, block, styles, paragraphStyle with { Code = false }, null);
                document.Blocks.Add(block);
            }
            else if (element.Name == TextNs + "list")
            {
                var styleName = (string?)element.Attribute(TextNs + "style-name");
                var isOrdered = styleName is null ? ordered : orderedLists.Contains(styleName);
                ReadBlocks(element, document, styles, orderedLists, listDepth + 1, isOrdered);
            }
            else if (element.Name == TextNs + "list-item" || element.Name == TextNs + "list-header" || element.Name == TextNs + "section"
                || element.Name.LocalName == "table" || element.Name.LocalName is "table-row" or "table-cell" or "table-header-rows")
            {
                ReadBlocks(element, document, styles, orderedLists, listDepth, ordered);
            }
        }
    }

    private static void ReadInline(XElement element, Block block, Dictionary<string, TextStyle> styles, TextStyle style, string? link)
    {
        foreach (var node in element.Nodes())
        {
            switch (node)
            {
                case XText text:
                    block.Add(new Run(text.Value.Replace('\n', ' '), style.Bold, style.Italic, style.Underline, link, style.Code));
                    break;
                case XElement child when child.Name == TextNs + "span":
                    var name = (string?)child.Attribute(TextNs + "style-name");
                    var inner = name is not null && styles.TryGetValue(name, out var s)
                        ? new TextStyle(style.Bold || s.Bold, style.Italic || s.Italic, style.Underline || s.Underline, style.Code || s.Code)
                        : style;
                    ReadInline(child, block, styles, inner, link);
                    break;
                case XElement child when child.Name == TextNs + "a":
                    ReadInline(child, block, styles, style, (string?)child.Attribute(XLink + "href") ?? link);
                    break;
                case XElement child when child.Name == TextNs + "s":
                    var count = int.TryParse((string?)child.Attribute(TextNs + "c"), out var c) ? c : 1;
                    block.Add(new Run(new string(' ', count), style.Bold, style.Italic, style.Underline, link, style.Code));
                    break;
                case XElement child when child.Name == TextNs + "tab":
                    block.Add(new Run("\t", style.Bold, style.Italic, style.Underline, link, style.Code));
                    break;
                case XElement child when child.Name == TextNs + "line-break":
                    block.Add(new Run("\n", style.Bold, style.Italic, style.Underline, link, style.Code));
                    break;
                case XElement child when child.Name.LocalName is "note" or "annotation" or "bookmark" or "bookmark-start" or "bookmark-end"
                    or "soft-page-break" or "frame":
                    break;
                case XElement child:
                    ReadInline(child, block, styles, style, link);
                    break;
            }
        }
    }

    // MARK: Writing

    private static readonly double[] HeadingSizes = [18, 15, 13, 12, 11, 11];

    public static void Write(RichDocument document, string path)
    {
        var automatic = new XElement(Office + "automatic-styles",
            ListStyle("L1", ordered: false), ListStyle("L2", ordered: true),
            new XElement(StyleNs + "style", new XAttribute(StyleNs + "name", "P_code"), new XAttribute(StyleNs + "family", "paragraph"),
                new XElement(StyleNs + "paragraph-properties", new XAttribute(Fo + "background-color", "#f4f4f4")),
                new XElement(StyleNs + "text-properties", new XAttribute(Fo + "font-family", "Consolas"), new XAttribute(Fo + "font-size", "10pt"))));
        var textStyles = new Dictionary<(bool, bool, bool, bool), string>();
        string? StyleFor(Run run)
        {
            var key = (run.Bold, run.Italic, run.Underline, run.Code);
            if (key == (false, false, false, false))
            {
                return null;
            }
            if (!textStyles.TryGetValue(key, out var name))
            {
                name = $"T{textStyles.Count + 1}";
                textStyles[key] = name;
                var props = new XElement(StyleNs + "text-properties");
                if (run.Bold)
                {
                    props.Add(new XAttribute(Fo + "font-weight", "bold"));
                }
                if (run.Italic)
                {
                    props.Add(new XAttribute(Fo + "font-style", "italic"));
                }
                if (run.Underline)
                {
                    props.Add(new XAttribute(StyleNs + "text-underline-style", "solid"), new XAttribute(StyleNs + "text-underline-width", "auto"),
                        new XAttribute(StyleNs + "text-underline-color", "font-color"));
                }
                if (run.Code)
                {
                    props.Add(new XAttribute(Fo + "font-family", "Consolas"));
                }
                automatic.Add(new XElement(StyleNs + "style", new XAttribute(StyleNs + "name", name), new XAttribute(StyleNs + "family", "text"), props));
            }
            return name;
        }

        var text = new XElement(Office + "text");
        var lists = new Stack<XElement>();
        foreach (var block in document.Blocks)
        {
            XElement paragraph;
            if (block.Type == BlockType.ListItem)
            {
                while (lists.Count > block.Level + 1)
                {
                    lists.Pop();
                }
                if (lists.Count == 0 || lists.Count == block.Level + 1 && (string?)lists.Peek().Attribute(TextNs + "style-name") != (block.Ordered ? "L2" : "L1"))
                {
                    if (lists.Count > 0)
                    {
                        lists.Pop();
                    }
                    var top = new XElement(TextNs + "list", new XAttribute(TextNs + "style-name", block.Ordered ? "L2" : "L1"));
                    if (lists.Count == 0)
                    {
                        text.Add(top);
                    }
                    else
                    {
                        LastItem(lists.Peek()).Add(top);
                    }
                    lists.Push(top);
                }
                while (lists.Count < block.Level + 1)
                {
                    var nested = new XElement(TextNs + "list");
                    LastItem(lists.Peek()).Add(nested);
                    lists.Push(nested);
                }
                paragraph = new XElement(TextNs + "p", new XAttribute(TextNs + "style-name", "Standard"));
                lists.Peek().Add(new XElement(TextNs + "list-item", paragraph));
            }
            else
            {
                lists.Clear();
                paragraph = block.Type switch
                {
                    BlockType.Heading => new XElement(TextNs + "h",
                        new XAttribute(TextNs + "style-name", $"Heading_20_{Math.Clamp(block.Level, 1, 6)}"),
                        new XAttribute(TextNs + "outline-level", Math.Clamp(block.Level, 1, 6))),
                    BlockType.Code => new XElement(TextNs + "p", new XAttribute(TextNs + "style-name", "P_code")),
                    _ => new XElement(TextNs + "p", new XAttribute(TextNs + "style-name", "Standard")),
                };
                text.Add(paragraph);
            }
            foreach (var run in block.Runs)
            {
                var target = paragraph;
                if (run.Link is { } link)
                {
                    var anchor = new XElement(TextNs + "a", new XAttribute(XLink + "type", "simple"), new XAttribute(XLink + "href", link));
                    paragraph.Add(anchor);
                    target = anchor;
                }
                var style = block.Type == BlockType.Code ? null : StyleFor(run);
                if (style is not null)
                {
                    var span = new XElement(TextNs + "span", new XAttribute(TextNs + "style-name", style));
                    target.Add(span);
                    target = span;
                }
                AddText(target, run.Text);
            }
        }

        var content = new XDocument(new XElement(Office + "document-content",
            new XAttribute(XNamespace.Xmlns + "office", Office), new XAttribute(XNamespace.Xmlns + "text", TextNs),
            new XAttribute(XNamespace.Xmlns + "style", StyleNs), new XAttribute(XNamespace.Xmlns + "fo", Fo),
            new XAttribute(XNamespace.Xmlns + "xlink", XLink), new XAttribute(Office + "version", "1.2"),
            automatic, new XElement(Office + "body", text)));

        var styles = new XDocument(new XElement(Office + "document-styles",
            new XAttribute(XNamespace.Xmlns + "office", Office), new XAttribute(XNamespace.Xmlns + "style", StyleNs),
            new XAttribute(XNamespace.Xmlns + "fo", Fo), new XAttribute(XNamespace.Xmlns + "text", TextNs), new XAttribute(Office + "version", "1.2"),
            new XElement(Office + "styles",
                new XElement(StyleNs + "default-style", new XAttribute(StyleNs + "family", "paragraph"),
                    new XElement(StyleNs + "text-properties", new XAttribute(Fo + "font-size", "11pt"), new XAttribute(StyleNs + "font-name", "Calibri"))),
                new XElement(StyleNs + "style", new XAttribute(StyleNs + "name", "Standard"), new XAttribute(StyleNs + "family", "paragraph"),
                    new XElement(StyleNs + "paragraph-properties", new XAttribute(Fo + "margin-bottom", "0.21cm"))),
                Enumerable.Range(1, 6).Select(level => new XElement(StyleNs + "style",
                    new XAttribute(StyleNs + "name", $"Heading_20_{level}"), new XAttribute(StyleNs + "display-name", $"Heading {level}"),
                    new XAttribute(StyleNs + "family", "paragraph"), new XAttribute(StyleNs + "parent-style-name", "Standard"),
                    new XAttribute(StyleNs + "default-outline-level", level),
                    new XElement(StyleNs + "paragraph-properties", new XAttribute(Fo + "margin-top", "0.42cm"), new XAttribute(Fo + "keep-with-next", "always")),
                    new XElement(StyleNs + "text-properties", new XAttribute(Fo + "font-size", $"{HeadingSizes[level - 1]}pt"),
                        new XAttribute(Fo + "font-weight", "bold")))))));

        var manifest = new XDocument(new XElement(Manifest + "manifest",
            new XAttribute(XNamespace.Xmlns + "manifest", Manifest), new XAttribute(Manifest + "version", "1.2"),
            FileEntry("/", MimeType), FileEntry("content.xml", "text/xml"), FileEntry("styles.xml", "text/xml")));

        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        // The mimetype must be the first entry and stored uncompressed.
        using (var writer = new StreamWriter(zip.CreateEntry("mimetype", CompressionLevel.NoCompression).Open(), new UTF8Encoding(false)))
        {
            writer.Write(MimeType);
        }
        Save(zip, "content.xml", content);
        Save(zip, "styles.xml", styles);
        Save(zip, "META-INF/manifest.xml", manifest);
    }

    private static XElement LastItem(XElement list)
    {
        if (list.Elements(TextNs + "list-item").LastOrDefault() is { } item)
        {
            return item;
        }
        var empty = new XElement(TextNs + "list-item");
        list.Add(empty);
        return empty;
    }

    private static XElement ListStyle(string name, bool ordered)
    {
        var style = new XElement(TextNs + "list-style", new XAttribute(StyleNs + "name", name));
        for (var level = 1; level <= 10; level++)
        {
            var levelProps = new XElement(StyleNs + "list-level-properties",
                new XAttribute(TextNs + "list-level-position-and-space-mode", "label-alignment"),
                new XElement(StyleNs + "list-level-label-alignment", new XAttribute(TextNs + "label-followed-by", "listtab"),
                    new XAttribute(Fo + "text-indent", "-0.635cm"), new XAttribute(Fo + "margin-left", $"{0.635 * (level + 1):0.###}cm".Replace(',', '.'))));
            style.Add(ordered
                ? new XElement(TextNs + "list-level-style-number", new XAttribute(TextNs + "level", level),
                    new XAttribute(StyleNs + "num-suffix", "."), new XAttribute(StyleNs + "num-format", "1"), levelProps)
                : new XElement(TextNs + "list-level-style-bullet", new XAttribute(TextNs + "level", level),
                    new XAttribute(TextNs + "bullet-char", "•"), levelProps));
        }
        return style;
    }

    /// <summary>ODF collapses whitespace, so runs of spaces, tabs and line breaks become elements.</summary>
    private static void AddText(XElement target, string text)
    {
        var plain = new StringBuilder();
        void Flush()
        {
            if (plain.Length > 0)
            {
                target.Add(new XText(plain.ToString()));
                plain.Clear();
            }
        }
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\n')
            {
                Flush();
                target.Add(new XElement(TextNs + "line-break"));
            }
            else if (c == '\t')
            {
                Flush();
                target.Add(new XElement(TextNs + "tab"));
            }
            else if (c == ' ' && (i == 0 || i == text.Length - 1 || text[i + 1] == ' '))
            {
                var count = 1;
                while (i + 1 < text.Length && text[i + 1] == ' ')
                {
                    count++;
                    i++;
                }
                Flush();
                target.Add(count == 1 ? new XElement(TextNs + "s") : new XElement(TextNs + "s", new XAttribute(TextNs + "c", count)));
            }
            else
            {
                plain.Append(c);
            }
        }
        Flush();
    }

    private static XElement FileEntry(string path, string type) => new(Manifest + "file-entry",
        new XAttribute(Manifest + "full-path", path), new XAttribute(Manifest + "media-type", type));

    private static void Save(ZipArchive zip, string name, XDocument document)
    {
        using var stream = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
        document.Save(stream, SaveOptions.DisableFormatting);
    }
}
