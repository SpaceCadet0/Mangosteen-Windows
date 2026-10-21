using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace Mangosteen.Core.Documents;

/// <summary>Word documents (.docx) through the Open XML SDK.</summary>
public static class DocxFormat
{
    private const string CodeFont = "Consolas";

    // MARK: Reading

    public static RichDocument Read(string path)
    {
        Archives.CheckUnpackedSize(path, "word/media/", "docProps/thumbnail");
        using var word = WordprocessingDocument.Open(path, isEditable: false);
        var main = word.MainDocumentPart ?? throw new InvalidDataException("The document has no body.");
        var body = main.Document?.Body ?? throw new InvalidDataException("The document has no body.");
        var headingStyles = HeadingStyles(main);
        var orderedLists = OrderedLists(main);
        var links = main.HyperlinkRelationships.ToDictionary(r => r.Id, r => r.Uri.OriginalString);
        var document = new RichDocument();

        foreach (var paragraph in body.Descendants<W.Paragraph>())
        {
            // Text boxes repeat their content in a fallback for old Word versions; read it once.
            if (paragraph.Ancestors<DocumentFormat.OpenXml.AlternateContentFallback>().Any())
            {
                continue;
            }
            var properties = paragraph.ParagraphProperties;
            var styleId = properties?.ParagraphStyleId?.Val?.Value ?? "";
            var block = new Block(BlockType.Paragraph);
            if (headingStyles.TryGetValue(styleId, out var level))
            {
                block = new Block(BlockType.Heading, level);
            }
            else if (properties?.OutlineLevel?.Val?.Value is { } outline && outline < 6)
            {
                block = new Block(BlockType.Heading, outline + 1);
            }
            else if (properties?.NumberingProperties is { } numbering && numbering.NumberingId?.Val?.Value is { } numId and > 0)
            {
                var ilvl = numbering.NumberingLevelReference?.Val?.Value ?? 0;
                block = new Block(BlockType.ListItem, ilvl, orderedLists.Contains((numId, ilvl)));
            }
            else if (styleId.Contains("code", StringComparison.OrdinalIgnoreCase) || styleId.Contains("HTMLPreformatted", StringComparison.OrdinalIgnoreCase))
            {
                block = new Block(BlockType.Code);
            }

            foreach (var run in paragraph.Descendants<W.Run>())
            {
                var link = run.Ancestors<Hyperlink>().FirstOrDefault()?.Id?.Value is { } id && links.TryGetValue(id, out var uri) ? uri : null;
                var rp = run.RunProperties;
                var bold = IsOn(rp?.Bold);
                var italic = IsOn(rp?.Italic);
                var underline = rp?.Underline?.Val?.Value is { } u ? u != UnderlineValues.None : rp?.Underline is not null;
                var font = rp?.RunFonts?.Ascii?.Value ?? "";
                var code = block.Type == BlockType.Code || font.Contains("Consolas", StringComparison.OrdinalIgnoreCase)
                    || font.Contains("Courier", StringComparison.OrdinalIgnoreCase) || font.Contains("Mono", StringComparison.OrdinalIgnoreCase);
                foreach (var child in run.ChildElements)
                {
                    var text = child switch
                    {
                        W.Text t => t.Text,
                        TabChar => "\t",
                        Break or CarriageReturn => "\n",
                        NoBreakHyphen => "-",
                        _ => "",
                    };
                    block.Add(new Run(text, bold, italic, underline && link is null, link, code && block.Type != BlockType.Code));
                }
            }
            document.Blocks.Add(block);
        }
        return document;
    }

    private static bool IsOn(OnOffType? value) => value is not null && (value.Val is null || value.Val.Value);

