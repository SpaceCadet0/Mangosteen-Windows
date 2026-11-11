using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using AngleSharp.Xhtml;

namespace Mangosteen.Core.Documents;

/// <summary>
/// Electronic books: EPUB (a ZIP of XHTML chapters) and FictionBook 2 (one XML file, often zipped). A book is read into
/// chapters of XHTML with its pictures, then written as EPUB or FB2, or as one HTML page that the document converter
/// turns into PDF, Word, RTF, text or Markdown. Documents become one-chapter books the same way.
/// </summary>
public static partial class BookConverter
{
    static BookConverter() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>A chapter: the inside of an XHTML body; pictures are referred to by their name in <see cref="Book.Images"/>.</summary>
    public sealed record Chapter(string Title, string Body);

    public sealed record Picture(byte[] Data, string MediaType);

    public sealed class Book
    {
        public string Title { get; set; } = "";
        public string Author { get; set; } = "";
        public string Language { get; set; } = "ru";
        public List<Chapter> Chapters { get; } = [];
        public SortedDictionary<string, Picture> Images { get; } = new(StringComparer.Ordinal);
        public string? Cover { get; set; }
    }

    public static bool IsBook(FileFormat format) => format is FileFormat.Epub or FileFormat.Fb2;

    private static InvalidDataException Unreadable(string path) =>
        new(string.Format(Loc.L("“{0}” cannot be read as an e-book."), Path.GetFileName(path)));

    // ---------------------------------------------------------------- Reading

    public static Book Read(string path, FileFormat format)
    {
        try
        {
            return format switch
            {
                FileFormat.Epub => ReadEpub(File.ReadAllBytes(path), OutputNaming.BaseName(path)),
                FileFormat.Fb2 => ReadFb2(Fb2Bytes(File.ReadAllBytes(path)), OutputNaming.BaseName(path)),
                _ => throw new NotSupportedException($"{format.DisplayName()} is not an e-book."),
            };
        }
        catch (Exception error) when (error is XmlException or InvalidDataException or KeyNotFoundException or FormatException)
        {
            throw Unreadable(path);
        }
    }

