using System.Windows.Media.Imaging;
using Mangosteen.Core.Documents;
using MigraDoc.DocumentObjectModel;
using MigraDoc.Rendering;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Run = Mangosteen.Core.Documents.Run;

namespace Mangosteen.Conversion;

/// <summary>Writes PDF with PDFsharp and MigraDoc: images as pages, merged PDFs, and documents laid out on A4.</summary>
internal static class PdfWriter
{
    private static readonly object FontSetup = new();
    private static bool _fontsReady;

    /// <summary>MigraDoc and PDFsharp use the fonts installed in Windows (Cyrillic included); see <see cref="WindowsFontResolver"/>.</summary>
    internal static void EnsureFonts()
    {
        lock (FontSetup)
        {
            if (!_fontsReady)
            {
                GlobalFontSettings.FontResolver = new WindowsFontResolver();
                _fontsReady = true;
            }
        }
    }

    /// <summary>One page per image, the page as large as the image at its own resolution.</summary>
    public static void ImagesToPdf(IEnumerable<(BitmapSource Image, bool Photo)> images, string output)
    {
        using var document = new PdfDocument();
        foreach (var (image, photo) in images)
        {
            var stream = new MemoryStream();
            BitmapEncoder encoder = photo ? new JpegBitmapEncoder { QualityLevel = 92 } : new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(photo ? ImageConverter.FlattenOnWhite(image) : image));
            encoder.Save(stream);
            stream.Position = 0;
            using var picture = XImage.FromStream(stream);
            var page = document.AddPage();
            var dpiX = image.DpiX > 1 ? image.DpiX : 96;
            var dpiY = image.DpiY > 1 ? image.DpiY : 96;
            page.Width = XUnit.FromPoint(image.PixelWidth * 72.0 / dpiX);
            page.Height = XUnit.FromPoint(image.PixelHeight * 72.0 / dpiY);
            using var graphics = XGraphics.FromPdfPage(page);
            graphics.DrawImage(picture, 0, 0, page.Width.Point, page.Height.Point);
        }
        if (document.PageCount == 0)
        {
            throw new InvalidOperationException("There are no images to put into the PDF.");
        }
        document.Save(output);
    }

    /// <summary>Concatenates PDFs in the given order.</summary>
    public static void Merge(IReadOnlyList<string> inputs, string output)
    {
        using var merged = new PdfDocument();
        foreach (var input in inputs)
        {
            PdfDocument source;
            try
            {
                source = PdfReader.Open(input, PdfDocumentOpenMode.Import);
            }
            catch (PdfReaderException error)
            {
                throw new InvalidDataException($"{Path.GetFileName(input)} could not be opened: {error.Message}", error);
            }
            using (source)
            {
                foreach (var page in source.Pages)
                {
                    merged.AddPage(page);
                }
            }
        }
        merged.Save(output);
    }

    private static readonly double[] HeadingSizes = [20, 16, 14, 12.5, 11.5, 11];

    /// <summary>Lays a document out on A4 pages.</summary>
    public static void WriteDocument(RichDocument source, string title, string output)
    {
        EnsureFonts();
        var document = new Document();
        document.Info.Title = title;
        var normal = document.Styles[StyleNames.Normal]!;
        normal.Font.Name = "Segoe UI";
        normal.Font.Size = 11;
        normal.ParagraphFormat.SpaceAfter = Unit.FromPoint(6);
        for (var level = 1; level <= 6; level++)
        {
            var heading = document.Styles[$"Heading{level}"]!;
            heading.Font.Name = "Segoe UI";
            heading.Font.Size = HeadingSizes[level - 1];
            heading.Font.Bold = true;
            heading.ParagraphFormat.SpaceBefore = Unit.FromPoint(12);
            heading.ParagraphFormat.SpaceAfter = Unit.FromPoint(6);
            heading.ParagraphFormat.KeepWithNext = true;
        }
        var code = document.Styles.AddStyle("Code", StyleNames.Normal);
        code.Font.Name = "Consolas";
        code.Font.Size = 9.5;
        code.ParagraphFormat.SpaceAfter = 0;
        code.ParagraphFormat.Shading.Color = new Color(0xF4, 0xF4, 0xF4);

        var section = document.AddSection();
        section.PageSetup = document.DefaultPageSetup.Clone();
        section.PageSetup.PageFormat = PageFormat.A4;
        section.PageSetup.TopMargin = section.PageSetup.BottomMargin = Unit.FromCentimeter(2);
        section.PageSetup.LeftMargin = section.PageSetup.RightMargin = Unit.FromCentimeter(2);

        Block? previous = null;
        foreach (var block in source.Blocks)
        {
            var paragraph = section.AddParagraph();
            switch (block.Type)
            {
                case BlockType.Heading:
                    paragraph.Style = $"Heading{Math.Clamp(block.Level, 1, 6)}";
                    break;
                case BlockType.Code:
                    paragraph.Style = "Code";
                    break;
                case BlockType.ListItem:
                    var depth = Math.Min(block.Level, 2);
                    paragraph.Format.ListInfo = new ListInfo
                    {
                        ListType = block.Ordered
                            ? depth switch { 0 => ListType.NumberList1, 1 => ListType.NumberList2, _ => ListType.NumberList3 }
                            : depth switch { 0 => ListType.BulletList1, 1 => ListType.BulletList2, _ => ListType.BulletList3 },
                        ContinuePreviousList = previous?.Type == BlockType.ListItem,
                        NumberPosition = Unit.FromCentimeter(0.6 * block.Level),
                    };
                    paragraph.Format.LeftIndent = Unit.FromCentimeter(0.6 * (block.Level + 1));
                    paragraph.Format.SpaceAfter = Unit.FromPoint(2);
                    break;
            }
            foreach (var run in block.Runs)
            {
                AddRun(paragraph, run, block.Type == BlockType.Code);
            }
            previous = block;
        }

        var renderer = new PdfDocumentRenderer { Document = document };
        renderer.RenderDocument();
        renderer.PdfDocument.Save(output);
    }

    private static void AddRun(MigraDoc.DocumentObjectModel.Paragraph paragraph, Run run, bool inCode)
    {
        FormattedText text;
        if (run.Link is { } link)
        {
            var hyperlink = paragraph.AddHyperlink(link, HyperlinkType.Web);
            text = hyperlink.AddFormattedText();
            text.Color = new Color(0x05, 0x63, 0xC1);
            text.Underline = Underline.Single;
        }
        else
        {
            text = paragraph.AddFormattedText();
        }
        text.Bold = run.Bold;
        text.Italic = run.Italic;
        if (run.Underline)
        {
            text.Underline = Underline.Single;
        }
        if (run.Code && !inCode)
        {
            text.Font.Name = "Consolas";
        }
        var lines = run.Text.Split('\n');
        for (var l = 0; l < lines.Length; l++)
        {
            if (l > 0)
            {
                text.AddLineBreak();
            }
            var parts = lines[l].Split('\t');
            for (var t = 0; t < parts.Length; t++)
            {
                if (t > 0)
                {
                    text.AddTab();
                }
                if (parts[t].Length > 0)
                {
                    text.AddText(parts[t]);
                }
            }
        }
    }
}
