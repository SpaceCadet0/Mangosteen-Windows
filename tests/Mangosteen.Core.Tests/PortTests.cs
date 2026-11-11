using System.IO.Compression;
using System.Text;
using Mangosteen.Core.Documents;
using Mangosteen.Core.Media;

namespace Mangosteen.Core.Tests;

/// <summary>Features ported from the macOS app after Windows 1.3.4.</summary>
public class WheelLayoutTests
{
    private static List<string> Ids(int count) => Enumerable.Range(1, count).Select(i => $"tool.t{i}").ToList();

    [Fact]
    public void FaceHoldsTenAndTheRestGoesBehind()
    {
        var (front, back) = WheelLayout.Split(Ids(14), id => id, null);
        Assert.Equal(10, front.Count);
        Assert.Equal(["tool.t11", "tool.t12", "tool.t13", "tool.t14"], back);
    }

    [Fact]
    public void NewSegmentsStartOnTheBackAndChoicesWin()
    {
        var items = new List<string> { "tool.crop", "tool.trace", "tool.edit" };
        var (front, back) = WheelLayout.Split(items, id => id, null);
        Assert.Equal(["tool.crop", "tool.edit"], front);
        Assert.Equal(["tool.trace"], back);

        var choices = new Dictionary<string, SlicePlacement> { ["tool.trace"] = SlicePlacement.Front, ["tool.edit"] = SlicePlacement.Hidden };
        (front, back) = WheelLayout.Split(items, id => id, choices);
        Assert.Equal(["tool.crop", "tool.trace"], front);
        Assert.Empty(back);
    }

    [Fact]
    public void AnEmptyFaceShowsTheBack()
    {
        var choices = new Dictionary<string, SlicePlacement> { ["a"] = SlicePlacement.Back, ["b"] = SlicePlacement.Back };
        var (front, back) = WheelLayout.Split(["a", "b"], id => id, choices);
        Assert.Equal(["a", "b"], front);
        Assert.Empty(back);
    }

    [Fact]
    public void VideoToolsPutTheNewOnesBehind()
    {
        FormatCatalog.MediaAvailable = true;
        try
        {
            var sides = WheelCatalog.Sides(WheelKind.Tools, ["clip.mp4"], null);
            Assert.Contains(sides.Back, i => i.Id == "tool.extractSubtitles");
            Assert.Contains(sides.Back, i => i.Id == "tool.removeBars");
            Assert.Contains(sides.Back, i => i.Id == "tool.hdrToSDR");
            Assert.True(sides.Front.Count <= WheelLayout.MaxFront);
        }
        finally
        {
            FormatCatalog.MediaAvailable = false;
        }
    }

    [Fact]
    public void EveryToolIdIsListedForSettings()
    {
        var ids = WheelCatalog.AllItems(WheelKind.Tools).Select(i => i.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.Contains("tool.trace", ids);
        Assert.Contains("tool.pagesPerSheet", ids);
    }
}

public class FormatPortTests
{
    [Fact]
    public void EveryFormatHasACategory()
    {
        foreach (var format in Enum.GetValues<FileFormat>())
        {
            _ = format.Category();
        }
    }

    [Theory]
    [InlineData("book.fb2.zip", FileFormat.Fb2)]
    [InlineData("book.epub", FileFormat.Epub)]
    [InlineData("a.tar.xz", FileFormat.Txz)]
    [InlineData("a.tar.bz2", FileFormat.Tbz)]
    [InlineData("a.7z", FileFormat.SevenZip)]
    [InlineData("a.rar", FileFormat.Rar)]
    [InlineData("a.ssa", FileFormat.Ass)]
    [InlineData("data.json", FileFormat.Json)]
    [InlineData("IMG_0001.CR3", FileFormat.Raw)]
    [InlineData("a.jxl", FileFormat.Jxl)]
    [InlineData("clip.3gp", FileFormat.ThreeGP)]
    public void NewFormatsAreDetected(string name, FileFormat format) => Assert.Equal(format, FileFormats.Detect(name));

