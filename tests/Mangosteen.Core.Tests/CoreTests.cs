using Mangosteen.Core;

namespace Mangosteen.Core.Tests;

public class FileFormatTests
{
    [Theory]
    [InlineData("photo.PNG", FileFormat.Png)]
    [InlineData("photo.jpeg", FileFormat.Jpg)]
    [InlineData("scan.tif", FileFormat.Tiff)]
    [InlineData("backup.tar.gz", FileFormat.Tgz)]
    [InlineData("song.aif", FileFormat.Aiff)]
    public void DetectsByExtension(string name, FileFormat expected) =>
        Assert.Equal(expected, FileFormats.Detect(Path.Combine("C:", "x", name)));

    [Theory]
    [InlineData("README")]
    [InlineData("notes.xyz")]
    [InlineData("folder.")]
    public void UnknownIsNull(string name) => Assert.Null(FileFormats.Detect(name));
}

public class FormatCatalogTests
{
    [Fact]
    public void PngOffersOtherImageFormats() =>
        Assert.Equal([FileFormat.Jpg, FileFormat.Webp, FileFormat.Tiff, FileFormat.Gif, FileFormat.Pdf],
            FormatCatalog.Targets(["a.png"]));

    [Fact]
    public void BmpIsReadAndEditedButNotOffered()
    {
        Assert.Equal([FileFormat.Png, FileFormat.Jpg, FileFormat.Webp, FileFormat.Tiff, FileFormat.Gif, FileFormat.Pdf],
            FormatCatalog.Targets(["a.bmp"]));
        Assert.DoesNotContain(FileFormat.Bmp, FormatCatalog.Targets(["a.jpg", "b.mp4", "c.png"]));
        Assert.Contains(FileFormat.Bmp, FormatCatalog.EditableImageFormats);
    }

    [Fact]
    public void MixedImagesOfferEveryFormatSomeItemNeeds() =>
        Assert.Equal([FileFormat.Png, FileFormat.Jpg, FileFormat.Webp, FileFormat.Tiff, FileFormat.Gif, FileFormat.Pdf],
            FormatCatalog.Targets(["a.png", "b.jpg"]));

    [Fact]
    public void ReadOnlyInputsCanBeConverted() =>
        Assert.Contains(FileFormat.Png, FormatCatalog.Targets(["icon.ico", "photo.heic"]));

    [Fact]
    public void UnsupportedFilesAreIgnored() =>
        Assert.Equal(FormatCatalog.Targets(["a.png"]), FormatCatalog.Targets(["a.png", "clip.mp4", "no-such-folder"]));

    [Fact]
    public void NothingConvertibleGivesNothing() => Assert.Empty(FormatCatalog.Targets(["song.mp3", "clip.mp4", "old.doc"]));
}

public class WheelCatalogTests
{
    [Fact]
    public void ConvertItemsCarryTheirTarget()
    {
        var items = WheelCatalog.Items(WheelKind.Convert, ["a.png"]);
        Assert.Equal("JPG", items[0].Title);
        Assert.Equal(FileFormat.Jpg, items[0].Target);
        Assert.All(items, i => Assert.True(i.Available));
    }

    [Fact]
    public void ImageToolsMatchMacOrder()
    {
        var ids = WheelCatalog.Items(WheelKind.Tools, ["a.png", "b.jpg"]).Select(i => i.Id);
        Assert.Equal(["tool.compress", "tool.crop", "tool.edit", "tool.annotate", "tool.redact", "tool.recognizeText", "tool.frame", "tool.removeBackground",
            "tool.collage", "tool.metadata", "tool.createPDF"], ids);
    }

    [Fact]
    public void ActionsAreTheSameForAnyFile() =>
        Assert.Equal(WheelCatalog.Items(WheelKind.Actions, ["a.png"]), WheelCatalog.Items(WheelKind.Actions, ["b.mp4"]));

    [Theory]
    [InlineData(1, "1 file")]
    [InlineData(3, "3 files")]
    public void FileCount(int count, string text) => Assert.Equal(text, WheelCatalog.FileCount(count));
}

