using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mangosteen.Conversion;
using Mangosteen.Core;
using Mangosteen.Core.Documents;
using SkiaSharp;
using UglyToad.PdfPig;

namespace Mangosteen.Windows.Tests;

public sealed class ConversionTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mangosteen-conv").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string PathOf(string name) => Path.Combine(_dir, name);

    private static BitmapSource Image(int width = 40, int height = 30)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < width * height; i++)
        {
            pixels[i * 4] = (byte)i;
            pixels[i * 4 + 1] = 0x1E;
            pixels[i * 4 + 2] = 0x67;
            pixels[i * 4 + 3] = (byte)(i % 3 == 0 ? 128 : 255);
        }
        var image = BitmapSource.Create(width, height, 72, 72, PixelFormats.Bgra32, null, pixels, width * 4);
        image.Freeze();
        return image;
    }

    private string Png(string name, int width = 40, int height = 30)
    {
        var path = PathOf(name);
        ImageConverter.WriteImage(Image(width, height), FileFormat.Png, path);
        return path;
    }

    [Fact]
    public void WebpKeepsSizeAndAlpha()
    {
        var output = Converter.Convert(Png("фото.png"), FileFormat.Webp)!;
        Assert.Equal(PathOf("фото.webp"), output);
        using var decoded = SKBitmap.Decode(output);
        Assert.Equal(40, decoded.Width);
        Assert.Equal(30, decoded.Height);
        Assert.NotEqual(SKAlphaType.Opaque, decoded.AlphaType);
    }

    [Fact]
    public void ImageToPdfUsesTheImageSize()
    {
        var output = Converter.Convert(Png("фото.png"), FileFormat.Pdf)!;
        using var pdf = PdfDocument.Open(output);
        var page = Assert.Single(pdf.GetPages());
        // 40 x 30 pixels at 72 dpi = 40 x 30 points.
        Assert.Equal(40, page.Width, 1);
        Assert.Equal(30, page.Height, 1);
    }

    [Fact]
    public void CreatePdfFromSeveralImages()
    {
        var output = Converter.CreatePdf([Png("a.png"), Png("b.png", 20, 60), PathOf("notes.txt")]);
        Assert.Equal(PathOf("Images.pdf"), output);
        using var pdf = PdfDocument.Open(output);
        Assert.Equal(2, pdf.NumberOfPages);
    }

    [Fact]
    public void RendersPdfPages()
    {
        var single = Converter.CreatePdf([Png("one.png", 72, 144)]);
        var png = Converter.Convert(single, FileFormat.Png)!;
        var page = ImageConverter.Decode(png);
        // 72 x 144 points rendered at 300 dpi.
        Assert.Equal(300, page.PixelWidth, 2.0);
        Assert.Equal(600, page.PixelHeight, 2.0);
    }

    [Fact]
    public void SeveralPagesBecomeAFolder()
    {
        var pdf = Converter.CreatePdf([Png("a.png"), Png("b.png"), Png("c.png")]);
        var folder = Converter.Convert(pdf, FileFormat.Jpg)!;
        Assert.Equal(PathOf("Images (JPG)"), folder);
        Assert.Equal(["Images 1.jpg", "Images 2.jpg", "Images 3.jpg"], Directory.GetFiles(folder).Select(Path.GetFileName).Order());
    }

    [Fact]
    public void MergesPdfs()
    {
        var a = Converter.CreatePdf([Png("a.png"), Png("b.png")]);
        var b = Converter.Convert(Png("c.png"), FileFormat.Pdf)!;
        var merged = Converter.MergePdfs([a, b]);
        Assert.Equal(PathOf("Merged.pdf"), merged);
        using var pdf = PdfDocument.Open(merged);
        Assert.Equal(3, pdf.NumberOfPages);
    }

    [Fact]
    public void DocumentToPdfKeepsCyrillicText()
    {
        File.WriteAllText(PathOf("заметки.md"), "# Заголовок\n\nПривет, **мир**! Ёжик и щука.\n\n- пункт\n- ещё\n\n1. раз\n");
        var output = Converter.Convert(PathOf("заметки.md"), FileFormat.Pdf)!;
        var text = PdfText.PlainText(output);
        Assert.Contains("Заголовок", text);
        Assert.Contains("Привет, мир! Ёжик и щука.", text);
        Assert.Contains("пункт", text);
    }

    [Fact]
    public void PdfTextToDocx()
    {
        File.WriteAllText(PathOf("a.md"), "Первая строка абзаца.\n\nВторой абзац.\n");
        var pdf = Converter.Convert(PathOf("a.md"), FileFormat.Pdf)!;
        var docx = Converter.Convert(pdf, FileFormat.Docx)!;
        var document = DocumentConverter.Read(docx, FileFormat.Docx);
        Assert.Contains(document.Blocks, b => b.Text.Contains("Первая строка абзаца."));
    }

    [Fact]
    public void ConvertsEveryKindThroughTheDispatcher()
    {
        File.WriteAllText(PathOf("субтитры.srt"), "1\n00:00:01,000 --> 00:00:02,000\nПривет\n");
        File.WriteAllText(PathOf("таблица.csv"), "a,b\n1,2\n");
        Directory.CreateDirectory(PathOf("папка"));
        File.WriteAllText(Path.Combine(PathOf("папка"), "файл.txt"), "x");

        Assert.Equal(PathOf("субтитры.vtt"), Converter.Convert(PathOf("субтитры.srt"), FileFormat.Vtt));
        Assert.Equal(PathOf("таблица.xlsx"), Converter.Convert(PathOf("таблица.csv"), FileFormat.Xlsx));
        Assert.Equal(PathOf("папка.zip"), Converter.Convert(PathOf("папка"), FileFormat.Zip));
        Assert.Equal(PathOf("папка.tar.gz"), Converter.Convert(PathOf("папка.zip"), FileFormat.Tgz));
        Assert.Null(Converter.Convert(PathOf("таблица.csv"), FileFormat.Csv));
    }

    [Fact]
    public void WorkbookWithSeveralSheetsBecomesAFolder()
    {
        var xlsx = PathOf("книга.xlsx");
        Tables.WriteXlsx([["a"]], "Первый", xlsx);
        // Add a second sheet with the Open XML SDK.
        using (var document = DocumentFormat.OpenXml.Packaging.SpreadsheetDocument.Open(xlsx, true))
        {
            var workbook = document.WorkbookPart!;
            var part = workbook.AddNewPart<DocumentFormat.OpenXml.Packaging.WorksheetPart>();
            part.Worksheet = new DocumentFormat.OpenXml.Spreadsheet.Worksheet(new DocumentFormat.OpenXml.Spreadsheet.SheetData(
                new DocumentFormat.OpenXml.Spreadsheet.Row(new DocumentFormat.OpenXml.Spreadsheet.Cell
                {
                    CellReference = "A1", DataType = DocumentFormat.OpenXml.Spreadsheet.CellValues.InlineString,
                    InlineString = new DocumentFormat.OpenXml.Spreadsheet.InlineString(new DocumentFormat.OpenXml.Spreadsheet.Text("b")),
                }) { RowIndex = 1 }));
            workbook.Workbook!.Sheets!.Append(new DocumentFormat.OpenXml.Spreadsheet.Sheet
            {
                Id = workbook.GetIdOfPart(part), SheetId = 2, Name = "Второй",
            });
        }
        var folder = Converter.Convert(xlsx, FileFormat.Csv)!;
        Assert.Equal(PathOf("книга (CSV)"), folder);
        Assert.Equal(["Второй.csv", "Первый.csv"], Directory.GetFiles(folder).Select(Path.GetFileName).Order());
    }
}
