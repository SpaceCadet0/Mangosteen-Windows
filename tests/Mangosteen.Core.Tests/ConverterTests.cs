using System.IO.Compression;
using System.Text;
using Mangosteen.Core;
using Mangosteen.Core.Documents;

namespace Mangosteen.Core.Tests;

public abstract class TempFolderTest : IDisposable
{
    protected readonly string Dir = Directory.CreateTempSubdirectory("mangosteen-tests").FullName;

    static TempFolderTest() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public void Dispose()
    {
        Directory.Delete(Dir, recursive: true);
        GC.SuppressFinalize(this);
    }

    protected string PathOf(string name) => Path.Combine(Dir, name);
}

public class TextFileTests : TempFolderTest
{
    [Fact]
    public void ReadsWindows1251()
    {
        var path = PathOf("старый.txt");
        File.WriteAllBytes(path, Encoding.GetEncoding(1251).GetBytes("Привет, мир"));
        Assert.Equal("Привет, мир", TextFile.Read(path));
    }

    [Fact]
    public void ReadsUtf8WithAndWithoutBom()
    {
        File.WriteAllText(PathOf("a.txt"), "Ёжик", new UTF8Encoding(true));
        File.WriteAllText(PathOf("b.txt"), "Ёжик", new UTF8Encoding(false));
        Assert.Equal("Ёжик", TextFile.Read(PathOf("a.txt")));
        Assert.Equal("Ёжик", TextFile.Read(PathOf("b.txt")));
    }

    [Fact]
    public void ReadsUtf16() 
    {
        File.WriteAllText(PathOf("u.txt"), "Ёжик", Encoding.Unicode);
        Assert.Equal("Ёжик", TextFile.Read(PathOf("u.txt")));
    }
}

public class SubtitleTests : TempFolderTest
{
    private const string Srt = "1\r\n00:00:01,500 --> 00:00:03,000\r\nПривет\r\n\r\n2\r\n00:01:02,000 --> 00:01:04,250\r\n<i>Пока</i>\r\n";

    [Fact]
    public void SrtToVttAndBack()
    {
        File.WriteAllBytes(PathOf("фильм.srt"), Encoding.GetEncoding(1251).GetBytes(Srt));
        Subtitles.Convert(PathOf("фильм.srt"), FileFormat.Vtt, PathOf("out.vtt"));
        var vtt = File.ReadAllText(PathOf("out.vtt"));
        Assert.StartsWith("WEBVTT\n\n00:00:01.500 --> 00:00:03.000\nПривет", vtt);

        Subtitles.Convert(PathOf("out.vtt"), FileFormat.Srt, PathOf("back.srt"));
        Assert.Contains("2\n00:01:02,000 --> 00:01:04,250\n<i>Пока</i>", File.ReadAllText(PathOf("back.srt")));
    }

    [Fact]
    public void ToTextStripsTags()
    {
        File.WriteAllText(PathOf("a.srt"), Srt);
        Subtitles.Convert(PathOf("a.srt"), FileFormat.Txt, PathOf("a.txt"));
        Assert.Equal("Привет\nПока\n", File.ReadAllText(PathOf("a.txt")));
    }

    [Fact]
    public void EmptyFileFails()
    {
        File.WriteAllText(PathOf("empty.srt"), "nothing here");
        Assert.Throws<InvalidDataException>(() => Subtitles.Convert(PathOf("empty.srt"), FileFormat.Vtt, PathOf("x.vtt")));
    }
}

public class TableTests : TempFolderTest
{
    [Fact]
    public void CsvQuotingRoundTrips()
    {
        var rows = Tables.Parse("name,note\r\n\"Иванов, И.\",\"he said \"\"hi\"\"\nnext line\"\r\nПётр,\r\n", ',');
        Assert.Equal(3, rows.Count);
        Assert.Equal(["Иванов, И.", "he said \"hi\"\nnext line"], rows[1]);
        Assert.Equal(["Пётр", ""], rows[2]);
        Assert.Equal(rows, Tables.Parse(Tables.Serialize(rows, ','), ','));
    }

    [Fact]
    public void CsvToXlsxToTsv()
    {
        File.WriteAllBytes(PathOf("данные.csv"), Encoding.GetEncoding(1251).GetBytes("Город,Население\nМосква,13010112\nКод,007\n"));
        Tables.Convert(PathOf("данные.csv"), FileFormat.Csv, FileFormat.Xlsx, PathOf("данные.xlsx"));
        var sheets = Tables.ReadXlsx(PathOf("данные.xlsx"));
        Assert.Single(sheets);
        Assert.Equal("данные", sheets[0].Name);
        Assert.Equal(["Москва", "13010112"], sheets[0].Rows[1]);
        Assert.Equal(["Код", "007"], sheets[0].Rows[2]);

        Tables.Convert(PathOf("данные.xlsx"), FileFormat.Xlsx, FileFormat.Tsv, PathOf("данные.tsv"));
        Assert.Equal("Город\tНаселение\nМосква\t13010112\nКод\t007\n", File.ReadAllText(PathOf("данные.tsv")));
    }

