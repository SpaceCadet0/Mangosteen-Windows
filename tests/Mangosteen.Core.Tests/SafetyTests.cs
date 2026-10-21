using System.Buffers.Binary;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using Mangosteen.Core;
using Mangosteen.Core.Documents;

namespace Mangosteen.Core.Tests;

/// <summary>Crafted archives and documents must not fill the memory, write outside their folder or reach out of it.</summary>
public class SafetyTests : TempFolderTest
{
    /// <summary>Rewrites the unpacked size of <paramref name="entryName"/> in its local header and the central directory.</summary>
    private static void PatchUnpackedSize(string zip, string entryName, uint size)
    {
        var bytes = File.ReadAllBytes(zip);
        var name = Encoding.UTF8.GetBytes(entryName);
        var patched = 0;
        for (var i = 0; i + 46 < bytes.Length; i++)
        {
            var signature = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i));
            var (sizeAt, nameLengthAt, nameAt) = signature switch
            {
                0x04034b50 => (22, 26, 30),
                0x02014b50 => (24, 28, 46),
                _ => (-1, 0, 0),
            };
            if (sizeAt < 0)
            {
                continue;
            }
            var length = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(i + nameLengthAt));
            if (length == name.Length && i + nameAt + length <= bytes.Length && bytes.AsSpan(i + nameAt, length).SequenceEqual(name))
            {
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i + sizeAt), size);
                patched++;
            }
        }
        Assert.Equal(2, patched);
        File.WriteAllBytes(zip, bytes);
    }

    private string Workbook(string name)
    {
        var path = PathOf(name);
        Tables.WriteXlsx([["a", "b"], ["1", "2"]], "Sheet1", path);
        return path;
    }

    private static string SheetEntry(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        return zip.Entries.First(e => e.FullName.StartsWith("xl/worksheets/", StringComparison.Ordinal)).FullName;
    }

    [Fact]
    public void WorkbookDeclaringAHugePartIsRefusedBeforeReading()
    {
        var path = Workbook("bomb.xlsx");
        PatchUnpackedSize(path, SheetEntry(path), 300u << 20);
        var error = Assert.Throws<InvalidDataException>(() => Tables.ReadXlsx(path));
        Assert.Contains("too large", error.Message);
    }

    [Fact]
    public void WorkbookUnderstatingAPartStopsAtTheDeclaredSize()
    {
        var path = Workbook("liar.xlsx");
        PatchUnpackedSize(path, SheetEntry(path), 10);
        Assert.ThrowsAny<Exception>(() => Tables.ReadXlsx(path));
    }

    [Fact]
    public void WorkbookPicturesDoNotCount()
    {
        var path = Workbook("pictures.xlsx");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            zip.CreateEntry("xl/media/image1.png");
        }
        PatchUnpackedSize(path, "xl/media/image1.png", 900u << 20);
        Assert.Equal("1", Tables.ReadXlsx(path)[0].Rows[1][0]);
    }

    [Theory]
    [InlineData(1, "1900-01-01")]
    [InlineData(59, "1900-02-28")]
    [InlineData(60, "1900-02-29")]
    [InlineData(61, "1900-03-01")]
    [InlineData(45000, "2023-03-15")]
    [InlineData(45000.5, "2023-03-15 12:00:00")]
    [InlineData(0.25, "06:00:00")]
    public void ExcelDatesIn1900System(double serial, string expected) => Assert.Equal(expected, Tables.ExcelDate(serial));

    [Fact]
    public void ExcelDatesIn1904System() => Assert.Equal("1904-01-02", Tables.ExcelDate(1, date1904: true));

    [Fact]
    public void ZipEntriesCannotLeaveTheFolder()
    {
        var archive = PathOf("slip.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(zip.CreateEntry("../evil.txt").Open());
            writer.Write("x");
        }
        var folder = PathOf("out");
        Assert.ThrowsAny<IOException>(() => Archives.Extract(archive, FileFormat.Zip, folder));
        Assert.False(File.Exists(PathOf("evil.txt")));
    }

    [Fact]
    public void TarLinksCannotPointOutOfTheFolder()
    {
        var archive = PathOf("links.tar");
        using (var stream = File.Create(archive))
        using (var tar = new TarWriter(stream))
        {
            tar.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "escape") { LinkName = "../../outside" });
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "../evil.txt") { DataStream = new MemoryStream([1]) });
        }
        var folder = PathOf("out");
        Assert.ThrowsAny<IOException>(() => Archives.Extract(archive, FileFormat.Tar, folder));
        Assert.False(File.Exists(PathOf("evil.txt")));
        Assert.False(Directory.Exists(Path.Combine(folder, "escape")) || File.Exists(Path.Combine(folder, "escape")));
    }

    [Fact]
    public void HtmlImportLoadsNothingFromOutside()
    {
        var html = """
            <html><head><base href="file:///C:/"><link rel="stylesheet" href="../secret.css">
            <meta http-equiv="refresh" content="0; url=https://example.com"><style>@import url(../x.css); p { background: url(file:///etc/passwd) }</style></head>
            <body><p>Before</p><img src="file:///etc/passwd"><img src="../../photo.jpg" srcset="https://example.com/a.png 2x">
            <video poster="https://example.com/p.png"></video><p>After</p></body></html>
            """;
        File.WriteAllText(PathOf("page.html"), html);
        DocumentConverter.Convert(PathOf("page.html"), FileFormat.Html, FileFormat.Docx, PathOf("page.docx"));
        using var zip = ZipFile.OpenRead(PathOf("page.docx"));
        Assert.DoesNotContain(zip.Entries, e => e.FullName.StartsWith("word/media/", StringComparison.Ordinal));
        var text = string.Join("\n", HtmlFormat.Parse(html).Blocks.Select(b => b.Text));
        Assert.Contains("Before", text);
        Assert.Contains("After", text);
        Assert.DoesNotContain("passwd", text);
    }
}