    [Fact]
    public void TypeScriptIsNotVideo()
    {
        var folder = Directory.CreateTempSubdirectory("mangosteen-ts").FullName;
        try
        {
            var code = Path.Combine(folder, "app.ts");
            File.WriteAllText(code, "export const answer = 42;\n" + new string(' ', 300));
            Assert.Null(FileFormats.Detect(code));
            var stream = Path.Combine(folder, "clip.ts");
            var bytes = new byte[400];
            bytes[0] = 0x47;
            bytes[188] = 0x47;
            File.WriteAllBytes(stream, bytes);
            Assert.Equal(FileFormat.Ts, FileFormats.Detect(stream));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void BooksTablesAndSubtitlesGetTheirTargets()
    {
        Assert.Contains(FileFormat.Fb2, FormatCatalog.Targets(["a.epub"]));
        Assert.Contains(FileFormat.Pdf, FormatCatalog.Targets(["a.fb2"]));
        Assert.Contains(FileFormat.Epub, FormatCatalog.Targets(["a.docx"]));
        Assert.Contains(FileFormat.Json, FormatCatalog.Targets(["a.csv"]));
        Assert.Contains(FileFormat.Xlsx, FormatCatalog.Targets(["a.json"]));
        Assert.Contains(FileFormat.Ass, FormatCatalog.Targets(["a.srt"]));
        Assert.Contains(FileFormat.Srt, FormatCatalog.Targets(["a.ass"]));
        Assert.Contains(FileFormat.Zip, FormatCatalog.Targets(["a.7z"]));
    }

    [Fact]
    public void MagickFormatsNeedMagick()
    {
        Assert.Empty(FormatCatalog.Targets(["a.psd"]));
        FormatCatalog.MagickAvailable = true;
        try
        {
            Assert.Contains(FileFormat.Png, FormatCatalog.Targets(["a.psd"]));
            Assert.Contains(FileFormat.Exr, FormatCatalog.Targets(["a.png"]));
            Assert.Equal(FileFormat.Jpg, FormatCatalog.EditedFormat(FileFormat.Raw));
            Assert.Equal(FileFormat.Png, FormatCatalog.EditedFormat(FileFormat.Psd));
        }
        finally
        {
            FormatCatalog.MagickAvailable = false;
        }
    }
}

public class OutputLocationTests
{
    private static readonly DateTime Moment = new(2026, 11, 10, 9, 5, 7);

    [Fact]
    public void TemplateFillsTheTokens() =>
        Assert.Equal("photo 2026-11-10 09-05-07 PNG", OutputLocation.Name("photo", "png", "{name} {date} {time} {format}", Moment));

    [Fact]
    public void PlainTemplateKeepsTheName() => Assert.Equal("photo", OutputLocation.Name("photo", "png", "{name}", Moment));

    [Fact]
    public void ForbiddenCharactersBecomeDashesButTheNameStays() =>
        Assert.Equal("a-b photo", OutputLocation.Name("photo", "png", "a:b {name}", Moment));

    [Fact]
    public void FolderModeSendsResultsThereExceptFromInsideIt()
    {
        var policy = new OutputPolicy(SaveMode.Folder, @"C:\Out".Replace('\\', Path.DirectorySeparatorChar));
        var root = Path.GetFullPath(policy.Folder!);
        Assert.Equal(root, OutputLocation.Directory(Path.GetFullPath("photos"), policy, null, _ => true));
        var inside = Path.Combine(root, "sub");
        Assert.Equal(inside, OutputLocation.Directory(inside, policy, null, _ => true));
    }

    [Fact]
    public void ReadOnlyFolderFallsBackToDownloads()
    {
        var source = Path.GetFullPath("readonly-disc");
        Assert.Equal(OutputLocation.Downloads, OutputLocation.Directory(source, new OutputPolicy(), null, p => p != source));
    }

    [Fact]
    public void WriteAtomicallyFollowsThePolicy()
    {
        var folder = Directory.CreateTempSubdirectory("mangosteen-policy").FullName;
        var saved = OutputLocation.Policy;
        try
        {
            var target = Path.Combine(folder, "out");
            OutputLocation.Policy = new OutputPolicy(SaveMode.Folder, target, "{name} ({format})");
            var result = OutputNaming.WriteAtomically(Path.Combine(folder, "in"), "photo", "jpg", p => File.WriteAllText(p, "x"));
            Assert.Equal(Path.Combine(target, "photo (JPG).jpg"), result);
        }
        finally
        {
            OutputLocation.Policy = saved;
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void PresetQualityBecomesAPercentage() =>
        Assert.Equal(new ConversionOptions(70, 1600, false), new ConversionPreset { Quality = 0.7, MaxPixelSize = 1600, PreserveMetadata = false }.Options);
}

public class SubtitleAndTablePortTests
{
    private const string AssText = """
        [Script Info]
        Title: test

        [Events]
        Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
        Dialogue: 0,0:00:02.00,0:00:03.50,Default,,0,0,0,,Second, with a comma
        Dialogue: 0,0:00:00.50,0:00:01.25,Default,,0,0,0,,{\i1}Hello{\i0}\Nworld{\pos(10,10)}
        """;

    [Fact]
    public void AssIsReadInTimeOrderWithItalics()
    {
        var cues = Subtitles.ParseAss(AssText);
        Assert.Equal(2, cues.Count);
        Assert.Equal(0.5, cues[0].Start, 3);
        Assert.Equal("<i>Hello</i>\nworld", cues[0].Text);
        Assert.Equal("Second, with a comma", cues[1].Text);
    }

    [Fact]
    public void AssRoundTrips()
    {
        var cues = new List<Subtitles.Cue> { new(1, 2.5, "<b>Bold</b>\nline") };
        var text = Subtitles.Ass(cues);
        Assert.Contains("Dialogue: 0,0:00:01.00,0:00:02.50,Default,,0,0,0,,{\\b1}Bold{\\b0}\\Nline", text);
        Assert.Equal(cues, Subtitles.ParseAss(text));
    }

    [Fact]
    public void JsonKeepsKeyOrderAndFlattensObjects()
    {
        var rows = JsonTable.Rows("""[{"z":1,"a":{"b":"x","c":true}},{"a":{"b":"y"},"z":"007","extra":null}]""");
        Assert.Equal(["z", "a.b", "a.c", "extra"], rows[0]);
        Assert.Equal(["1", "x", "true", ""], rows[1]);
        Assert.Equal(["007", "y", "", ""], rows[2]);
    }

    [Fact]
    public void JsonWritesNumbersAndKeepsLeadingZerosAsText()
    {
        var json = JsonTable.Serialize([["id", "code", "ok"], ["1.50", "007", "true"]]);
        Assert.Contains("\"id\": 1.50", json);
        Assert.Contains("\"code\": \"007\"", json);
        Assert.Contains("\"ok\": true", json);
        Assert.Equal([["id", "code", "ok"], ["1.50", "007", "true"]], JsonTable.Rows(json));
    }

    [Fact]
    public void JsonThatIsNotATableIsRefused() => Assert.Throws<InvalidDataException>(() => JsonTable.Rows("""{"a":1}"""));

    [Theory]
    [InlineData("a;b;c\n1;2;3", ';')]
    [InlineData("a,b\n1,2", ',')]
    [InlineData("\"x;y\",b\n", ',')]
    [InlineData("a\tb\tc", '\t')]
    public void CsvSeparatorIsDetected(string text, char separator) => Assert.Equal(separator, Tables.DetectedSeparator(text));

    [Fact]
    public void WesternEuropeanSingleByteTextIsNotReadAsCyrillic()
    {
        var spanish = Encoding.GetEncoding(1252).GetBytes("El año pasado, más café.");
        Assert.Equal("El año pasado, más café.", TextFile.Decode(spanish));
        var russian = Encoding.GetEncoding(1251).GetBytes("Привет, мир");
        Assert.Equal("Привет, мир", TextFile.Decode(russian));
    }

    static SubtitleAndTablePortTests() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
}

public class PdfAndVideoPortTests
{
    [Fact]
    public void RangesBecomeDocuments()
    {
        var selection = PdfSplit.FromRanges("1-3, 8, 12-", 14);
        Assert.NotNull(selection);
        var documents = PdfSplit.Documents(14, selection.Value.Cuts, selection.Value.Excluded);
        Assert.Equal([[0, 1, 2], [7], [11, 12, 13]], documents);
    }

    [Theory]
    [InlineData("3-1")]
    [InlineData("1-3, 2")]
    [InlineData("1-20")]
    [InlineData("abc")]
    [InlineData("")]
    public void BadRangesAreRefused(string text) => Assert.Null(PdfSplit.FromRanges(text, 10));

    [Fact]
    public void OpenStartRunsFromPageOne() =>
        Assert.Equal([[0, 1, 2]], PdfSplit.Documents(5, PdfSplit.FromRanges("–3", 5)!.Value.Cuts, PdfSplit.FromRanges("–3", 5)!.Value.Excluded));

    [Fact]
    public void LastCropIsRead() =>
        Assert.Equal(new CropDetect.Box(0, 140, 1920, 800), CropDetect.LastCrop("crop=1920:1080:0:0\n... crop=1920:800:0:140\n"));

    [Fact]
    public void PictureAreaHoldsEveryMomentAndIgnoresThinEdges()
    {
        var area = CropDetect.PictureArea([new(0, 140, 1920, 800), new(0, 138, 1920, 804)], 1920, 1080);
        Assert.Equal(new CropDetect.Box(0, 138, 1920, 804), area);
        Assert.Null(CropDetect.PictureArea([new(2, 2, 1916, 1076)], 1920, 1080));
    }

    [Fact]
    public void CodecChoiceGivesTheEncoder()
    {
        Assert.Contains("libx264", new VideoEncoding().Arguments(FileFormat.Mp4));
        Assert.Contains("libx265", new VideoEncoding(VideoCodec.Hevc).Arguments(FileFormat.Mp4));
        Assert.Contains("hvc1", new VideoEncoding(VideoCodec.Hevc).Arguments(FileFormat.Mov));
        Assert.Contains("libsvtav1", new VideoEncoding(VideoCodec.Av1).Arguments(FileFormat.Mkv));
        // MOV cannot hold AV1.
        Assert.Contains("libx264", new VideoEncoding(VideoCodec.Av1).Arguments(FileFormat.Mov));
    }

    [Fact]
    public void HardwareIsUsedOnlyWhenItWorked()
    {
        var saved = VideoEncoding.WorkingHardwareEncoders;
        try
        {
            VideoEncoding.WorkingHardwareEncoders = new HashSet<string>();
            Assert.Contains("libx264", new VideoEncoding(VideoCodec.H264, true).Arguments(FileFormat.Mp4));
            VideoEncoding.WorkingHardwareEncoders = new HashSet<string> { "h264_qsv", "hevc_nvenc" };
            Assert.Contains("h264_qsv", new VideoEncoding(VideoCodec.H264, true).Arguments(FileFormat.Mp4));
            Assert.Contains("hevc_nvenc", new VideoEncoding(VideoCodec.Hevc, true).Arguments(FileFormat.Mp4));
        }
        finally
        {
            VideoEncoding.WorkingHardwareEncoders = saved;
        }
    }
}

public class BookTests
{
    static BookTests() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private static BookConverter.Book Sample()
    {
        var book = new BookConverter.Book { Title = "Книга", Author = "Иван Петров", Language = "ru" };
        book.Chapters.Add(new BookConverter.Chapter("Первая", "<h2>Первая</h2><p>Текст <em>курсивом</em>.</p><p><img src=\"pic.png\" alt=\"\"/></p>"));
        book.Chapters.Add(new BookConverter.Chapter("Вторая", "<p>Ещё текст &amp; знаки.</p>"));
        book.Images["pic.png"] = new BookConverter.Picture([0x89, 0x50, 0x4E, 0x47, 1, 2, 3], "image/png");
        book.Cover = "pic.png";
        return book;
    }

    [Fact]
    public void EpubStartsWithAStoredMimetype()
    {
        var data = BookConverter.Epub(Sample());
        Assert.Equal("PK", Encoding.ASCII.GetString(data, 0, 2));
        Assert.Equal(0, BitConverter.ToUInt16(data, 8)); // compression method: stored
        Assert.Equal("mimetype", Encoding.ASCII.GetString(data, 30, 8));
        Assert.Equal("application/epub+zip", Encoding.ASCII.GetString(data, 30 + 8 + BitConverter.ToUInt16(data, 28), 20));
        using var zip = new ZipArchive(new MemoryStream(data));
        Assert.NotNull(zip.GetEntry("OEBPS/nav.xhtml"));
        Assert.NotNull(zip.GetEntry("OEBPS/toc.ncx"));
        Assert.NotNull(zip.GetEntry("OEBPS/images/pic.png"));
    }

    [Fact]
    public void EpubRoundTripsChaptersAndPictures()
    {
        var book = BookConverter.ReadEpub(BookConverter.Epub(Sample()), "fallback");
        Assert.Equal("Книга", book.Title);
        Assert.Equal("Иван Петров", book.Author);
        Assert.Equal(2, book.Chapters.Count);
        Assert.Equal("Первая", book.Chapters[0].Title);
        Assert.Contains("курсивом", book.Chapters[0].Body);
        Assert.Single(book.Images);
        Assert.Equal("pic.png", book.Cover);
        Assert.Contains("src=\"pic.png\"", book.Chapters[0].Body);
    }

    [Fact]
    public void Fb2RoundTrips()
    {
        var text = BookConverter.Fb2(Sample());
        var book = BookConverter.ReadFb2(Encoding.UTF8.GetBytes(text), "fallback");
        Assert.Equal("Книга", book.Title);
        Assert.Equal("Иван Петров", book.Author);
        Assert.Equal(["Первая", "Вторая"], book.Chapters.Select(c => c.Title));
        Assert.Contains("<em>курсивом</em>", book.Chapters[0].Body);
        Assert.Equal([0x89, 0x50, 0x4E, 0x47, 1, 2, 3], book.Images["pic.png"].Data);
    }

    [Fact]
    public void ZippedWindows1251Fb2IsRead()
    {
        const string xml = """
            <?xml version="1.0" encoding="windows-1251"?>
            <FictionBook xmlns="http://www.gribuser.ru/xml/fictionbook/2.0">
            <description><title-info><author><first-name>Анна</first-name><last-name>Каренина</last-name></author><book-title>Роман</book-title><lang>ru</lang></title-info></description>
            <body><section><title><p>Глава 1</p></title><p>Все счастливые семьи похожи друг на друга.</p></section></body>
            </FictionBook>
            """;
        var folder = Directory.CreateTempSubdirectory("mangosteen-fb2").FullName;
        try
        {
            var path = Path.Combine(folder, "novel.fb2.zip");
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                using var stream = zip.CreateEntry("novel.fb2").Open();
                stream.Write(Encoding.GetEncoding(1251).GetBytes(xml));
            }
            var book = BookConverter.Read(path, FileFormats.Detect(path)!.Value);
            Assert.Equal("Роман", book.Title);
            Assert.Equal("Анна Каренина", book.Author);
            Assert.Contains("счастливые", book.Chapters[0].Body);

            // And on to other formats through the document converter.
            var epub = Path.Combine(folder, "novel.epub");
            Documents.DocumentConverter.Convert(path, FileFormat.Fb2, FileFormat.Epub, epub);
            Assert.Equal("Роман", BookConverter.Read(epub, FileFormat.Epub).Title);
            var txt = Path.Combine(folder, "novel.txt");
            Documents.DocumentConverter.Convert(path, FileFormat.Fb2, FileFormat.Txt, txt);
            Assert.Contains("счастливые семьи", File.ReadAllText(txt));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void DocumentsBecomeOneChapterBooks()
    {
        var folder = Directory.CreateTempSubdirectory("mangosteen-doc-book").FullName;
        try
        {
            var md = Path.Combine(folder, "Notes.md");
            File.WriteAllText(md, "# Title\n\nSome *text*.\n");
            var fb2 = Path.Combine(folder, "Notes.fb2");
            Documents.DocumentConverter.Convert(md, FileFormat.Md, FileFormat.Fb2, fb2);
            var book = BookConverter.Read(fb2, FileFormat.Fb2);
            Assert.Equal("Notes", book.Title);
            Assert.Contains("text", book.Chapters[0].Body);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}

public class PortLanguageTests
{
    [Theory]
    [InlineData("de", "Konvertieren")]
    [InlineData("fr", "Convertir")]
    [InlineData("pt-BR", "Converter")]
    [InlineData("zh-Hans", "转换")]
    [InlineData("ja", "変換")]
    public void NewLanguagesAreLoaded(string code, string convert)
    {
        try
        {
            Loc.SetLanguage(code);
            Assert.Equal(code, Loc.Language);
            Assert.Equal(convert, Loc.L("Convert"));
        }
        finally
        {
            Loc.SetLanguage("en");
        }
    }

    [Fact]
    public void EveryLanguageHasEveryString()
    {
        var russian = Loc.Load("ru").Keys.ToHashSet();
        Assert.True(russian.Count > 500);
        foreach (var (code, _) in Loc.Languages.Skip(1))
        {
            Assert.Equal(russian, Loc.Load(code).Keys.ToHashSet());
        }
    }

    [Theory]
    [InlineData("de", 1, "1 Datei")]
    [InlineData("de", 3, "3 Dateien")]
    [InlineData("fr", 0, "0 fichier")]
    [InlineData("zh-Hans", 3, "3 个文件")]
    [InlineData("ja", 2, "2 個のファイル")]
    public void FilesAreCounted(string code, int count, string text)
    {
        try
        {
            Loc.SetLanguage(code);
            Assert.Equal(text, WheelCatalog.FileCount(count));
        }
        finally
        {
            Loc.SetLanguage("en");
        }
    }

    [Theory]
    [InlineData("pt-PT", "pt-BR")]
    [InlineData("zh-CN", "zh-Hans")]
    [InlineData("de-AT", "de")]
    [InlineData("it-IT", "en")]
    public void WindowsLanguageIsMapped(string culture, string code) =>
        Assert.Equal(code, Loc.FromCulture(new System.Globalization.CultureInfo(culture)));
}
