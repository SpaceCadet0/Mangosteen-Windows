using Mangosteen.Core;
using Mangosteen.Core.Imaging;
using Mangosteen.Core.Media;

namespace Mangosteen.Core.Tests;

public class MediaMathTests
{
    [Theory]
    [InlineData("75", 75)]
    [InlineData("1:15", 75)]
    [InlineData("0:01:15,5", 75.5)]
    [InlineData("1:00:00.250", 3600.25)]
    public void ParsesTimecodes(string text, double seconds) => Assert.Equal(seconds, MediaMath.ParseTimecode(text)!.Value, 6);

    [Theory]
    [InlineData("abc")]
    [InlineData("1:2:3:4")]
    [InlineData("-5")]
    public void RejectsBadTimecodes(string text) => Assert.Null(MediaMath.ParseTimecode(text));

    [Fact]
    public void FormatsTimecodes() => Assert.Equal("01:02:03.456", MediaMath.Timecode(3723.456));

    [Theory]
    [InlineData(1.5, new[] { "atempo=1.5" })]
    [InlineData(4, new[] { "atempo=2", "atempo=2" })]
    [InlineData(0.25, new[] { "atempo=0.5", "atempo=0.5" })]
    [InlineData(3, new[] { "atempo=2", "atempo=1.5" })]
    public void ChainsAtempo(double factor, string[] expected) => Assert.Equal(expected, MediaMath.AtempoChain(factor));

    [Fact]
    public void SplitsEvenly() => Assert.Equal([10.0, 20.0], MediaMath.EqualSplitPoints(30, 3));

    [Fact]
    public void SplitBoundsDropCutsAtTheEnds() => Assert.Equal([0.0, 5, 10], MediaMath.SplitBounds([5, 0.01, 9.99, 5], 10));

    [Fact]
    public void PeaksAreAbsoluteMaxima() => Assert.Equal([0.5f, 1f], MediaMath.Peaks([0.1f, -0.5f, 0.2f, 2f], 2));

    [Fact]
    public void BleepGraphMutesAndMixes()
    {
        var graph = MediaMath.BleepGraph([(1, 2), (3.5, 4)], 1000, 0.3, 10);
        Assert.Contains("between(t,1.000,2.000)+between(t,3.500,4.000)", graph);
        Assert.Contains("sine=frequency=1000", graph);
        Assert.EndsWith("[a]", graph);
    }

    [Fact]
    public void ChannelFilters()
    {
        Assert.Equal("pan=stereo|c0=1.00*c1|c1=0.50*c0", MediaMath.ChannelFilter(MediaMath.ChannelLayout.Stereo, 1, 0.5, true, 2));
        Assert.Equal("pan=mono|c0=0.50*c0+0.50*c0", MediaMath.ChannelFilter(MediaMath.ChannelLayout.Mono, 1, 1, false, 1));
    }

    [Fact]
    public void RedactionGraphUsesEvenPixels()
    {
        var graph = MediaMath.RedactionGraph([new MediaMath.VideoRegion(new UnitRect(0.1, 0.1, 0.25, 0.25), 1, 2)], RedactionStyle.Solid, 0xFF000000, 641, 481);
        Assert.StartsWith("[0:v]drawbox=x=64:y=48:w=160:h=120:color=0x000000@1:t=fill:enable='between(t,1.000,2.000)'[v0]", graph);
        Assert.EndsWith("[v0]format=yuv420p[v]", graph);
    }

    [Fact]
    public void TargetSizeBitrate()
    {
        var arguments = MediaMath.CompressionArguments(FileFormat.Mp4, 100, new MediaMath.MediaCompression(UseTargetSize: true, TargetBytes: 10_000_000));
        // 10 MB over 100 s = 800 kbit/s, minus 7 % and the 96 kbit/s audio.
        Assert.Contains("648k", arguments);
        Assert.Contains("+faststart", arguments);
    }

    [Fact]
    public void LosslessAudioCompressesToM4a() => Assert.Equal(FileFormat.M4a, MediaMath.CompressedFormat(FileFormat.Wav));
}

public sealed class MediaToolCatalogTests : IDisposable
{
    public void Dispose() => FormatCatalog.MediaAvailable = false;

    [Fact]
    public void MediaToolsNeedFFmpeg()
    {
        FormatCatalog.MediaAvailable = false;
        Assert.All(WheelCatalog.Items(WheelKind.Tools, ["clip.mp4"]), i => Assert.False(i.Available));
        FormatCatalog.MediaAvailable = true;
        var items = WheelCatalog.Items(WheelKind.Tools, ["clip.mp4", "b.mp4"]);
        Assert.Contains(items, i => i.Id == "tool.join" && i.Available);
        Assert.All(items, i => Assert.True(i.Available));
    }
}

public sealed class LanguageTests : IDisposable
{
    public void Dispose() => Loc.SetLanguage("en");

    [Theory]
    [InlineData(1, "1 файл")]
    [InlineData(2, "2 файла")]
    [InlineData(5, "5 файлов")]
    [InlineData(11, "11 файлов")]
    [InlineData(21, "21 файл")]
    [InlineData(22, "22 файла")]
    [InlineData(112, "112 файлов")]
    public void RussianPlurals(int count, string text)
    {
        Loc.SetLanguage("ru");
        Assert.Equal(text, WheelCatalog.FileCount(count));
    }

    [Fact]
    public void SpanishAndEnglishPlurals()
    {
        Loc.SetLanguage("es");
        Assert.Equal("1 archivo", WheelCatalog.FileCount(1));
        Assert.Equal("3 archivos", WheelCatalog.FileCount(3));
        Loc.SetLanguage("en");
        Assert.Equal("3 files", WheelCatalog.FileCount(3));
    }

    [Fact]
    public void TablesAreEmbeddedAndComplete()
    {
        var ru = Loc.Load("ru");
        var es = Loc.Load("es");
        Assert.True(ru.Count > 300);
        Assert.Equal(ru.Keys.Order(), es.Keys.Order());
        Loc.SetLanguage("ru");
        Assert.Equal("Обрезать", Loc.L("Crop"));
        Assert.Equal("Инструменты", WheelCatalog.Title(WheelKind.Tools));
        Assert.Contains(WheelCatalog.Items(WheelKind.Actions, ["a.png"]), i => i.Title == "Путь" || i.Title == "Копировать");
    }
}
