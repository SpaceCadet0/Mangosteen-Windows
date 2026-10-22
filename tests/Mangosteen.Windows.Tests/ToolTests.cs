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

    public void Dispose() => TempCleanup.Delete(_dir);

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
    public void PdfSplitWritesPartsInAFolder()
    {
        var pdf = Converter.CreatePdf([Photo("1.jpg", 200, 100, false), Photo("2.jpg", 100, 200, false), Photo("3.jpg", 100, 100, false)]);
        var folder = PdfSplitTool.Save(PdfSplit.Documents(3, [0], [2]), pdf);
        Assert.Equal(PathOf("Images (parts)"), folder);
        Assert.Equal(["Images 1.pdf", "Images 2.pdf"], Directory.GetFiles(folder).Select(Path.GetFileName).Order());
        using (var second = PdfDocument.Open(Path.Combine(folder, "Images 2.pdf")))
        {
            Assert.Equal(1, second.NumberOfPages);
        }
        var single = PdfSplitTool.Save(PdfSplit.Documents(3, [], [0]), pdf);
        Assert.Equal(PathOf("Images (pages).pdf"), single);
        using var document = PdfDocument.Open(single);
        Assert.Equal(2, document.NumberOfPages);
    }

    private string Encoded(string name, PixelFormat format, BitmapEncoder encoder, bool profile = false)
    {
        var (width, height) = (4, 2);
        var stride = (width * format.BitsPerPixel + 7) / 8;
        var pixels = new byte[stride * height];
        new Random(3).NextBytes(pixels);
        var image = BitmapSource.Create(width, height, 96, 96, format, null, pixels, stride);
        var colors = profile ? new System.Collections.ObjectModel.ReadOnlyCollection<ColorContext>([new ColorContext(PixelFormats.Bgr24)]) : null;
        encoder.Frames.Add(BitmapFrame.Create(image, null, null, colors));
        using (var stream = File.Create(PathOf(name)))
        {
            encoder.Save(stream);
        }
        return PathOf(name);
    }

    private static BitmapFrame Open(string path)
    {
        using var stream = File.OpenRead(path);
        return BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad).Frames[0];
    }

    private static string Turn(string path) =>
        CropTool.CropFile(path, new ImageTransform().Rotate(clockwise: true), (_, _) => UnitRect.Full, null, [], []);

    [Fact]
    public void TurningKeeps16Bits()
    {
        var output = Turn(Encoded("deep.png", PixelFormats.Rgba64, new PngBitmapEncoder()));
        Assert.Equal(PathOf("deep (changed).png"), output);
        var frame = Open(output);
        Assert.Equal((2, 4), (frame.PixelWidth, frame.PixelHeight));
        Assert.True(frame.Format.BitsPerPixel >= 48, $"pixel format {frame.Format}");
    }

    [Fact]
    public void TurningKeepsGreyAndCmyk()
    {
        Assert.Equal(PixelFormats.Gray8, Open(Turn(Encoded("grey.jpg", PixelFormats.Gray8, new JpegBitmapEncoder()))).Format);
        Assert.Equal(PixelFormats.Cmyk32, Open(Turn(Encoded("print.tif", PixelFormats.Cmyk32, new TiffBitmapEncoder()))).Format);
    }

    [Fact]
    public void TurningKeepsTheColourProfile()
    {
        var input = Encoded("profiled.jpg", PixelFormats.Bgr24, new JpegBitmapEncoder(), profile: true);
        var before = Open(input).ColorContexts?.Count ?? 0;
        Assert.Equal(before, Open(Turn(input)).ColorContexts?.Count ?? 0);
    }

    private string TextPage(string name, string text)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(Brushes.White, null, new System.Windows.Rect(0, 0, 1400, 300));
            context.DrawText(new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture, System.Windows.FlowDirection.LeftToRight,
                new Typeface("Arial"), 72, Brushes.Black, 1), new System.Windows.Point(40, 100));
        }
        var bitmap = new RenderTargetBitmap(1400, 300, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(PathOf(name));
        encoder.Save(stream);
        return PathOf(name);
    }

    [Fact]
    public void TextIsRecognisedFromImagesAndPdfPages()
    {
        if (global::Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages() is null)
        {
            return; // no recognition language on this machine
        }
        var (first, second) = Sta.Run(() => (TextPage("first.png", "Mangosteen fruit"), TextPage("second.png", "Second page")));
        var output = TextRecognition.RecognizeFile(first, CancellationToken.None);
        Assert.Equal(PathOf("first (text).txt"), output);
        Assert.Contains("Mangosteen", File.ReadAllText(output));
        var pdf = Converter.CreatePdf([first, second]);
        var text = File.ReadAllText(TextRecognition.RecognizeFile(pdf, CancellationToken.None));
        Assert.Contains("\f", text);
        Assert.Contains("Second", text);
        var blank = Sta.Run(() => TextPage("blank.png", " "));
        var error = Assert.Throws<ShownException>(() => TextRecognition.RecognizeFile(blank, CancellationToken.None));
        Assert.Contains("blank.png", error.Message);
    }

    private static string RepositoryFile(string relative)
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "Mangosteen.sln")))
        {
            folder = folder.Parent;
        }
        return Path.Combine(folder!.FullName, relative);
    }

    [Fact]
    public void BackgroundIsRemovedAroundTheSubject()
    {
        if (!BackgroundRemoval.IsBundled)
        {
            return; // built without the model (build/fetch-isnet.ps1 did not run)
        }
        var photo = PathOf("mangosteen.jpg");
        File.Copy(RepositoryFile("docs/images/source/mangosteen-video.jpg"), photo);
        var output = RemoveBackgroundTool.RemoveFile(photo, null, null, crop: false);
        Assert.Equal(PathOf("mangosteen (no background).png"), output);
        var result = ImageIO.ToBgra(ImageIO.Load(output));
        var source = ImageIO.ToBgra(ImageIO.Load(photo));
        Assert.Equal((source.Width, source.Height), (result.Width, result.Height));
        byte Alpha(double x, double y) => result.Pixels[((int)(y * result.Height) * result.Width + (int)(x * result.Width)) * 4 + 3];
        Assert.True(Alpha(0.02, 0.02) < 30, "the corner is background");
        Assert.True(Alpha(0.5, 0.6) > 220, "the middle fruit is subject");

        var onWhite = RemoveBackgroundTool.RemoveFile(photo, null, 0xFFFFFFFF, crop: true);
        Assert.Equal(PathOf("mangosteen (no background).jpg"), onWhite);
        var cropped = ImageIO.Load(onWhite);
        Assert.True(cropped.PixelWidth < source.Width && cropped.PixelHeight < source.Height);

        var blank = PathOf("blank.png");
        var pixels = Enumerable.Repeat((byte)180, 400 * 300 * 4).ToArray();
        File.WriteAllBytes(blank, ImageIO.Encode(BitmapSource.Create(400, 300, 96, 96, PixelFormats.Bgra32, null, pixels, 1600), FileFormat.Png));
        var error = Assert.Throws<ShownException>(() => RemoveBackgroundTool.RemoveFile(blank, null, null, crop: false));
        Assert.Contains("blank.png", error.Message);
    }

    [Fact]
    public void LockedPdfSaysSo()
    {
        var message = Jobs.Describe(new InvalidDataException("a.pdf is protected with a password."), "a.pdf");
        Assert.Equal("“a.pdf” is locked with a password.", message);
        Assert.Equal("b.txt: broken", Jobs.Describe(new IOException("broken"), "b.txt"));
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

public sealed class AnnotationTests
{
    [Fact]
    public void EveryKindRendersAtFullSize()
    {
        var photo = BitmapSource.Create(400, 300, 96, 96, PixelFormats.Bgra32, null, new byte[400 * 300 * 4], 1600);
        photo.Freeze();
        System.Windows.Point P(double x, double y) => new(x, y);
        var marks = new List<Annotation>
        {
            new(AnnotationKind.Arrow, [P(10, 10), P(200, 150)], 0xFFE5484D, 4, 20),
            new(AnnotationKind.Rectangle, [P(20, 20), P(120, 80)], 0xFF3E8ED0, 3, 20),
            new(AnnotationKind.Ellipse, [P(150, 20), P(250, 90)], 0xFF46A758, 3, 20),
            new(AnnotationKind.Text, [P(30, 200)], 0xFFFFFFFF, 3, 24) { Text = "Привет" },
            new(AnnotationKind.Number, [P(300, 200)], 0xFF671E38, 3, 20) { Number = 1 },
            new(AnnotationKind.Pen, [P(10, 280), P(50, 260), P(90, 290)], 0xFF000000, 3, 20),
            new(AnnotationKind.Highlight, [P(100, 250), P(200, 250)], 0xFFF5C542, 3, 20),
            new(AnnotationKind.Magnifier, [P(320, 80), P(360, 80)], 0, 3, 20),
            new(AnnotationKind.Picture, [P(250, 220), P(290, 260)], 0, 0, 0) { Picture = photo },
        };
        var result = Sta.Run(() => AnnotationDrawing.Render(photo, marks));
        Assert.Equal(400, result.PixelWidth);
        Assert.Equal(300, result.PixelHeight);
        // The arrow is drawn: the transparent photo got opaque pixels.
        Assert.Contains(ImageIO.ToBgra(result).Pixels.Where((_, i) => i % 4 == 3), a => a > 200);
    }
}