public sealed class OutputNamingTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mangosteen-tests").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void FirstFreeNameIsPlain() =>
        Assert.Equal(Path.Combine(_dir, "photo.jpg"), OutputNaming.UniquePath(_dir, "photo", "jpg"));

    [Fact]
    public void TakenNamesGetANumber()
    {
        File.WriteAllText(Path.Combine(_dir, "photo.jpg"), "");
        File.WriteAllText(Path.Combine(_dir, "photo 2.jpg"), "");
        Assert.Equal(Path.Combine(_dir, "photo 3.jpg"), OutputNaming.UniquePath(_dir, "photo", "jpg"));
    }

    [Theory]
    [InlineData("photo.png", "photo")]
    [InlineData("backup.tar.gz", "backup")]
    [InlineData("my.photo.png", "my.photo")]
    public void BaseNameDropsTheExtension(string name, string expected) =>
        Assert.Equal(expected, OutputNaming.BaseName(Path.Combine(_dir, name)));

    [Fact]
    public void WriteAtomicallyNeverOverwrites()
    {
        var existing = Path.Combine(_dir, "photo.jpg");
        File.WriteAllText(existing, "original");
        var result = OutputNaming.WriteAtomically(_dir, "photo", "jpg", temp => File.WriteAllText(temp, "new"));
        Assert.Equal(Path.Combine(_dir, "photo 2.jpg"), result);
        Assert.Equal("original", File.ReadAllText(existing));
        Assert.Equal("new", File.ReadAllText(result));
    }

    [Fact]
    public void WriteAtomicallyLeavesNothingOnFailure()
    {
        Assert.Throws<InvalidOperationException>(() =>
            OutputNaming.WriteAtomically(_dir, "photo", "jpg", temp =>
            {
                File.WriteAllText(temp, "partial");
                throw new InvalidOperationException();
            }));
        Assert.Empty(Directory.GetFileSystemEntries(_dir));
    }
}

public class ResultHistoryTests : TempFolderTest
{
    [Fact]
    public void KeepsTheNewestAndForgetsWhatIsGone()
    {
        var file = PathOf("history.json");
        var history = ResultHistory.Load(file);
        File.WriteAllText(PathOf("a.txt"), "a");
        for (var i = 0; i < 25; i++)
        {
            history.Add($"Job {i}", [PathOf(i % 2 == 0 ? "a.txt" : "gone.txt")], new DateTime(2026, 1, 1).AddMinutes(i));
        }
        Assert.Equal(ResultHistory.Kept, ResultHistory.Load(file).Entries.Count);
        var live = history.Live();
        Assert.Equal("Job 24", live[0].Title);
        Assert.All(live, e => Assert.Equal([PathOf("a.txt")], e.Outputs));
        Assert.Equal(10, ResultHistory.Load(file).Entries.Count);
        history.Remove(live[0]);
        Assert.Equal("Job 22", ResultHistory.Load(file).Live()[0].Title);
        history.Clear();
        Assert.Empty(ResultHistory.Load(file).Entries);
    }
}

public class MixedSelectionTests : TempFolderTest
{
    [Fact]
    public void MixedSelectionsGetTheCommonTools()
    {
        Assert.Equal(["tool.compressAll", "tool.metadata", "tool.recognizeText", "tool.archive"],
            WheelCatalog.Items(WheelKind.Tools, ["a.jpg", "b.pdf"]).Select(i => i.Id));
        Assert.Equal(["tool.compressAll", "tool.metadata", "tool.archive"],
            WheelCatalog.Items(WheelKind.Tools, ["a.jpg", "b.mp4"]).Select(i => i.Id));
        Assert.Equal(["tool.archive"], WheelCatalog.Items(WheelKind.Tools, ["a.docx", "b.csv"]).Select(i => i.Id));
        Assert.Contains("tool.crop", WheelCatalog.Items(WheelKind.Tools, ["a.jpg", "b.png", "c.webp"]).Select(i => i.Id));
    }

    [Fact]
    public void ArchiveIsNamedAfterTheSharedFolder()
    {
        var photos = Directory.CreateDirectory(PathOf("Photos")).FullName;
        var inner = Directory.CreateDirectory(Path.Combine(photos, "Trip")).FullName;
        File.WriteAllText(Path.Combine(photos, "a.txt"), "a");
        File.WriteAllText(Path.Combine(inner, "a.txt"), "b");
        Assert.Equal("Photos", Archives.BundleName([Path.Combine(photos, "a.txt"), inner]));
        Assert.Equal("Photos", Archives.BundleName([Path.Combine(photos, "a.txt"), Path.Combine(inner, "a.txt")]));
        var first = Archives.AddToArchive([Path.Combine(photos, "a.txt"), Path.Combine(inner, "a.txt"), inner]);
        Assert.Equal(Path.Combine(photos, "Photos.zip"), first);
        using (var zip = System.IO.Compression.ZipFile.OpenRead(first))
        {
            Assert.Equal(["Trip/a.txt", "a 2.txt", "a.txt"], zip.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal));
        }
        Assert.Equal(Path.Combine(photos, "a.zip"), Archives.AddToArchive([Path.Combine(photos, "a.txt")]));
        Assert.Equal(Path.Combine(photos, "Photos 2.zip"), Archives.AddToArchive([Path.Combine(photos, "a.txt"), inner]));
    }
}

public class LocContextTests
{
    [Fact]
    public void ContextIsDroppedInEnglish()
    {
        // English only: other tests run in parallel and expect English strings.
        Assert.Equal("Crop", Loc.L("Crop|tab"));
        Assert.Equal("Обрезка", Loc.Load("ru")["Crop|tab"]);
    }
}
