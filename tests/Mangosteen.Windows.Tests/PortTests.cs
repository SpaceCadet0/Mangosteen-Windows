using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mangosteen.Conversion;
using Mangosteen.Core;
using Mangosteen.Core.Imaging;
using Mangosteen.Media;
using Mangosteen.Tools;
using UglyToad.PdfPig;

namespace Mangosteen.Windows.Tests;

/// <summary>Features ported from the macOS app, on Windows itself.</summary>
public sealed class PortTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mangosteen-port").FullName;

    public PortTests()
    {
        FormatCatalog.MagickAvailable = true;
        FormatCatalog.MediaAvailable = FFmpeg.IsBundled;
    }

    public void Dispose()
    {
        FormatCatalog.MagickAvailable = false;
        FormatCatalog.MediaAvailable = false;
        FormatCatalog.SevenZipWritable = FormatCatalog.IsoWritable = false;
        TempCleanup.Delete(_dir);
    }

    private string PathOf(string name) => Path.Combine(_dir, name);

    private static BitmapSource Picture(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                var ink = x > width / 4 && x < width * 3 / 4 && y > height / 4 && y < height * 3 / 4;
                (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = ink ? ((byte)40, (byte)30, (byte)200, (byte)255) : ((byte)255, (byte)255, (byte)255, (byte)255);
            }
        }
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        image.Freeze();
        return image;
    }

    private string Png(string name, int width = 120, int height = 80)
    {
        var path = PathOf(name);
        ImageConverter.WriteImage(Picture(width, height), FileFormat.Png, path);
        return path;
    }

    [Fact]
    public void IcoHasSquareFramesUpToThePictureSize()
    {
        var ico = Converter.Convert(Png("logo.png", 120, 80), FileFormat.Ico)!;
        using var stream = File.OpenRead(ico);
        var decoder = new IconBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        Assert.Equal([16, 32, 48, 64], decoder.Frames.Select(f => f.PixelWidth).OrderBy(w => w));
        Assert.All(decoder.Frames, f => Assert.Equal(f.PixelWidth, f.PixelHeight));
    }

    [Theory]
    [InlineData("psd")]
    [InlineData("tga")]
    [InlineData("exr")]
    [InlineData("dds")]
    [InlineData("jxl")]
    [InlineData("dcm")]
    [InlineData("jp2")]
    [InlineData("dng")]
    public void ImageMagickReadsTheFormatsOffered(string extension) => Assert.True(MagickImaging.Reads(extension), extension);

    [Theory]
    [InlineData(FileFormat.Psd)]
    [InlineData(FileFormat.Tga)]
    [InlineData(FileFormat.Exr)]
    public void MagickFormatsRoundTrip(FileFormat format)
    {
        var output = Converter.Convert(Png("art.png"), format)!;
        Assert.Equal(format, FileFormats.Detect(output));
        var back = Converter.Convert(output, FileFormat.Png)!;
        var image = ImageConverter.Decode(back);
        Assert.Equal((120, 80), (image.PixelWidth, image.PixelHeight));
    }

    [Fact]
    public void SevenZipAndIsoAreWrittenAndReadWithTar()
    {
        FormatCatalog.SevenZipWritable = Archives.TarWrites("7zip");
        FormatCatalog.IsoWritable = Archives.TarWrites("iso9660");
        Assert.True(FormatCatalog.SevenZipWritable && FormatCatalog.IsoWritable, "tar.exe on the runner writes 7z and ISO");
        // tar.exe takes the names its ANSI code page can spell: Cyrillic on a Russian Windows, Latin-1 on the runner.
        var (folderName, fileName) = Archives.TarCanName("Фото заметка") ? ("Фото", "заметка.txt") : ("Café", "résumé.txt");
        var folder = Directory.CreateDirectory(PathOf(folderName)).FullName;
        File.WriteAllText(Path.Combine(folder, fileName), "привет");
        foreach (var format in new[] { FileFormat.SevenZip, FileFormat.Iso })
        {
            var archive = Converter.Convert(folder, format)!;
            var extracted = Archives.ExtractNextToArchive(archive);
            Assert.Equal("привет", File.ReadAllText(Directory.EnumerateFiles(extracted, fileName, SearchOption.AllDirectories).Single()));
        }
    }

    [SkippableFact]
    public void NamesTarCannotSpellAreRefusedWithAReason()
    {
        Skip.If(Archives.TarCanName("Фото"), "This Windows' code page spells Cyrillic.");
        FormatCatalog.SevenZipWritable = FormatCatalog.IsoWritable = true;
        var folder = Directory.CreateDirectory(PathOf("Photos")).FullName;
        File.WriteAllText(Path.Combine(folder, "заметка.txt"), "привет");
        foreach (var format in new[] { FileFormat.SevenZip, FileFormat.Iso })
        {
            var error = Assert.Throws<NotSupportedException>(() => Converter.Convert(folder, format));
            Assert.Contains("заметка.txt", error.Message);
            Assert.Contains("ZIP", error.Message);
        }
        Assert.Equal(["Photos"], Directory.EnumerateFileSystemEntries(_dir).Select(Path.GetFileName));
    }

    [Fact]
    public void BooksBecomePdf()
    {
        var md = PathOf("Книга.md");
        File.WriteAllText(md, "# Глава\n\nТекст книги.\n");
        var epub = Converter.Convert(md, FileFormat.Epub)!;
        var pdf = Converter.Convert(epub, FileFormat.Pdf)!;
        using var document = PdfDocument.Open(pdf);
        // Page.Text has no spaces between words; the words come apart by their positions.
        Assert.Contains("Текст книги", string.Join(" ", document.GetPages().SelectMany(p => p.GetWords()).Select(w => w.Text)));
    }

    [Fact]
    public void FourPagesGoOnOneSheet()
    {
        var images = Enumerable.Range(1, 5).Select(i => Png($"p{i}.png")).ToList();
        var pdf = Converter.CreatePdf(images);
        var output = PathOf("sheets.pdf");
        PagesPerSheetTool.Write(pdf, 4, frame: true, output);
        using var document = PdfDocument.Open(output);
        Assert.Equal(2, document.NumberOfPages);
    }

    [Fact]
    public void BlackAndWhiteTraceMakesAnSvg()
    {
        var image = Picture(200, 120);
        var bgra = ImageIO.ToBgra(image);
        var result = TraceTool.TraceWith(new VectorTracer.Pixels(bgra.Pixels, bgra.Width, bgra.Height), [], 200, 120, 1,
            new VectorTracer.Options(), CancellationToken.None);
        Assert.Equal(2, result.Layers.Count);
        Sta.Run(() =>
        {
            Assert.All(result.Layers, layer => TraceTool.Shape(layer));
            return 0;
        });
    }

    [SkippableFact]
    public void ColourTraceRunsVTracer()
    {
        Skip.IfNot(VTracer.IsBundled, "This build has no vtracer.");
        var image = Picture(200, 120);
        var bgra = ImageIO.ToBgra(image);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var png = new MemoryStream();
        encoder.Save(png);
        var result = VTracer.Trace(new VectorTracer.Pixels(bgra.Pixels, bgra.Width, bgra.Height), png.ToArray(), 200, 120,
            new VectorTracer.Options(Colors: 2), CancellationToken.None);
        Assert.NotEmpty(result.Layers);
        // The ink of Picture is BGRA (40, 30, 200): red.
        Assert.Contains(result.Layers, l => l.Color.R > 0.6 && l.Color.B < 0.3);
        Sta.Run(() =>
        {
            Assert.All(result.Layers, layer => TraceTool.Shape(layer));
            return 0;
        });
    }

    [Fact]
    public void PdfTagsAreWrittenToACopy()
    {
        var pdf = Converter.CreatePdf([Png("a.png"), Png("b.png")]);
        var output = FileTags.Write(pdf, new Dictionary<FileTags.Field, string> { [FileTags.Field.Title] = "Отчёт", [FileTags.Field.Keywords] = "a, b" },
            removeAll: false, null, CancellationToken.None);
        var tags = FileTags.Read(output);
        Assert.Equal("Отчёт", tags[FileTags.Field.Title]);
        Assert.Equal("a, b", tags[FileTags.Field.Keywords]);
        Assert.False(FileTags.Read(pdf).ContainsKey(FileTags.Field.Title));
    }

    [SkippableFact]
    public void MediaTagsAreWrittenWithoutReencoding()
    {
        Skip.IfNot(FFmpeg.IsBundled, "This build has no FFmpeg.");
        var song = PathOf("song.mp3");
        FFmpeg.Encode(["-f", "lavfi", "-i", "sine=frequency=440:duration=1", "-c:a", "libmp3lame", "-metadata", "genre=Old"], song, 1, null, CancellationToken.None);
        var output = FileTags.Write(song, new Dictionary<FileTags.Field, string> { [FileTags.Field.Title] = "Песня" }, removeAll: false, null, CancellationToken.None);
        var tags = FileTags.Read(output);
        Assert.Equal("Песня", tags[FileTags.Field.Title]);
        Assert.Equal("Old", tags[FileTags.Field.Genre]);
        var cleared = FileTags.Write(song, new Dictionary<FileTags.Field, string>(), removeAll: true, null, CancellationToken.None);
        Assert.False(FileTags.Read(cleared).ContainsKey(FileTags.Field.Genre));
    }

    [SkippableFact]
    public void BlackBarsAreFoundAndCut()
    {
        Skip.IfNot(FFmpeg.IsBundled, "This build has no FFmpeg.");
        var clip = PathOf("letterbox.mp4");
        FFmpeg.Encode(["-f", "lavfi", "-i", "testsrc2=size=320x180:rate=25:duration=2", "-vf", "pad=320:240:0:30:black",
            "-c:v", "libx264", "-pix_fmt", "yuv420p"], clip, 2, null, CancellationToken.None);
        var crop = VideoFixes.BlackBarsCrop(clip, CancellationToken.None);
        Assert.NotNull(crop);
        Assert.InRange(crop.Value.Height, 176, 184);
        var output = VideoFixes.RemoveBlackBars(clip, _ => { }, CancellationToken.None);
        var info = MediaConverter.Probe(output);
        Assert.InRange(info.Height ?? 0, 176, 184);
    }

    [SkippableFact]
    public void SdrVideoIsNotTakenForHdr()
    {
        Skip.IfNot(FFmpeg.IsBundled, "This build has no FFmpeg.");
        var clip = PathOf("sdr.mp4");
        FFmpeg.Encode(["-f", "lavfi", "-i", "testsrc2=size=160x120:rate=25:duration=1", "-c:v", "libx264", "-pix_fmt", "yuv420p"], clip, 1, null, CancellationToken.None);
        Assert.Throws<ShownException>(() => VideoFixes.HdrToSdr(clip, _ => { }, CancellationToken.None));
    }

    [SkippableFact]
    public void SubtitlesComeOutOfAVideo()
    {
        Skip.IfNot(FFmpeg.IsBundled, "This build has no FFmpeg.");
        var srt = PathOf("sub.srt");
        File.WriteAllText(srt, "1\n00:00:00,100 --> 00:00:01,000\nПривет\n");
        var clip = PathOf("with subs.mkv");
        FFmpeg.Encode(["-f", "lavfi", "-i", "testsrc2=size=160x120:rate=25:duration=1", "-i", srt, "-map", "0:v", "-map", "1:s",
            "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:s", "srt"], clip, 1, null, CancellationToken.None);
        var output = VideoFixes.ExtractSubtitles(clip, _ => { }, CancellationToken.None);
        Assert.EndsWith(".srt", output);
        Assert.Contains("Привет", File.ReadAllText(output));
    }

    [SkippableFact]
    public void HevcIsChosenFromSettings()
    {
        Skip.IfNot(FFmpeg.IsBundled, "This build has no FFmpeg.");
        var saved = Core.Media.VideoEncoding.Current;
        try
        {
            Core.Media.VideoEncoding.Current = new Core.Media.VideoEncoding(Core.Media.VideoCodec.Hevc);
            var clip = PathOf("clip.avi");
            FFmpeg.Encode(["-f", "lavfi", "-i", "testsrc2=size=160x120:rate=25:duration=1", "-c:v", "mpeg4"], clip, 1, null, CancellationToken.None);
            var output = Converter.Convert(clip, FileFormat.Mp4)!;
            Assert.Equal("hevc", MediaConverter.Probe(output).VideoCodec);
        }
        finally
        {
            Core.Media.VideoEncoding.Current = saved;
        }
    }
}

public sealed class AnimatedGifTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mangosteen-gif").FullName;

    public void Dispose() => TempCleanup.Delete(_dir);

    [SkippableFact]
    public void AnimatedGifStaysAnimated()
    {
        Skip.IfNot(FFmpeg.IsBundled, "This build has no FFmpeg.");
        var gif = Path.Combine(_dir, "anim.gif");
        FFmpeg.Encode(["-f", "lavfi", "-i", "testsrc2=size=240x160:rate=10:duration=2"], gif, 2, null, CancellationToken.None);
        Assert.True(Compression.IsAnimated(gif));
        var output = Compression.CompressFile(gif, new Core.Imaging.CompressionSettings(), CancellationToken.None);
        Assert.EndsWith(".webp", output);
        // An animated WebP carries an ANIM chunk.
        Assert.Contains("ANIM", System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(output)[..64]));
    }
}
