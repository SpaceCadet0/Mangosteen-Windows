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
        Assert.Equal([FileFormat.Jpg, FileFormat.Webp, FileFormat.Tiff, FileFormat.Bmp, FileFormat.Gif, FileFormat.Pdf],
            FormatCatalog.Targets(["a.png"]));

    [Fact]
    public void MixedImagesOfferEveryFormatSomeItemNeeds() =>
        Assert.Equal([FileFormat.Png, FileFormat.Jpg, FileFormat.Webp, FileFormat.Tiff, FileFormat.Bmp, FileFormat.Gif, FileFormat.Pdf],
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
        Assert.Equal(["tool.compress", "tool.crop", "tool.edit", "tool.annotate", "tool.redact", "tool.frame",
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
