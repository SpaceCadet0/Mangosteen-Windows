using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mangosteen.Conversion;
using Mangosteen.Core;
using Mangosteen.Core.Imaging;
using Mangosteen.Tools;
using UglyToad.PdfPig;

namespace Mangosteen.Windows.Tests;

public sealed class ToolTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mangosteen-tools").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string PathOf(string name) => Path.Combine(_dir, name);

    /// <summary>A noisy photo-like JPEG with a title and a GPS position.</summary>
    private string Photo(string name, int width = 800, int height = 600, bool gps = true)
    {
        var pixels = new byte[width * height * 4];
        var random = new Random(7);
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var x = i / 4 % width;
            var y = i / 4 / width;
            pixels[i] = (byte)(x * 255 / width);
            pixels[i + 1] = (byte)(y * 255 / height);
            pixels[i + 2] = (byte)random.Next(256);
            pixels[i + 3] = 255;
        }
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        var metadata = new BitmapMetadata("jpg") { Title = "Старое название", CameraModel = "Test camera" };
        if (gps)
        {
            // EXIF rationals: numerator in the low 32 bits, denominator in the high 32 bits.
            static ulong Rational(uint value) => value | (1UL << 32);
            metadata.SetQuery("/app1/ifd/gps/{ushort=1}", "N");
            metadata.SetQuery("/app1/ifd/gps/{ushort=2}", new[] { Rational(55), Rational(45), Rational(0) });
            metadata.SetQuery("/app1/ifd/gps/{ushort=3}", "E");
            metadata.SetQuery("/app1/ifd/gps/{ushort=4}", new[] { Rational(37), Rational(37), Rational(0) });
        }
        var encoder = new JpegBitmapEncoder { QualityLevel = 90 };
        encoder.Frames.Add(BitmapFrame.Create(new FormatConvertedBitmap(image, PixelFormats.Bgr24, null, 0), null, metadata, null));
        var path = PathOf(name);
        using var stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }

    private static byte[] Pixels(string path) => ImageIO.ToBgra(ImageConverter.Decode(path)).Pixels;

    [Fact]
    public void MetadataEditKeepsPixels()
    {
        var photo = Photo("фото.jpg");
        var output = MetadataEditor.Apply(photo, new MetadataChanges
        {
            Text = new() { [MetadataField.Description] = "Новое название", [MetadataField.Author] = "Иван" },
        });
        Assert.Equal(PathOf("фото (metadata).jpg"), output);
        var metadata = MetadataEditor.Read(output);
        Assert.Equal("Новое название", MetadataEditor.Text(MetadataField.Description, metadata));
        Assert.Equal("Иван", MetadataEditor.Text(MetadataField.Author, metadata));
        Assert.Equal(Pixels(photo), Pixels(output));
        Assert.Equal("Старое название", MetadataEditor.Text(MetadataField.Description, MetadataEditor.Read(photo)));
    }

    [Fact]
    public void RemoveLocationClearsGps()
    {
        var photo = Photo("gps.jpg");
        Assert.NotNull(MetadataEditor.Location(MetadataEditor.Read(photo)));
        var output = MetadataEditor.Apply(photo, new MetadataChanges { RemoveLocation = true });
        var metadata = MetadataEditor.Read(output);
        Assert.Null(MetadataEditor.Location(metadata));
        Assert.Null(metadata?.GetQuery("/app1/ifd/gps/{ushort=2}"));
        Assert.Equal("Test camera", MetadataEditor.Text(MetadataField.Model, metadata));
    }

    [Fact]
    public void RemoveAllIsLossless()
    {
        var photo = Photo("all.jpg");
        var output = MetadataEditor.Apply(photo, new MetadataChanges { RemoveAll = true });
        Assert.Equal(Pixels(photo), Pixels(output));
        Assert.DoesNotContain(MetadataEditor.Entries(MetadataEditor.Read(output)), e => e.Key.Contains("gps", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("", MetadataEditor.Text(MetadataField.Description, MetadataEditor.Read(output)));
    }

    [Fact]
    public void TargetSizeFits()
    {
        var photo = Photo("big.jpg", 1600, 1200);
        var settings = new CompressionSettings { Mode = CompressionMode.TargetSize, TargetBytes = 60_000, Format = FileFormat.Jpg };
        var (data, format) = Compression.CompressImage(photo, settings);
        Assert.Equal(FileFormat.Jpg, format);
        Assert.InRange(data.Length, 1, 60_000);
    }

    [Fact]
    public void CompressWritesASmallerCopy()
    {
        var photo = Photo("снимок.jpg", 1600, 1200);
        var output = Compression.CompressFile(photo, new CompressionSettings { Preset = CompressionPreset.Small }, CancellationToken.None);
        Assert.Equal(PathOf("снимок (compressed).jpg"), output);
        Assert.True(new FileInfo(output).Length < new FileInfo(photo).Length);
    }

    [Fact]
    public void PngIsCompressedToWebp()
    {
        var png = PathOf("screen.png");
        ImageConverter.WriteImage(ImageConverter.Decode(Photo("src.jpg", gps: false)), FileFormat.Png, png);
        var output = Compression.CompressFile(png, new CompressionSettings(), CancellationToken.None);
        Assert.EndsWith(".webp", output);
    }

    [Fact]
    public void PdfCompressionKeepsAValidPdf()
    {
        var pdf = Converter.CreatePdf([Photo("a.jpg", 2400, 1800, false), Photo("b.jpg", 2400, 1800, false)]);
        var data = Compression.CompressPdf(pdf, CancellationToken.None);
        Assert.True(data.Length < new FileInfo(pdf).Length);
        File.WriteAllBytes(PathOf("small.pdf"), data);
        using (var document = PdfDocument.Open(PathOf("small.pdf")))
        {
            Assert.Equal(2, document.NumberOfPages);
        }
        Assert.True(PdfRenderer.RenderPage(PathOf("small.pdf"), 1).PixelWidth > 100);
    }

    [Fact]
    public void PdfPagesReorderRotateAndDuplicate()
    {
        var pdf = Converter.CreatePdf([Photo("1.jpg", 200, 100, false), Photo("2.jpg", 100, 200, false)]);
        var output = PdfPagesTool.Save([new(0, 1, 90), new(0, 0, 0), new(0, 0, 180)], [pdf]);
        Assert.Equal(PathOf("Images (pages).pdf"), output);
        using var document = PdfDocument.Open(output);
        var pages = document.GetPages().ToList();
        Assert.Equal(3, pages.Count);
        Assert.Equal(90, pages[0].Rotation.Value);
        Assert.Equal(180, pages[2].Rotation.Value);
        Assert.Equal(75, pages[0].MediaBox.Bounds.Width, 1); // the second page (100 px at 96 dpi = 75 pt) came first
    }

    [Fact]
    public void BackgroundAndCollageSizes()
    {
        var photo = ImageConverter.Decode(Photo("p.jpg", 400, 300, false));
        var framed = Sta.Run(() => Compose.Frame(photo, new FrameSettings { Padding = 0.1, Background = FrameBackground.Blur }, null));
        Assert.Equal(480, framed.PixelWidth);
        Assert.Equal(380, framed.PixelHeight);
        var collage = Sta.Run(() => Compose.Collage([photo, photo, photo], new CollageSettings { Width = 1000 }, 0xFFFFFFFF));
        Assert.Equal(1000, collage.PixelWidth);
    }
}