    [Fact]
    public void SheetsBecomeFiles()
    {
        Tables.WriteSheets([new Tables.Sheet("Лист: 1", [["a"]]), new Tables.Sheet("Two", [["b"]])], FileFormat.Csv, PathOf("out"));
        Assert.Equal(["Two.csv", "Лист- 1.csv"], Directory.GetFiles(PathOf("out")).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("A1", 0)]
    [InlineData("Z9", 25)]
    [InlineData("AA10", 26)]
    [InlineData("BC3", 54)]
    public void ColumnNames(string reference, int index)
    {
        Assert.Equal(index, Tables.ColumnIndex(reference));
        Assert.StartsWith(Tables.ColumnName(index), reference);
    }
}

public class ArchiveTests : TempFolderTest
{
    private string MakeFolder()
    {
        var folder = PathOf("Папка");
        Directory.CreateDirectory(Path.Combine(folder, "вложенная"));
        File.WriteAllText(Path.Combine(folder, "файл.txt"), "один");
        File.WriteAllText(Path.Combine(folder, "вложенная", "два.txt"), "два");
        return folder;
    }

    [Theory]
    [InlineData(FileFormat.Zip)]
    [InlineData(FileFormat.Tar)]
    [InlineData(FileFormat.Tgz)]
    public void FolderRoundTrips(FileFormat format)
    {
        var folder = MakeFolder();
        var archive = PathOf("архив." + format.Extension());
        Archives.Pack([folder], format, archive);
        var extracted = Archives.ExtractNextToArchive(archive);
        Assert.Equal(PathOf("архив"), extracted);
        Assert.Equal("два", File.ReadAllText(Path.Combine(extracted, "Папка", "вложенная", "два.txt")));
        Assert.Equal("один", File.ReadAllText(Path.Combine(extracted, "Папка", "файл.txt")));
    }

    [Fact]
    public void RepacksZipToTarGz()
    {
        var zip = PathOf("a.zip");
        Archives.Pack([MakeFolder()], FileFormat.Zip, zip);
        Archives.Repack(zip, FileFormat.Zip, FileFormat.Tgz, PathOf("a.tar.gz"));
        var extracted = Archives.ExtractNextToArchive(PathOf("a.tar.gz"));
        Assert.True(File.Exists(Path.Combine(extracted, "Папка", "файл.txt")));
    }

    [Fact]
    public void ReadsUtf8NamesWithoutTheFlag()
    {
        // Some tools write UTF-8 names without setting the UTF-8 flag.
        var zip = PathOf("legacy.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create, new UTF8Encoding(false)))
        {
            using var writer = new StreamWriter(archive.CreateEntry("отчёт.txt").Open());
            writer.Write("ok");
        }
        var extracted = Archives.ExtractNextToArchive(zip);
        Assert.Equal("ok", File.ReadAllText(Path.Combine(extracted, "отчёт.txt")));
    }

    [Fact]
    public void SingleFileKeepsItsName()
    {
        File.WriteAllText(PathOf("заметка.txt"), "x");
        Archives.Pack([PathOf("заметка.txt")], FileFormat.Zip, PathOf("z.zip"));
        using var archive = ZipFile.OpenRead(PathOf("z.zip"));
        Assert.Equal("заметка.txt", Assert.Single(archive.Entries).FullName);
    }
}

public class FolderOutputTests : TempFolderTest
{
    [Fact]
    public void WriteAtomicallyCreatesFolders()
    {
        Directory.CreateDirectory(PathOf("pages"));
        var result = OutputNaming.WriteAtomically(Dir, "pages", "", temp =>
        {
            Directory.CreateDirectory(temp);
            File.WriteAllText(Path.Combine(temp, "1.txt"), "x");
        });
        Assert.Equal(PathOf("pages 2"), result);
        Assert.True(File.Exists(Path.Combine(result, "1.txt")));
        Assert.Equal(2, Directory.GetFileSystemEntries(Dir).Length);
    }

    [Fact]
    public void FailedFolderIsRemoved()
    {
        Assert.Throws<InvalidOperationException>(() => OutputNaming.WriteAtomically(Dir, "x", "", temp =>
        {
            Directory.CreateDirectory(temp);
            throw new InvalidOperationException();
        }));
        Assert.Empty(Directory.GetFileSystemEntries(Dir));
    }

    [Fact]
    public void FoldersOfferArchives()
    {
        Directory.CreateDirectory(PathOf("folder"));
        Assert.Equal([FileFormat.Zip, FileFormat.Tar, FileFormat.Tgz], FormatCatalog.Targets([PathOf("folder")]));
    }
}

public class CatalogTests
{
    [Fact]
    public void PdfOffersImagesAndText() =>
        Assert.Equal([FileFormat.Png, FileFormat.Jpg, FileFormat.Txt, FileFormat.Md, FileFormat.Rtf, FileFormat.Docx, FileFormat.Html],
            FormatCatalog.Targets(["a.pdf"]));

    [Fact]
    public void DocumentsOfferEachOtherAndPdf() =>
        Assert.Equal([FileFormat.Pdf, FileFormat.Odt, FileFormat.Rtf, FileFormat.Txt, FileFormat.Md, FileFormat.Html],
            FormatCatalog.Targets(["a.docx"]));

    [Fact]
    public void ImagesOfferWebpAndPdf() => Assert.Equal(
        [FileFormat.Jpg, FileFormat.Webp, FileFormat.Tiff, FileFormat.Bmp, FileFormat.Gif, FileFormat.Pdf], FormatCatalog.Targets(["a.png"]));

    [Fact]
    public void LegacyDocIsNotConvertible() => Assert.Empty(FormatCatalog.Targets(["old.doc"]));

    [Fact]
    public void MixedKindsShareNothing() => Assert.Empty(FormatCatalog.Targets(["a.srt", "b.csv"]));

    [Fact]
    public void ImagesAndPdfShareImageFormats() =>
        // The PDF is already a PDF, so "PDF" converts only the TIFF.
        Assert.Equal([FileFormat.Png, FileFormat.Jpg, FileFormat.Pdf], FormatCatalog.Targets(["a.pdf", "b.tiff"]));
}