    /// <summary>The FictionBook XML of a .fb2 file, or of the .fb2 inside a .fb2.zip.</summary>
    private static byte[] Fb2Bytes(byte[] data)
    {
        if (data.Length < 2 || data[0] != 0x50 || data[1] != 0x4B)
        {
            return data;
        }
        using var zip = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read);
        var entry = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith(".fb2", StringComparison.OrdinalIgnoreCase))
            ?? zip.Entries.FirstOrDefault(e => e.Length > 0) ?? throw new InvalidDataException("The archive is empty.");
        using var stream = entry.Open();
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    public static Book ReadEpub(byte[] data, string fallbackTitle)
    {
        using var zip = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read);
        var entries = zip.Entries.Where(e => e.Length > 0 || e.FullName.Length > 0)
            .GroupBy(e => e.FullName, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        byte[]? Entry(string name)
        {
            if (!entries.TryGetValue(name, out var entry))
            {
                return null;
            }
            using var stream = entry.Open();
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            return copy.ToArray();
        }
        var container = Entry("META-INF/container.xml") ?? throw new InvalidDataException("No container.xml.");
        var rootPath = Xml(container).Descendants().FirstOrDefault(e => e.Name.LocalName == "rootfile")?.Attribute("full-path")?.Value
            ?? throw new InvalidDataException("No package file.");
        var opf = Xml(Entry(rootPath) ?? throw new InvalidDataException("No package file."));
        var basePath = rootPath.Contains('/') ? rootPath[..rootPath.LastIndexOf('/')] : "";
        var metadata = opf.Descendants().FirstOrDefault(e => e.Name.LocalName == "metadata");
        string? Meta(string name) => metadata?.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value is { } v
            && Clean(v) is { Length: > 0 } c ? c : null;
        var book = new Book { Title = Meta("title") ?? fallbackTitle, Author = Meta("creator") ?? "" };
        book.Language = Meta("language") ?? book.Language;

        // Manifest: id → path inside the archive and type.
        var manifest = new Dictionary<string, (string Path, string Type)>();
        foreach (var item in opf.Descendants().Where(e => e.Name.LocalName == "item"))
        {
            if (item.Attribute("id")?.Value is { } id && item.Attribute("href")?.Value is { } href)
            {
                manifest[id] = (Resolve(href, basePath), item.Attribute("media-type")?.Value ?? "");
            }
        }
        // Pictures get short unique names.
        var imageNames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (_, (itemPath, type)) in manifest.OrderBy(m => m.Value.Path, StringComparer.Ordinal))
        {
            if (!type.StartsWith("image/", StringComparison.Ordinal) || Entry(itemPath) is not { } image || imageNames.ContainsKey(itemPath))
            {
                continue;
            }
            var name = UniqueName(itemPath[(itemPath.LastIndexOf('/') + 1)..], book.Images.Keys.ToHashSet());
            book.Images[name] = new Picture(image, type);
            imageNames[itemPath] = name;
        }
        var coverId = opf.Descendants().FirstOrDefault(e => e.Name.LocalName == "meta" && e.Attribute("name")?.Value == "cover")?.Attribute("content")?.Value;
        if (coverId is not null && manifest.TryGetValue(coverId, out var coverItem) && imageNames.TryGetValue(coverItem.Path, out var coverName))
        {
            book.Cover = coverName;
        }
        else if (opf.Descendants().FirstOrDefault(e => e.Name.LocalName == "item"
                     && (e.Attribute("properties")?.Value ?? "").Contains("cover-image", StringComparison.Ordinal))?.Attribute("href")?.Value is { } coverHref)
        {
            book.Cover = imageNames.GetValueOrDefault(Resolve(coverHref, basePath));
        }

        // Chapters in reading order.
        var parser = new HtmlParser();
        foreach (var itemref in opf.Descendants().Where(e => e.Name.LocalName == "itemref"))
        {
            if (itemref.Attribute("idref")?.Value is not { } idref || !manifest.TryGetValue(idref, out var item) || Entry(item.Path) is not { } pageData)
            {
                continue;
            }
            var folder = item.Path.Contains('/') ? item.Path[..item.Path.LastIndexOf('/')] : "";
            var document = parser.ParseDocument(DecodeXmlText(pageData));
            foreach (var picture in document.QuerySelectorAll("img, image"))
            {
                foreach (var attribute in picture.Attributes.ToList())
                {
                    if (attribute.LocalName is "src" or "href" && imageNames.TryGetValue(Resolve(attribute.Value, folder), out var newName))
                    {
                        picture.SetAttribute(attribute.NamespaceUri, attribute.Name, newName);
                    }
                }
            }
            foreach (var script in document.QuerySelectorAll("script").ToList())
            {
                script.Remove();
            }
            if (document.Body is not { } bodyElement || bodyElement.TextContent.Trim().Length == 0 && bodyElement.QuerySelector("img, image") is null)
            {
                continue;
            }
            var heading = document.QuerySelector("h1, h2, h3")?.TextContent ?? document.Title ?? "";
            book.Chapters.Add(new Chapter(Clean(heading), Xhtml(bodyElement)));
        }
        if (book.Chapters.Count == 0)
        {
            throw new InvalidDataException("The book has no chapters.");
        }
        return book;
    }

    public static Book ReadFb2(byte[] data, string fallbackTitle)
    {
        var document = Xml(data);
        var root = document.Root ?? throw new InvalidDataException("Empty FictionBook.");
        var titleInfo = root.Descendants().FirstOrDefault(e => e.Name.LocalName == "title-info");
        XElement? Child(XElement? element, string name) => element?.Elements().FirstOrDefault(e => e.Name.LocalName == name);
        var book = new Book { Title = Child(titleInfo, "book-title")?.Value is { } t && Clean(t).Length > 0 ? Clean(t) : fallbackTitle };
        if (Child(titleInfo, "author") is { } author)
        {
            book.Author = string.Join(" ", new[] { "first-name", "middle-name", "last-name" }
                .Select(n => Child(author, n)?.Value.Trim() ?? "").Where(s => s.Length > 0));
            if (book.Author.Length == 0 && Child(author, "nickname")?.Value.Trim() is { Length: > 0 } nickname)
            {
                book.Author = nickname;
            }
        }
        if (Child(titleInfo, "lang")?.Value.Trim() is { Length: > 0 } lang)
        {
            book.Language = lang;
        }
        foreach (var binary in root.Elements().Where(e => e.Name.LocalName == "binary"))
        {
            if (binary.Attribute("id")?.Value is { } id)
            {
                try
                {
                    book.Images[SafeName(id)] = new Picture(Convert.FromBase64String(Regex.Replace(binary.Value, @"\s+", "")),
                        binary.Attribute("content-type")?.Value ?? "image/jpeg");
                }
                catch (FormatException)
                {
                }
            }
        }
        if (Child(titleInfo, "coverpage")?.Elements().FirstOrDefault(e => e.Name.LocalName == "image") is { } cover && ImageReference(cover) is { } coverRef)
        {
            book.Cover = SafeName(coverRef);
        }
        foreach (var body in root.Elements().Where(e => e.Name.LocalName == "body"))
        {
            var isNotes = body.Attribute("name")?.Value == "notes";
            var sections = body.Elements().Where(e => e.Name.LocalName == "section").ToList();
            if (sections.Count == 0)
            {
                book.Chapters.Add(new Chapter(isNotes ? Loc.L("Notes") : book.Title, Fb2Blocks(body, 1)));
                continue;
            }
            // The body's own title and epigraph before the first section open the book.
            var lead = body.Elements().Where(e => e.Name.LocalName != "section").ToList();
            if (lead.Count > 0 && !isNotes)
            {
                book.Chapters.Add(new Chapter(book.Title, string.Concat(lead.Select(e => Fb2Block(e, 1)))));
            }
            foreach (var section in sections)
            {
                var title = Clean(Child(section, "title")?.Value ?? "");
                book.Chapters.Add(new Chapter(title.Length == 0 && isNotes ? Loc.L("Notes") : title, Fb2Blocks(section, 2)));
            }
        }
        if (book.Chapters.Count == 0)
        {
            throw new InvalidDataException("The book has no chapters.");
        }
        return book;
    }

    private static string Fb2Blocks(XElement element, int depth) => string.Join("\n", element.Nodes().Select(node => node switch
    {
        XElement child => Fb2Block(child, depth),
        XText text when text.Value.Trim().Length > 0 => $"<p>{Escape(text.Value.Trim())}</p>",
        _ => "",
    }).Where(s => s.Length > 0));

    /// <summary>One FictionBook block as XHTML.</summary>
    private static string Fb2Block(XElement element, int depth)
    {
        var heading = Math.Min(depth, 6);
        switch (element.Name.LocalName)
        {
            case "title":
                return $"<h{heading}>{string.Join("<br/>", element.Elements().Select(Fb2Inline))}</h{heading}>";
            case "subtitle":
                var sub = Math.Min(heading + 1, 6);
                return $"<h{sub}>{Fb2Inline(element)}</h{sub}>";
            case "p":
                return $"<p>{Fb2Inline(element)}</p>";
            case "empty-line":
                return "<br/>";
            case "image":
                return ImageReference(element) is { } name ? $"<p><img src=\"{Escape(SafeName(name))}\" alt=\"\"/></p>" : "";
            case "section":
                return Fb2Blocks(element, depth + 1);
            case "epigraph" or "cite":
                return $"<blockquote>{Fb2Blocks(element, depth)}</blockquote>";
            case "poem" or "stanza":
                return $"<div class=\"poem\">{Fb2Blocks(element, depth)}</div>";
            case "v":
                return $"<p class=\"verse\">{Fb2Inline(element)}</p>";
            case "text-author":
                return $"<p class=\"author\"><em>{Fb2Inline(element)}</em></p>";
            case "table":
                return "<table>" + string.Concat(element.Elements().Select(row => "<tr>" + string.Concat(row.Elements().Select(cell =>
                {
                    var tag = cell.Name.LocalName == "th" ? "th" : "td";
                    return $"<{tag}>{Fb2Inline(cell)}</{tag}>";
                })) + "</tr>")) + "</table>";
            default:
                return Fb2Blocks(element, depth);
        }
    }

    /// <summary>Text with emphasis, strong, links and the like, as XHTML.</summary>
    private static string Fb2Inline(XElement element) => string.Concat(element.Nodes().Select(node =>
    {
        if (node is not XElement child)
        {
            return node is XText text ? Escape(text.Value) : "";
        }
        var inner = Fb2Inline(child);
        return child.Name.LocalName switch
        {
            "emphasis" => $"<em>{inner}</em>",
            "strong" => $"<strong>{inner}</strong>",
            "strikethrough" => $"<s>{inner}</s>",
            "sup" => $"<sup>{inner}</sup>",
            "sub" => $"<sub>{inner}</sub>",
            "code" => $"<code>{inner}</code>",
            "image" => ImageReference(child) is { } name ? $"<img src=\"{Escape(SafeName(name))}\" alt=\"\"/>" : "",
            _ => inner,
        };
    }));

    private static string? ImageReference(XElement element) =>
        element.Attributes().FirstOrDefault(a => a.Name.LocalName == "href")?.Value is { } link ? link.TrimStart('#') : null;

    // ---------------------------------------------------------------- Documents

    /// <summary>A document's HTML as a book of one chapter titled after the file.</summary>
    public static Book FromHtml(string html, string title)
    {
        var document = new HtmlParser().ParseDocument(html);
        var body = document.Body is { } b ? Xhtml(b) : $"<p>{Escape(document.DocumentElement.TextContent)}</p>";
        var book = new Book { Title = title };
        book.Chapters.Add(new Chapter(title, body));
        return book;
    }

    // ---------------------------------------------------------------- Writing

    /// <summary>The EPUB 3 file, with an EPUB 2 table of contents too for older readers.</summary>
    public static byte[] Epub(Book book)
    {
        var id = $"urn:uuid:{Guid.NewGuid()}";
        var modified = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var manifest = new List<string>
        {
            "<item id=\"nav\" href=\"nav.xhtml\" media-type=\"application/xhtml+xml\" properties=\"nav\"/>",
            "<item id=\"ncx\" href=\"toc.ncx\" media-type=\"application/x-dtbncx+xml\"/>",
        };
        var spine = new List<string>();
        var navPoints = new List<string>();
        var navItems = new List<string>();
        var files = new List<(string Path, byte[] Data)>();
        for (var i = 0; i < book.Chapters.Count; i++)
        {
            var chapter = book.Chapters[i];
            var file = $"chapter{i + 1:000}.xhtml";
            var title = chapter.Title.Length == 0 ? string.Format(Loc.L("Chapter {0}"), i + 1) : chapter.Title;
            var body = ImageLink().Replace(chapter.Body, "$1=\"images/$2\"");
            files.Add(($"OEBPS/{file}", Encoding.UTF8.GetBytes(XhtmlPage(title, body, book.Language))));
            manifest.Add($"<item id=\"c{i + 1}\" href=\"{file}\" media-type=\"application/xhtml+xml\"/>");
            spine.Add($"<itemref idref=\"c{i + 1}\"/>");
            navItems.Add($"<li><a href=\"{file}\">{Escape(title)}</a></li>");
            navPoints.Add($"<navPoint id=\"p{i + 1}\" playOrder=\"{i + 1}\"><navLabel><text>{Escape(title)}</text></navLabel><content src=\"{file}\"/></navPoint>");
        }
        var names = book.Images.Keys.ToList();
        for (var i = 0; i < names.Count; i++)
        {
            var image = book.Images[names[i]];
            files.Add(($"OEBPS/images/{names[i]}", image.Data));
            var cover = names[i] == book.Cover ? " properties=\"cover-image\"" : "";
            manifest.Add($"<item id=\"img{i + 1}\" href=\"images/{Escape(names[i])}\" media-type=\"{Escape(image.MediaType)}\"{cover}/>");
        }
        var coverMeta = book.Cover is { } c && names.IndexOf(c) is var index and >= 0 ? $"<meta name=\"cover\" content=\"img{index + 1}\"/>" : "";
        var language = Escape(book.Language);
        var opf = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="id" xml:lang="{language}">
              <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                <dc:identifier id="id">{id}</dc:identifier>
                <dc:title>{Escape(book.Title)}</dc:title>
                {(book.Author.Length == 0 ? "" : $"<dc:creator>{Escape(book.Author)}</dc:creator>")}
                <dc:language>{language}</dc:language>
                <meta property="dcterms:modified">{modified}</meta>
                {coverMeta}
              </metadata>
              <manifest>
                {string.Join("\n    ", manifest)}
              </manifest>
              <spine toc="ncx">
                {string.Join("\n    ", spine)}
              </spine>
            </package>
            """;
        var nav = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops" xml:lang="{language}">
            <head><title>{Escape(book.Title)}</title></head>
            <body><nav epub:type="toc"><h1>{Escape(book.Title)}</h1><ol>
            {string.Join("\n", navItems)}
            </ol></nav></body></html>
            """;
        var ncx = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <ncx xmlns="http://www.daisy.org/z3986/2005/ncx/" version="2005-1">
            <head><meta name="dtb:uid" content="{id}"/></head>
            <docTitle><text>{Escape(book.Title)}</text></docTitle>
            <navMap>
            {string.Join("\n", navPoints)}
            </navMap></ncx>
            """;
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string path, byte[] data, CompressionLevel level)
            {
                var entry = zip.CreateEntry(path, level);
                using var stream = entry.Open();
                stream.Write(data);
            }
            // The mimetype comes first and is stored, so readers can recognise the file by its first bytes.
            Add("mimetype", Encoding.ASCII.GetBytes("application/epub+zip"), CompressionLevel.NoCompression);
            Add("META-INF/container.xml", Encoding.UTF8.GetBytes("""
                <?xml version="1.0" encoding="UTF-8"?>
                <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
                  <rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/></rootfiles>
                </container>
                """), CompressionLevel.Optimal);
            Add("OEBPS/content.opf", Encoding.UTF8.GetBytes(opf), CompressionLevel.Optimal);
            Add("OEBPS/nav.xhtml", Encoding.UTF8.GetBytes(nav), CompressionLevel.Optimal);
            Add("OEBPS/toc.ncx", Encoding.UTF8.GetBytes(ncx), CompressionLevel.Optimal);
            foreach (var (path, data) in files)
            {
                Add(path, data, path.EndsWith(".xhtml", StringComparison.Ordinal) ? CompressionLevel.Optimal : CompressionLevel.Fastest);
            }
        }
        return memory.ToArray();
    }

    private static string XhtmlPage(string title, string body, string language) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE html>
        <html xmlns="http://www.w3.org/1999/xhtml" xml:lang="{Escape(language)}">
        <head><meta charset="utf-8"/><title>{Escape(title)}</title></head>
        <body>
        {body}
        </body></html>
        """;

    /// <summary>The FictionBook file: description, one body with a section per chapter, and the pictures as base64.</summary>
    public static string Fb2(Book book)
    {
        var parser = new HtmlParser();
        var sections = new List<string>();
        for (var i = 0; i < book.Chapters.Count; i++)
        {
            var chapter = book.Chapters[i];
            var title = chapter.Title.Length == 0 ? string.Format(Loc.L("Chapter {0}"), i + 1) : chapter.Title;
            var body = parser.ParseDocument($"<html><body>{chapter.Body}</body></html>").Body;
            var blocks = body is null ? [$"<p>{Escape(chapter.Body)}</p>"] : Fb2From(body, title);
            sections.Add($"<section><title><p>{Escape(title)}</p></title>\n{string.Join("\n", blocks)}\n</section>");
        }
        var names = book.Author.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var author = names.Length == 0 ? $"<author><nickname>{Escape(Loc.L("Unknown author"))}</nickname></author>"
            : $"<author><first-name>{Escape(names[0])}</first-name>"
              + (names.Length > 1 ? $"<last-name>{Escape(string.Join(" ", names[1..]))}</last-name>" : "") + "</author>";
        var cover = book.Cover is { } c ? $"<coverpage><image l:href=\"#{Escape(SafeName(c))}\"/></coverpage>" : "";
        var binaries = book.Images.Select(pair =>
            $"<binary id=\"{Escape(SafeName(pair.Key))}\" content-type=\"{Escape(pair.Value.MediaType)}\">{Convert.ToBase64String(pair.Value.Data, Base64FormattingOptions.InsertLineBreaks)}</binary>");
        var date = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <FictionBook xmlns="http://www.gribuser.ru/xml/fictionbook/2.0" xmlns:l="http://www.w3.org/1999/xlink">
            <description>
              <title-info><genre>prose</genre>{author}<book-title>{Escape(book.Title)}</book-title>{cover}<lang>{Escape(book.Language)}</lang></title-info>
              <document-info><author><nickname>Mangosteen</nickname></author><program-used>Mangosteen</program-used><date>{date}</date><id>{Guid.NewGuid()}</id><version>1.0</version></document-info>
            </description>
            <body>
            {string.Join("\n", sections)}
            </body>
            {string.Join("\n", binaries)}
            </FictionBook>
            """;
    }

    /// <summary>
    /// XHTML blocks to FictionBook ones: paragraphs keep emphasis and strong, headings become subtitles, pictures stand
    /// on their own. The chapter's own heading is already its section title.
    /// </summary>
    private static List<string> Fb2From(IElement body, string title)
    {
        var blocks = new List<string>();
        var skipped = false;
        string Inline(INode node)
        {
            if (node is not IElement element)
            {
                return node.NodeType == NodeType.Text ? Escape(node.TextContent) : "";
            }
            var inner = string.Concat(element.ChildNodes.Select(Inline));
            return element.LocalName switch
            {
                "em" or "i" => $"<emphasis>{inner}</emphasis>",
                "strong" or "b" => $"<strong>{inner}</strong>",
                "s" or "strike" or "del" => $"<strikethrough>{inner}</strikethrough>",
                "sup" => $"<sup>{inner}</sup>",
                "sub" => $"<sub>{inner}</sub>",
                "code" => $"<code>{inner}</code>",
                "br" => " ",
                "img" => "",
                _ => inner,
            };
        }
        string ImageBlock(string source) => $"<image l:href=\"#{Escape(SafeName(source[(source.LastIndexOf('/') + 1)..]))}\"/>";
        void Walk(INode node)
        {
            if (node is not IElement element)
            {
                if (node.NodeType == NodeType.Text && node.TextContent.Trim() is { Length: > 0 } text)
                {
                    blocks.Add($"<p>{Escape(text)}</p>");
                }
                return;
            }
            switch (element.LocalName)
            {
                case "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
                    var heading = Clean(element.TextContent);
                    if (!skipped && heading == title)
                    {
                        skipped = true;
                        return;
                    }
                    if (heading.Length > 0)
                    {
                        blocks.Add($"<subtitle>{Escape(heading)}</subtitle>");
                    }
                    break;
                case "p" or "li" or "dt" or "dd" or "pre" or "td" or "th" or "caption" or "figcaption":
                    foreach (var image in element.QuerySelectorAll("img"))
                    {
                        if (image.GetAttribute("src") is { } src)
                        {
                            blocks.Add(ImageBlock(src));
                        }
                    }
                    var paragraph = string.Concat(element.ChildNodes.Select(Inline)).Trim();
                    if (paragraph.Length > 0)
                    {
                        blocks.Add($"<p>{paragraph}</p>");
                    }
                    break;
                case "br" or "hr":
                    blocks.Add("<empty-line/>");
                    break;
                case "img" or "image":
                    if ((element.GetAttribute("src") ?? element.Attributes.FirstOrDefault(a => a.LocalName == "href")?.Value) is { } source)
                    {
                        blocks.Add(ImageBlock(source));
                    }
                    break;
                case "script" or "style":
                    return;
                default:
                    foreach (var child in element.ChildNodes)
                    {
                        Walk(child);
                    }
                    break;
            }
        }
        foreach (var child in body.ChildNodes)
        {
            Walk(child);
        }
        return blocks;
    }

    /// <summary>The whole book as one HTML page; with <paramref name="inlineImages"/> the pictures travel inside it.</summary>
    public static string Html(Book book, bool inlineImages = false)
    {
        var body = new StringBuilder($"<h1>{Escape(book.Title)}</h1>\n");
        if (book.Author.Length > 0)
        {
            body.Append($"<p><em>{Escape(book.Author)}</em></p>\n");
        }
        if (book.Cover is { } cover)
        {
            body.Append($"<p><img src=\"{Escape(cover)}\" alt=\"\"/></p>\n");
        }
        foreach (var chapter in book.Chapters)
        {
            if (!HeadingTag().IsMatch(chapter.Body) && chapter.Title.Length > 0)
            {
                body.Append($"<h2>{Escape(chapter.Title)}</h2>\n");
            }
            body.Append(chapter.Body).Append('\n');
        }
        var text = body.ToString();
        if (inlineImages)
        {
            foreach (var (name, image) in book.Images)
            {
                text = text.Replace($"src=\"{Escape(name)}\"", $"src=\"data:{image.MediaType};base64,{Convert.ToBase64String(image.Data)}\"");
            }
        }
        return $"""
            <!DOCTYPE html>
            <html lang="{Escape(book.Language)}"><head><meta charset="utf-8"><title>{Escape(book.Title)}</title>
            <style>body{"{"}max-width:42em;margin:2em auto;padding:0 1em;font:1.05em/1.55 Georgia,serif{"}"}img{"{"}max-width:100%{"}"}blockquote{"{"}margin-left:2em;font-style:italic{"}"}.verse{"{"}margin:0 0 0 2em{"}"}</style>
            </head><body>
            {text}
            </body></html>
            """;
    }

    // ---------------------------------------------------------------- Helpers

    private static XDocument Xml(byte[] data)
    {
        using var stream = new MemoryStream(data);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
        return XDocument.Load(reader);
    }

    /// <summary>XHTML text in the encoding its XML declaration names (UTF-8 when none).</summary>
    private static string DecodeXmlText(byte[] data)
    {
        var head = Encoding.ASCII.GetString(data, 0, Math.Min(200, data.Length));
        if (EncodingDeclaration().Match(head) is { Success: true } match)
        {
            try
            {
                return Encoding.GetEncoding(match.Groups[1].Value).GetString(data);
            }
            catch (ArgumentException)
            {
            }
        }
        return TextFile.Decode(data);
    }

    /// <summary>The inside of a body element as XHTML.</summary>
    private static string Xhtml(IElement body) =>
        XmlnsAttribute().Replace(string.Concat(body.ChildNodes.Select(n => n.ToHtml(XhtmlMarkupFormatter.Instance))), "");

    private static string Clean(string text) => Regex.Replace(text, @"\s+", " ").Trim();

    /// <summary>A path inside the archive from a link in a file in <paramref name="folder"/> ("../images/a%20b.png").</summary>
    public static string Resolve(string href, string folder)
    {
        var clean = Uri.UnescapeDataString(href.Split('#')[0]);
        var parts = folder.Length == 0 ? new List<string>() : folder.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        foreach (var part in clean.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == "..")
            {
                if (parts.Count > 0)
                {
                    parts.RemoveAt(parts.Count - 1);
                }
            }
            else if (part != ".")
            {
                parts.Add(part);
            }
        }
        return string.Join('/', parts);
    }

    private static string UniqueName(string name, HashSet<string> taken)
    {
        var candidate = SafeName(name);
        for (var number = 2; taken.Contains(candidate); number++)
        {
            candidate = $"{number}-{SafeName(name)}";
        }
        return candidate;
    }

    /// <summary>A name that works as a file name, an XML id and a link: ASCII letters, digits, dot, dash, underscore.</summary>
    public static string SafeName(string name)
    {
        var cleaned = new string(name.Select(c => c < 128 && (char.IsLetterOrDigit(c) || c is '.' or '-' or '_') ? c : '_').ToArray());
        return cleaned.Length > 0 && char.IsLetter(cleaned[0]) ? cleaned : "i" + cleaned;
    }

    public static string Escape(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    [GeneratedRegex(@"(src|href)=""(?!https?:|#|images/|data:)([^""/]+\.(?:jpe?g|png|gif|svg|webp))""", RegexOptions.IgnoreCase)]
    private static partial Regex ImageLink();

    [GeneratedRegex("<h[1-6]", RegexOptions.IgnoreCase)]
    private static partial Regex HeadingTag();

    [GeneratedRegex("""encoding=["']([\w-]+)["']""")]
    private static partial Regex EncodingDeclaration();

    [GeneratedRegex(@" xmlns(:\w+)?=""[^""]*""")]
    private static partial Regex XmlnsAttribute();
}