    /// <summary>Style ids of headings: "Heading 1".."Heading 6" (any language, by name or outline level) and "Title".</summary>
    private static Dictionary<string, int> HeadingStyles(MainDocumentPart main)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var style in main.StyleDefinitionsPart?.Styles?.Elements<W.Style>() ?? [])
        {
            if (style.StyleId?.Value is not { } id)
            {
                continue;
            }
            var name = style.StyleName?.Val?.Value?.ToLowerInvariant() ?? "";
            if (style.StyleParagraphProperties?.OutlineLevel?.Val?.Value is { } outline && outline < 6)
            {
                result[id] = outline + 1;
            }
            else if (name.StartsWith("heading ", StringComparison.Ordinal) && int.TryParse(name[8..], out var level) && level is >= 1 and <= 6)
            {
                result[id] = level;
            }
            else if (name == "title")
            {
                result[id] = 1;
            }
        }
        for (var level = 1; level <= 6; level++)
        {
            result.TryAdd($"Heading{level}", level);
        }
        result.TryAdd("Title", 1);
        return result;
    }

    /// <summary>(numbering id, level) pairs that are numbered rather than bulleted.</summary>
    private static HashSet<(int, int)> OrderedLists(MainDocumentPart main)
    {
        var result = new HashSet<(int, int)>();
        var numbering = main.NumberingDefinitionsPart?.Numbering;
        if (numbering is null)
        {
            return result;
        }
        var abstracts = numbering.Elements<AbstractNum>()
            .Where(a => a.AbstractNumberId?.Value is not null)
            .ToDictionary(a => a.AbstractNumberId!.Value, a => a);
        foreach (var instance in numbering.Elements<NumberingInstance>())
        {
            if (instance.NumberID?.Value is not { } numId || instance.AbstractNumId?.Val?.Value is not { } abstractId
                || !abstracts.TryGetValue(abstractId, out var definition))
            {
                continue;
            }
            foreach (var level in definition.Elements<Level>())
            {
                var format = level.NumberingFormat?.Val?.Value;
                if (format is not null && format != NumberFormatValues.Bullet && format != NumberFormatValues.None
                    && level.LevelIndex?.Value is { } index)
                {
                    result.Add((numId, index));
                }
            }
        }
        return result;
    }

    // MARK: Writing

    private static readonly int[] HeadingSizes = [36, 30, 26, 24, 22, 22]; // half-points

    public static void Write(RichDocument document, string path)
    {
        using var word = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = word.AddMainDocumentPart();
        main.Document = new W.Document(new Body());
        AddStyles(main);
        var numbering = AddNumbering(main);
        var body = main.Document.Body!;
        var orderedInstance = 0;
        Block? previous = null;

        foreach (var block in document.Blocks)
        {
            var properties = new ParagraphProperties();
            switch (block.Type)
            {
                case BlockType.Heading:
                    properties.ParagraphStyleId = new ParagraphStyleId { Val = $"Heading{Math.Clamp(block.Level, 1, 6)}" };
                    break;
                case BlockType.Code:
                    properties.ParagraphStyleId = new ParagraphStyleId { Val = "Code" };
                    break;
                case BlockType.ListItem:
                    // Each ordered list starts at 1: it gets its own numbering instance.
                    if (block.Ordered && (previous?.Type != BlockType.ListItem || orderedInstance == 0))
                    {
                        orderedInstance = NewOrderedInstance(numbering);
                    }
                    properties.ParagraphStyleId = new ParagraphStyleId { Val = "ListParagraph" };
                    properties.NumberingProperties = new NumberingProperties(
                        new NumberingLevelReference { Val = Math.Min(block.Level, 8) },
                        new NumberingId { Val = block.Ordered ? orderedInstance : 1 });
                    break;
            }
            var paragraph = new W.Paragraph(properties);
            foreach (var run in block.Runs)
            {
                var element = BuildRun(run, block.Type == BlockType.Code);
                if (run.Link is { } link && TryUri(link) is { } uri)
                {
                    var relationship = main.AddHyperlinkRelationship(uri, isExternal: true);
                    paragraph.Append(new Hyperlink(element) { Id = relationship.Id });
                }
                else
                {
                    paragraph.Append(element);
                }
            }
            body.Append(paragraph);
            previous = block;
        }
        body.Append(new SectionProperties(
            new W.PageSize { Width = 11906, Height = 16838 },
            new PageMargin { Top = 1134, Bottom = 1134, Left = 1134, Right = 1134, Header = 708, Footer = 708, Gutter = 0 }));
        main.Document.Save();
    }

    private static Uri? TryUri(string link) =>
        Uri.TryCreate(link, UriKind.Absolute, out var absolute) ? absolute
        : Uri.TryCreate(link, UriKind.Relative, out var relative) ? relative : null;

    private static W.Run BuildRun(Run run, bool inCode)
    {
        var properties = new RunProperties();
        if (run.Link is not null)
        {
            properties.Append(new RunStyle { Val = "Hyperlink" });
        }
        if (run.Code && !inCode)
        {
            properties.Append(new RunFonts { Ascii = CodeFont, HighAnsi = CodeFont, ComplexScript = CodeFont });
        }
        if (run.Bold)
        {
            properties.Append(new Bold());
        }
        if (run.Italic)
        {
            properties.Append(new Italic());
        }
        if (run.Underline)
        {
            properties.Append(new Underline { Val = UnderlineValues.Single });
        }
        var element = new W.Run();
        if (properties.HasChildren)
        {
            element.Append(properties);
        }
        var parts = run.Text.Split('\n');
        for (var p = 0; p < parts.Length; p++)
        {
            if (p > 0)
            {
                element.Append(new Break());
            }
            var tabs = parts[p].Split('\t');
            for (var t = 0; t < tabs.Length; t++)
            {
                if (t > 0)
                {
                    element.Append(new TabChar());
                }
                if (tabs[t].Length > 0)
                {
                    element.Append(new W.Text(tabs[t]) { Space = SpaceProcessingModeValues.Preserve });
                }
            }
        }
        return element;
    }

    private static void AddStyles(MainDocumentPart main)
    {
        var styles = new Styles(
            new DocDefaults(
                new RunPropertiesDefault(new RunPropertiesBaseStyle(
                    new RunFonts { Ascii = "Calibri", HighAnsi = "Calibri", ComplexScript = "Calibri", EastAsia = "Calibri" },
                    new FontSize { Val = "22" })),
                new ParagraphPropertiesDefault(new ParagraphPropertiesBaseStyle(
                    new SpacingBetweenLines { After = "120", Line = "276", LineRule = LineSpacingRuleValues.Auto }))));
        styles.Append(new W.Style(new StyleName { Val = "Normal" }, new PrimaryStyle())
        {
            Type = StyleValues.Paragraph, StyleId = "Normal", Default = true,
        });
        for (var level = 1; level <= 6; level++)
        {
            styles.Append(new W.Style(
                new StyleName { Val = $"heading {level}" },
                new BasedOn { Val = "Normal" },
                new NextParagraphStyle { Val = "Normal" },
                new PrimaryStyle(),
                new StyleParagraphProperties(new KeepNext(), new SpacingBetweenLines { Before = "240", After = "120" },
                    new OutlineLevel { Val = level - 1 }),
                new StyleRunProperties(new Bold(), new FontSize { Val = HeadingSizes[level - 1].ToString() }))
            {
                Type = StyleValues.Paragraph, StyleId = $"Heading{level}",
            });
        }
        styles.Append(new W.Style(
            new StyleName { Val = "List Paragraph" }, new BasedOn { Val = "Normal" },
            new StyleParagraphProperties(new SpacingBetweenLines { After = "60" }))
        {
            Type = StyleValues.Paragraph, StyleId = "ListParagraph",
        });
        styles.Append(new W.Style(
            new StyleName { Val = "Code" }, new BasedOn { Val = "Normal" },
            new StyleParagraphProperties(new Shading { Val = ShadingPatternValues.Clear, Fill = "F4F4F4" },
                new SpacingBetweenLines { After = "0", Line = "240", LineRule = LineSpacingRuleValues.Auto }),
            new StyleRunProperties(new RunFonts { Ascii = CodeFont, HighAnsi = CodeFont, ComplexScript = CodeFont },
                new FontSize { Val = "20" }))
        {
            Type = StyleValues.Paragraph, StyleId = "Code",
        });
        styles.Append(new W.Style(
            new StyleName { Val = "Hyperlink" },
            new StyleRunProperties(new W.Color { Val = "0563C1" }, new Underline { Val = UnderlineValues.Single }))
        {
            Type = StyleValues.Character, StyleId = "Hyperlink",
        });
        main.AddNewPart<StyleDefinitionsPart>().Styles = styles;
    }

    /// <summary>Abstract 0: bullets, numbering instance 1. Abstract 1: decimal numbers, instances added per list.</summary>
    private static Numbering AddNumbering(MainDocumentPart main)
    {
        var bullets = new AbstractNum { AbstractNumberId = 0 };
        var numbers = new AbstractNum { AbstractNumberId = 1 };
        string[] bulletGlyphs = ["•", "◦", "▪"];
        for (var level = 0; level < 9; level++)
        {
            var indent = new Indentation { Left = (720 * (level + 1)).ToString(), Hanging = "360" };
            bullets.Append(new Level(
                new StartNumberingValue { Val = 1 }, new NumberingFormat { Val = NumberFormatValues.Bullet },
                new LevelText { Val = bulletGlyphs[level % 3] }, new LevelJustification { Val = LevelJustificationValues.Left },
                new PreviousParagraphProperties(indent))
            { LevelIndex = level });
            numbers.Append(new Level(
                new StartNumberingValue { Val = 1 }, new NumberingFormat { Val = NumberFormatValues.Decimal },
                new LevelText { Val = $"%{level + 1}." }, new LevelJustification { Val = LevelJustificationValues.Left },
                new PreviousParagraphProperties((Indentation)indent.CloneNode(true)))
            { LevelIndex = level });
        }
        var numbering = new Numbering(bullets, numbers,
            new NumberingInstance(new AbstractNumId { Val = 0 }) { NumberID = 1 });
        main.AddNewPart<NumberingDefinitionsPart>().Numbering = numbering;
        return numbering;
    }

    private static int NewOrderedInstance(Numbering numbering)
    {
        var id = numbering.Elements<NumberingInstance>().Max(n => n.NumberID!.Value) + 1;
        var instance = new NumberingInstance(new AbstractNumId { Val = 1 }) { NumberID = id };
        for (var level = 0; level < 9; level++)
        {
            instance.Append(new LevelOverride(new StartOverrideNumberingValue { Val = 1 }) { LevelIndex = level });
        }
        numbering.Append(instance);
        return id;
    }
}
